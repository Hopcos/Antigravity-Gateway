using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AntigravityGateway.Configuration;
using AntigravityGateway.Models.Gemini;

namespace AntigravityGateway.Services;

/// <summary>
/// Antigravity 原生 Provider：
/// 直接代理 Antigravity 登录账号与 daily-cloudcode-pa / cloudcode-pa 上游，
/// 采用 aicode-consumers 项目，支持全部 Antigravity 模型（Gemini 3.8 Flash, Gemini 2.5 Pro, Claude Sonnet, etc.）。
/// 包含四层高可用保障：
/// 1. 按模型独立并发控制与自适应冷却（解决全局互锁问题）；
/// 2. 智能 HTTP Retry-After 解析与抖动指数退避；
/// 3. 上游 429 配额耗尽 (RESOURCE_EXHAUSTED) / 503 容量过载时的自动模型故障转移 (Model Fallback)；
/// 4. 多账号池协同轮换。
/// </summary>
public sealed class AntigravityProvider : IGeminiProvider
{
    private static readonly Dictionary<string, string[]> DefaultFallbackChains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-4-6"] = ["gemini-3.8-flash-high", "claude-opus-4-6-thinking", "gemini-2.5-flash"],
        ["claude-opus-4-6-thinking"] = ["claude-sonnet-4-6", "gemini-3.8-flash-high", "gemini-2.5-flash"],
        ["gemini-3.8-flash-high"] = ["gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash"],
        ["gemini-3.8-flash"] = ["gemini-3.8-flash-high", "gemini-3.5-flash-lite", "gemini-2.5-flash"],
        ["gemini-3.8-flash-medium"] = ["gemini-3.8-flash", "gemini-2.5-flash"],
        ["gemini-3.8-flash-low"] = ["gemini-3.8-flash", "gemini-2.5-flash"],
        ["gemini-2.5-pro"] = ["gemini-3.8-flash-high", "gemini-3.8-flash", "gemini-2.5-flash"],
        ["gemini-2.5-flash"] = ["gemini-3.5-flash-lite", "gemini-2.5-flash-lite"],
        ["gpt-oss-120b-medium"] = ["gemini-3.8-flash-high", "gemini-2.5-flash"],
        ["gpt-4o"] = ["gemini-3.8-flash-high", "gemini-2.5-flash"]
    };

    private readonly ProviderOptions _opts;
    private readonly GatewayOptions? _gatewayOpts;
    private readonly AntigravityAccountService _account;
    private readonly HttpClient _http;
    private readonly ILogger<AntigravityProvider> _log;

    // 按模型独立的并发控制闸（避免一个模型占满全局阻塞其他可用模型）
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelGates = new(StringComparer.OrdinalIgnoreCase);

    // 按模型独立的限流冷却
    private readonly ConcurrentDictionary<string, DateTimeOffset> _modelCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int resetAfter, DateTimeOffset throttledAt)> _modelLastThrottles = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _minInterval;
    private readonly bool _enableFallback;
    private List<string> _models;

    public AntigravityProvider(
        ProviderOptions opts,
        AntigravityAccountService account,
        ILogger<AntigravityProvider> log,
        GatewayOptions? gatewayOpts = null)
    {
        _opts = opts;
        _gatewayOpts = gatewayOpts;
        _account = account;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        _minInterval = TimeSpan.FromSeconds(Math.Max(0, opts.MinInterval));

        _enableFallback = opts.EnableModelFallback ?? gatewayOpts?.EnableModelFallback ?? true;

        _models = opts.Models.Count > 0
            ? new List<string>(opts.Models)
            : new List<string>
            {
                "gemini-3.8-flash-high",
                "gemini-3.8-flash",
                "gemini-3.8-flash-medium",
                "gemini-3.8-flash-low",
                "gemini-3.5-flash-lite",
                "gemini-2.5-pro",
                "gemini-2.5-flash",
                "claude-sonnet-4-6",
                "claude-opus-4-6-thinking",
                "gpt-oss-120b-medium",
                "gpt-4o"
            };

        _log.LogInformation(
            "AntigravityProvider initialized. Project={Project}, BaseUrl={Url}, PerModelConcurrency={Conc}, ModelFallback={Fallback}",
            Project, BaseUrl, Math.Max(1, opts.MaxConcurrency), _enableFallback);
    }

    public string Name => "Antigravity";
    public string Project => string.IsNullOrWhiteSpace(_opts.Project) ? "aicode-consumers" : _opts.Project;
    public string Location => "global";
    public IReadOnlyList<string> Models => _models;

    private string BaseUrl => _opts.BaseUrl is { Length: > 0 } custom
        ? custom.TrimEnd('/')
        : "https://daily-cloudcode-pa.googleapis.com/v1internal";

    private string UserAgent => _opts.UserAgent is { Length: > 0 } ua
        ? ua
        : "antigravity/2.24.0 (win32; x64)";

    private int MaxAttempts => _opts.MaxAttempts > 0 ? _opts.MaxAttempts : 3;

    private SemaphoreSlim GateFor(string model) =>
        _modelGates.GetOrAdd(model, _ => new SemaphoreSlim(Math.Max(1, _opts.MaxConcurrency), Math.Max(1, _opts.MaxConcurrency)));

    private sealed class CloudCodePayload
    {
        [JsonPropertyName("project")] public string Project { get; set; } = "aicode-consumers";
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("request")] public GeminiGenerateContentRequest Request { get; set; } = new();
    }

    private string ResolveUpstreamModel(string requested)
    {
        var m = requested.Trim().ToLowerInvariant();
        return m switch
        {
            "gemini-3.8-flash" => "gemini-3.8-flash-high",
            "gemini-3.8" => "gemini-3.8-flash-high",
            "gpt-4o" => "gemini-3.8-flash-high",
            "gpt-4" => "gemini-3.8-flash-high",
            "claude-3-5-sonnet" => "claude-sonnet-4-6",
            "claude-3-7-sonnet" => "claude-sonnet-4-6",
            _ => m
        };
    }

    // ---------- 非流式聚合 ----------

    public async Task<GeminiGenerateContentResponse> GenerateAsync(
        string model, GeminiGenerateContentRequest request, CancellationToken ct)
    {
        var textBuilder = new System.Text.StringBuilder();
        var parts = new List<GeminiPart>();
        string? finishReason = null;
        GeminiUsageMetadata? lastUsage = null;
        string? modelVersion = null;

        await foreach (var chunk in GenerateStreamAsync(model, request, ct))
        {
            if (chunk.ModelVersion != null) modelVersion = chunk.ModelVersion;
            if (chunk.UsageMetadata != null) lastUsage = chunk.UsageMetadata;

            var cand = chunk.Candidates?.FirstOrDefault();
            if (cand == null) continue;

            if (cand.FinishReason != null) finishReason = cand.FinishReason;

            if (cand.Content?.Parts != null)
            {
                foreach (var part in cand.Content.Parts)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        textBuilder.Append(part.Text);
                    }
                    if (part.FunctionCall != null)
                    {
                        parts.Add(part);
                    }
                }
            }
        }

        if (textBuilder.Length > 0)
        {
            parts.Insert(0, new GeminiPart { Text = textBuilder.ToString() });
        }

        return new GeminiGenerateContentResponse
        {
            ModelVersion = modelVersion,
            UsageMetadata = lastUsage,
            Candidates = new List<GeminiCandidate>
            {
                new()
                {
                    Content = new GeminiContent
                    {
                        Role = "model",
                        Parts = parts
                    },
                    FinishReason = finishReason ?? (parts.Any(p => p.FunctionCall != null) ? "FUNCTION_CALL" : "STOP")
                }
            }
        };
    }

    // ---------- 流式核心（含智能重试与自动模型故障转移） ----------

    public async IAsyncEnumerable<GeminiGenerateContentStreamChunk> GenerateStreamAsync(
        string model, GeminiGenerateContentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // 尝试主模型请求
        Exception? primaryError = null;
        var yieldedAny = false;

        IAsyncEnumerable<GeminiGenerateContentStreamChunk>? streamEnumerable = null;
        try
        {
            streamEnumerable = GenerateStreamCoreAsync(model, request, ct);
        }
        catch (Exception ex)
        {
            primaryError = ex;
        }

        if (streamEnumerable is not null)
        {
            await using var enumerator = streamEnumerable.GetAsyncEnumerator(ct);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (UpstreamException ex) when (!yieldedAny && IsQuotaOrCapacityExhausted(ex.StatusCode, ex.ResponseBody))
                {
                    primaryError = ex;
                    break;
                }
                catch (Exception ex) when (!yieldedAny)
                {
                    primaryError = ex;
                    break;
                }

                if (!hasNext) yield break;

                yieldedAny = true;
                yield return enumerator.Current;
            }
        }

        // 若已输出部分内容，则不能执行切换（否则会导致客户端响应断裂）
        if (yieldedAny || primaryError is null) yield break;

        // 若未开启 Fallback 或发生非配额性错误，直接原样抛出
        if (!_enableFallback)
        {
            throw primaryError;
        }

        // 开始执行智能模型降级链 (Model Fallback)
        var fallbackList = GetFallbackCandidates(model);
        if (fallbackList.Count == 0)
        {
            throw primaryError;
        }

        _log.LogWarning(
            "Primary model '{Model}' failed ({Error}); triggering automatic failover chain: [{Chain}]",
            model, primaryError.Message, string.Join(" -> ", fallbackList));

        foreach (var fallbackModel in fallbackList)
        {
            if (ct.IsCancellationRequested) break;

            // 备选模型切换退避（600ms~1000ms），给上游速率限制器足够缓冲，避免毫秒级重试风暴
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(600, 1000)), ct);

            _log.LogInformation("Attempting failover to model '{FallbackModel}'...", fallbackModel);
            var fallbackYielded = false;

            IAsyncEnumerable<GeminiGenerateContentStreamChunk>? fbStream = null;
            try
            {
                fbStream = GenerateStreamCoreAsync(fallbackModel, request, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Failover model '{FallbackModel}' preparation failed: {Error}", fallbackModel, ex.Message);
                continue;
            }

            await using var fbEnumerator = fbStream.GetAsyncEnumerator(ct);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await fbEnumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Failover model '{FallbackModel}' execution failed: {Error}", fallbackModel, ex.Message);
                    break;
                }

                if (!hasNext)
                {
                    if (fallbackYielded) yield break;
                    break;
                }

                fallbackYielded = true;
                yield return fbEnumerator.Current;
            }

            if (fallbackYielded)
            {
                _log.LogInformation("Failover model '{FallbackModel}' successfully handled the request!", fallbackModel);
                yield break;
            }
        }

        // 所有备选均失败，抛出包含明确指引的异常
        throw new UpstreamException(
            HttpStatusCode.TooManyRequests,
            $"上游模型 '{model}' 配额耗尽 (RESOURCE_EXHAUSTED)，且备选降级链 [{string.Join(", ", fallbackList)}] 均不可用。建议：1) 检查 Google 账号使用额度；2) 切换至基础模型（如 gemini-2.5-flash / gemini-3.5-flash-lite）；3) 在 ANTIGRAVITY_REFRESH_TOKENS 中配置多账号轮换池。");
    }

    private async IAsyncEnumerable<GeminiGenerateContentStreamChunk> GenerateStreamCoreAsync(
        string model, GeminiGenerateContentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var upstreamModel = ResolveUpstreamModel(model);
        var url = $"{BaseUrl}:streamGenerateContent?alt=sse";

        var payload = new CloudCodePayload
        {
            Project = Project,
            Model = upstreamModel,
            Request = request
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions.Gemini);
        var gate = GateFor(upstreamModel);

        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var token = await _account.GetTokenAsync(false, ct);

                using var msg = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
                msg.Headers.Authorization = new("Bearer", token.Value);
                msg.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

                HttpResponseMessage? resp = null;
                Stream? stream = null;
                StreamReader? reader = null;

                try
                {
                    await WaitForModelCooldownAsync(upstreamModel, ct);
                    resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);

                    // 401 反应式刷新与单次重试
                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                    {
                        _log.LogWarning("Antigravity upstream returned 401; forcing token refresh and retrying once");
                        await _account.ForceRefreshAsync(ct);
                        resp.Dispose();
                        continue;
                    }

                    // 429 速率限制 / 配额耗尽
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        var delay = ParseRetryDelay(resp, errBody, attempt);
                        var resetSecs = (int)Math.Ceiling(delay.TotalSeconds);

                        ApplyModelThrottleCooldown(upstreamModel, resetSecs);

                        _log.LogWarning(
                            "Antigravity 429 rate limit on model '{Model}'; retry {Attempt}/{Max} after {Delay:F1}s: {Body}",
                            model, attempt, MaxAttempts, delay.TotalSeconds, Truncate(errBody, 200));

                        resp.Dispose();

                        // 若仍在重试次数内，按上游指示（Retry-After / reset after Ns）退避重试该模型
                        if (attempt < MaxAttempts)
                        {
                            await Task.Delay(delay, ct);
                            continue;
                        }

                        // 耗尽所有重试次数后，通知账号服务（多账号轮换生效），并抛出异常触发 Fallback 降级链
                        if (IsQuotaOrCapacityExhausted(HttpStatusCode.TooManyRequests, errBody))
                        {
                            _account.MarkAccountQuotaExhausted(TimeSpan.FromMinutes(2), "429 RESOURCE_EXHAUSTED");
                        }

                        throw new UpstreamException(HttpStatusCode.TooManyRequests, errBody);
                    }

                    // 503 容量过载 / 服务暂时不可用
                    if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        ApplyModelThrottleCooldown(upstreamModel, 20);
                        _log.LogWarning(
                            "Antigravity upstream 503 on model '{Model}'; retry {Attempt}/{Max}: {Body}",
                            model, attempt, MaxAttempts, Truncate(errBody, 200));
                        resp.Dispose();

                        if (attempt < MaxAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 8)), ct);
                            continue;
                        }

                        throw new UpstreamException(HttpStatusCode.ServiceUnavailable, errBody);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        var errBody = await resp.Content.ReadAsStringAsync(ct);
                        resp.Dispose();
                        throw new UpstreamException(resp.StatusCode, errBody);
                    }

                    ApplyModelSuccessCooldown(upstreamModel);

                    stream = await resp.Content.ReadAsStreamAsync(ct);
                    reader = new StreamReader(stream);
                }
                catch (Exception ex) when (ex is not UpstreamException && attempt < MaxAttempts && IsTransient(ex))
                {
                    _log.LogWarning(ex, "Transient connection error during send to model '{Model}'; retry {Attempt}/{Max}",
                        model, attempt, MaxAttempts);
                    resp?.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 10)), ct);
                    continue;
                }

                if (reader is null) continue;

                string? thoughtSignatureBuffer = null;

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null) break;
                        line = line.Trim();

                        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var data = line[5..].Trim();
                        if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

                        GeminiGenerateContentStreamChunk? chunk = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            var root = doc.RootElement;
                            var unwrapped = root.TryGetProperty("response", out var r) ? r : root;

                            // 捕获思维链签名
                            if (unwrapped.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in cands.EnumerateArray())
                                {
                                    if (c.TryGetProperty("content", out var cnt) && cnt.TryGetProperty("parts", out var prts))
                                    {
                                        foreach (var p in prts.EnumerateArray())
                                        {
                                            if (p.TryGetProperty("thoughtSignature", out var ts) && ts.ValueKind == JsonValueKind.String)
                                            {
                                                thoughtSignatureBuffer = ts.GetString();
                                            }
                                        }
                                    }
                                }
                            }

                            chunk = JsonSerializer.Deserialize<GeminiGenerateContentStreamChunk>(unwrapped, JsonOptions.Gemini);

                            // 为未带签名的 functionCall 回注前置签名
                            if (chunk?.Candidates != null && !string.IsNullOrEmpty(thoughtSignatureBuffer))
                            {
                                foreach (var c in chunk.Candidates)
                                {
                                    if (c.Content?.Parts != null)
                                    {
                                        foreach (var p in c.Content.Parts)
                                        {
                                            if (p.FunctionCall != null && string.IsNullOrEmpty(p.ThoughtSignature))
                                            {
                                                p.ThoughtSignature = thoughtSignatureBuffer;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to parse SSE data chunk: {Data}", Truncate(data, 100));
                        }

                        if (chunk != null)
                        {
                            yield return chunk;
                        }
                    }
                    yield break;
                }
                finally
                {
                    reader.Dispose();
                    if (stream != null) await stream.DisposeAsync();
                    resp?.Dispose();
                }
            }

            throw new UpstreamException(HttpStatusCode.BadGateway, $"Antigravity upstream failed on model '{model}' after retries.");
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------- 动态模型发现 ----------

    public async Task<List<string>> RefreshAvailableModelsAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}:fetchAvailableModels";
        try
        {
            var token = await _account.GetTokenAsync(false, ct);
            using var msg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            msg.Headers.Authorization = new("Bearer", token.Value);
            msg.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var resp = await _http.SendAsync(msg, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("models", out var modelsObj) && modelsObj.ValueKind == JsonValueKind.Object)
                {
                    var found = new List<string>();
                    foreach (var prop in modelsObj.EnumerateObject())
                    {
                        found.Add(prop.Name);
                    }
                    if (found.Count > 0)
                    {
                        if (!found.Contains("gemini-3.8-flash")) found.Insert(0, "gemini-3.8-flash");
                        if (!found.Contains("gpt-4o")) found.Add("gpt-4o");
                        _models = found;
                        _log.LogInformation("Discovered {Count} active models from Antigravity upstream", found.Count);
                        return found;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch available models from upstream; using defaults");
        }
        return _models;
    }

    private List<string> GetFallbackCandidates(string model)
    {
        var key = model.Trim().ToLowerInvariant();
        if (_gatewayOpts?.ModelFallbackChains != null && _gatewayOpts.ModelFallbackChains.TryGetValue(key, out var customList))
        {
            return customList.Where(m => !string.Equals(m, key, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (DefaultFallbackChains.TryGetValue(key, out var defaults))
        {
            return defaults.ToList();
        }

        // 通用降级规则
        return new List<string> { "gemini-3.8-flash-high", "gemini-2.5-flash" }
            .Where(m => !string.Equals(m, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException or TimeoutException;

    private static bool IsQuotaOrCapacityExhausted(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return body.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Resource has been exhausted", StringComparison.OrdinalIgnoreCase)
                || body.Contains("quota", StringComparison.OrdinalIgnoreCase);
        }
        if (status == HttpStatusCode.ServiceUnavailable)
        {
            return body.Contains("MODEL_CAPACITY_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || body.Contains("capacity", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static TimeSpan ParseRetryDelay(HttpResponseMessage resp, string body, int attempt)
    {
        // 1. 检查 HTTP Header: Retry-After
        if (resp.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta.HasValue)
            {
                return TimeSpan.FromSeconds(Math.Clamp((int)ra.Delta.Value.TotalSeconds, 1, 120));
            }
            if (ra.Date.HasValue)
            {
                var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    return TimeSpan.FromSeconds(Math.Clamp((int)delta.TotalSeconds, 1, 120));
                }
            }
        }

        // 2. 检查 JSON 响应体中的明确指示 (reset after Ns / retryDelay)
        var match = System.Text.RegularExpressions.Regex.Match(
            body,
            @"(?:reset after|retry in|retryDelay[""\s:]+)\s*(\d+)\s*s?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var s))
        {
            return TimeSpan.FromSeconds(Math.Clamp(s, 2, 120));
        }

        // 3. 指数退避 + 抖动 (Jitter)，避免固定 15s 硬等
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 12);
        var jitterMs = Random.Shared.Next(100, 800);
        return TimeSpan.FromSeconds(baseSeconds) + TimeSpan.FromMilliseconds(jitterMs);
    }

    private async ValueTask WaitForModelCooldownAsync(string model, CancellationToken ct)
    {
        if (_modelCooldowns.TryGetValue(model, out var until))
        {
            var delta = until - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                await Task.Delay(delta, ct);
            }
        }
    }

    private void ApplyModelSuccessCooldown(string model)
    {
        var secs = (int)_minInterval.TotalSeconds;
        if (_modelLastThrottles.TryGetValue(model, out var throttle))
        {
            // 冷却只在 429 发生后 2 分钟内生效，超出 2 分钟自动衰减恢复
            if (throttle.resetAfter > 0 && DateTimeOffset.UtcNow - throttle.throttledAt < TimeSpan.FromMinutes(2))
            {
                secs = Math.Max(secs, throttle.resetAfter);
            }
        }

        if (secs > 0)
        {
            var target = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(secs);
            _modelCooldowns.AddOrUpdate(model, target, (_, prev) => target > prev ? target : prev);
        }
    }

    private void ApplyModelThrottleCooldown(string model, int resetSecs)
    {
        var now = DateTimeOffset.UtcNow;
        _modelLastThrottles[model] = (resetSecs, now);
        var target = now + TimeSpan.FromSeconds(resetSecs);
        _modelCooldowns.AddOrUpdate(model, target, (_, prev) => target > prev ? target : prev);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}
