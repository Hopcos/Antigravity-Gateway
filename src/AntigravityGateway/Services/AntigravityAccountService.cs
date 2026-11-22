using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AntigravityGateway.Configuration;

namespace AntigravityGateway.Services;

public readonly record struct AccessToken(string Value, DateTimeOffset ExpiresUtc, int TtlSeconds)
{
    public static readonly AccessToken Empty = new("", DateTimeOffset.MinValue, 0);

    public bool IsValidAt(DateTimeOffset now, int marginSeconds = 60) =>
        !string.IsNullOrEmpty(Value) && ExpiresUtc - now > TimeSpan.FromSeconds(marginSeconds);
}

public sealed class AccountStatus
{
    public string Email { get; set; } = "unknown";
    public string Source { get; set; } = "unknown";
    public bool HasToken { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public int? RemainingSeconds => ExpiresUtc.HasValue ? (int)Math.Max(0, (ExpiresUtc.Value - DateTimeOffset.UtcNow).TotalSeconds) : null;
    public string? Scopes { get; set; }
    public int TotalAccounts { get; set; } = 1;
    public int ActiveAccounts { get; set; } = 1;
}

public sealed class AccountEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Email { get; set; } = "unknown";
    public string Source { get; set; } = "unknown";
    public string ClientId { get; set; } = AntigravityAccountService.DefaultAntigravityClientId;
    public string ClientSecret { get; set; } = AntigravityAccountService.DefaultAntigravityClientSecret;
    public string RefreshToken { get; set; } = "";
    public AccessToken CurrentToken { get; set; } = AccessToken.Empty;
    public DateTimeOffset CooldownUntil { get; set; } = DateTimeOffset.MinValue;
    public string? InitialAccessToken { get; set; }
    public DateTimeOffset? InitialExpiry { get; set; }

    public bool IsInCooldown(DateTimeOffset now) => CooldownUntil > now;
}

/// <summary>
/// Antigravity 账号与多账号轮换池管理服务：
/// 1. 自动从 Windows Credential Manager（gemini:antigravity）无感读取当前登录的 Antigravity 账号；
/// 2. 具备多级降级与多账号探测（Windows Credential Manager + 环境变量多 Token + oauth_creds.json + ADC）；
/// 3. 管理双 Token 永续生命周期：基于 Google OAuth 官方客户端及 Refresh Token 无限静默换取 Access Token；
/// 4. 支持多账号池轮换：当某个账号出现 429 配额耗尽时，自动标记冷却并秒级切换至池中下一个健康账号；
/// 5. 支持懒刷新、后台巡检保活（TokenRefresherBackgroundService）与 401 反应式刷新重试。
/// </summary>
public sealed class AntigravityAccountService
{
    public const string DefaultAntigravityClientId =
        "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    public const string DefaultAntigravityClientSecret =
        "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";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly HttpClient _http;
    private readonly AuthOptions _auth;
    private readonly ILogger<AntigravityAccountService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _credsGate = new(1, 1);

    private List<AccountEntry>? _accounts;
    private int _activeAccountIndex;

    public AntigravityAccountService(AuthOptions auth, ILogger<AntigravityAccountService> log)
    {
        _auth = auth;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public AccountStatus GetStatus()
    {
        var accounts = _accounts;
        if (accounts is null)
        {
            try
            {
                lock (_credsGate)
                {
                    accounts = _accounts ??= ResolveAllAccounts();
                }
            }
            catch
            {
                // 尚未加载成功
            }
        }

        if (accounts is null || accounts.Count == 0)
        {
            return new AccountStatus
            {
                Email = "detecting...",
                Source = _auth.Source,
                HasToken = false,
                TotalAccounts = 0,
                ActiveAccounts = 0
            };
        }

        var now = DateTimeOffset.UtcNow;
        var active = GetCurrentOrBestAccount();
        var token = active.CurrentToken;
        var activeCount = accounts.Count(a => !a.IsInCooldown(now));

        return new AccountStatus
        {
            Email = active.Email,
            Source = active.Source,
            HasToken = !string.IsNullOrEmpty(token.Value),
            ExpiresUtc = token.ExpiresUtc == DateTimeOffset.MinValue ? null : token.ExpiresUtc,
            Scopes = "https://www.googleapis.com/auth/aicode https://www.googleapis.com/auth/cloud-platform",
            TotalAccounts = accounts.Count,
            ActiveAccounts = Math.Max(1, activeCount)
        };
    }

    /// <summary>获取当前有效账号的 Access Token；force=true 时强制触发刷新。</summary>
    public async Task<AccessToken> GetTokenAsync(bool force = false, CancellationToken ct = default)
    {
        var accounts = await EnsureAccountsLoadedAsync(ct);
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct);
        try
        {
            var account = GetCurrentOrBestAccount();
            if (!force && account.CurrentToken.IsValidAt(now))
            {
                return account.CurrentToken;
            }

            var newToken = await RefreshAccountTokenAsync(account, ct);
            account.CurrentToken = newToken;
            return newToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AccessToken> ForceRefreshAsync(CancellationToken ct = default) => GetTokenAsync(true, ct);

    /// <summary>当某账号上游 429 配额耗尽时，标记其冷却并在存在多个账号时切换至下一个可用账号。</summary>
    public void MarkAccountQuotaExhausted(TimeSpan cooldown, string reason)
    {
        lock (_credsGate)
        {
            if (_accounts is null || _accounts.Count == 0) return;

            var current = GetCurrentOrBestAccount();

            // 如果池中只有 1 个账号，不执行长达数分钟的全局锁定，避免因瞬时并发或突发限流导致整个服务自锁瘫痪
            if (_accounts.Count <= 1)
            {
                _log.LogWarning("Single account in pool; skipping global account cooldown for {Email} ({Reason})", current.Email, reason);
                return;
            }

            current.CooldownUntil = DateTimeOffset.UtcNow + cooldown;
            _log.LogWarning("Account {Email} marked on cooldown for {Secs}s due to quota exhaustion ({Reason})",
                current.Email, cooldown.TotalSeconds, reason);

            var now = DateTimeOffset.UtcNow;
            for (var i = 1; i <= _accounts.Count; i++)
            {
                var nextIdx = (_activeAccountIndex + i) % _accounts.Count;
                if (!_accounts[nextIdx].IsInCooldown(now))
                {
                    _activeAccountIndex = nextIdx;
                    _log.LogInformation("Rotated active Antigravity account to {Email} ({Index}/{Total})",
                        _accounts[nextIdx].Email, nextIdx + 1, _accounts.Count);
                    return;
                }
            }
        }
    }

    /// <summary>供后台保活任务调用：对账号池内所有即将过期的 Token 提前执行静默续期。</summary>
    public async Task RefreshIfExpiringSoonAsync(CancellationToken ct = default)
    {
        if (_accounts is null || _accounts.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var account in _accounts)
        {
            var t = account.CurrentToken;
            var threshold = TimeSpan.FromSeconds(Math.Max(t.TtlSeconds / 10, 300));
            if (!t.IsValidAt(now, 0) || t.ExpiresUtc - now < threshold)
            {
                _log.LogInformation("Token for account {Email} approaching expiry; initiating proactive background renewal",
                    account.Email);
                try
                {
                    var refreshed = await RefreshAccountTokenAsync(account, ct);
                    account.CurrentToken = refreshed;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Background token renewal failed for account {Email}", account.Email);
                }
            }
        }
    }

    private AccountEntry GetCurrentOrBestAccount()
    {
        if (_accounts is null || _accounts.Count == 0)
        {
            throw new TokenRefreshException("没有可用的 Antigravity 账号。");
        }

        var now = DateTimeOffset.UtcNow;
        if (!_accounts[_activeAccountIndex].IsInCooldown(now))
        {
            return _accounts[_activeAccountIndex];
        }

        // 寻找第一个不在冷却中的账号
        for (var i = 0; i < _accounts.Count; i++)
        {
            if (!_accounts[i].IsInCooldown(now))
            {
                _activeAccountIndex = i;
                return _accounts[i];
            }
        }

        // 全部在冷却，挑选最早恢复的账号
        var earliest = _accounts.OrderBy(a => a.CooldownUntil).First();
        _activeAccountIndex = _accounts.IndexOf(earliest);
        return earliest;
    }

    private async Task<AccessToken> RefreshAccountTokenAsync(AccountEntry account, CancellationToken ct)
    {
        // 若首次加载且已有可用的初始 token 且未过期，直接使用
        if (account.CurrentToken == AccessToken.Empty &&
            !string.IsNullOrEmpty(account.InitialAccessToken) &&
            account.InitialExpiry.HasValue)
        {
            var initialExp = account.InitialExpiry.Value;
            if (initialExp > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                var initialTtl = (int)Math.Max(60, (initialExp - DateTimeOffset.UtcNow).TotalSeconds);
                _log.LogInformation("Using initial active token from {Source} for account {Email} (valid until {Exp:O})",
                    account.Source, account.Email, initialExp);
                return new AccessToken(account.InitialAccessToken, initialExp, initialTtl);
            }
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = account.ClientId,
            ["client_secret"] = account.ClientSecret,
            ["refresh_token"] = account.RefreshToken,
            ["grant_type"] = "refresh_token"
        });

        using var resp = await _http.PostAsync(TokenEndpoint, form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var hint = body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                ? $"Antigravity refresh token 已失效或被撤销 ({account.Email})，请在 Antigravity 重新登录账号"
                : $"请检查账号凭据配置 ({account.Email})";
            throw new TokenRefreshException($"Token refresh failed ({(int)resp.StatusCode}): {hint}. {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString();
        if (string.IsNullOrEmpty(access))
        {
            throw new TokenRefreshException($"OAuth token endpoint returned no access_token for {account.Email}");
        }

        var ttl = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var e) ? e : 3600;
        ttl = Math.Max(ttl, 60);
        var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(ttl - 60);

        _log.LogInformation("Antigravity token successfully refreshed for {Email} (valid ~{Ttl}s, until {Exp:yyyy-MM-dd HH:mm:ss})",
            account.Email, ttl, expiresUtc.ToLocalTime());

        return new AccessToken(access, expiresUtc, ttl);
    }

    private async ValueTask<List<AccountEntry>> EnsureAccountsLoadedAsync(CancellationToken ct)
    {
        if (_accounts is not null && _accounts.Count > 0) return _accounts;

        await _credsGate.WaitAsync(ct);
        try
        {
            if (_accounts is not null && _accounts.Count > 0) return _accounts;
            _accounts = ResolveAllAccounts();
            return _accounts;
        }
        finally
        {
            _credsGate.Release();
        }
    }

    private List<AccountEntry> ResolveAllAccounts()
    {
        var list = new List<AccountEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        void AddIfValid(AccountEntry? entry)
        {
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.RefreshToken) && seenTokens.Add(entry.RefreshToken))
            {
                list.Add(entry);
            }
        }

        // 1. 显式环境变量列表 / 单个 Token
        if (_auth.RefreshTokens.Count > 0)
        {
            var clientId = string.IsNullOrWhiteSpace(_auth.ClientId) ? DefaultAntigravityClientId : _auth.ClientId;
            var clientSecret = string.IsNullOrWhiteSpace(_auth.ClientSecret) ? DefaultAntigravityClientSecret : _auth.ClientSecret;
            var idx = 1;
            foreach (var rt in _auth.RefreshTokens)
            {
                if (!string.IsNullOrWhiteSpace(rt))
                {
                    AddIfValid(new AccountEntry
                    {
                        ClientId = clientId,
                        ClientSecret = clientSecret,
                        RefreshToken = rt.Trim(),
                        Email = $"env-account-{idx++}",
                        Source = "Environment (ANTIGRAVITY_REFRESH_TOKENS)"
                    });
                }
            }
            if (list.Count > 0)
            {
                _log.LogInformation("Loaded {Count} Antigravity account(s) from environment variable token list", list.Count);
                return list;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_auth.RefreshToken))
        {
            var clientId = string.IsNullOrWhiteSpace(_auth.ClientId) ? DefaultAntigravityClientId : _auth.ClientId;
            var clientSecret = string.IsNullOrWhiteSpace(_auth.ClientSecret) ? DefaultAntigravityClientSecret : _auth.ClientSecret;
            AddIfValid(new AccountEntry
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                RefreshToken = _auth.RefreshToken.Trim(),
                Email = "env-configured-user",
                Source = "Environment (ANTIGRAVITY_REFRESH_TOKEN)"
            });
            if (list.Count > 0)
            {
                _log.LogInformation("Loaded Antigravity account from environment variable ANTIGRAVITY_REFRESH_TOKEN");
                return list;
            }
        }

        // 2. Windows 凭据管理器探测 (Target: gemini:antigravity)
        if (OperatingSystem.IsWindows() && !_auth.Source.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var winCred = TryReadWindowsCredential(_auth.CredentialTarget);
            if (winCred is not null)
            {
                AddIfValid(winCred);
                _log.LogInformation("Discovered Antigravity account from Windows Credential Manager [{Target}]: {Email}",
                    _auth.CredentialTarget, winCred.Email);
                return list;
            }
        }

        // 3. ~/.gemini/oauth_creds.json 文件探测 (降级备选)
        var tokenFile = !string.IsNullOrEmpty(_auth.TokenFile) ? _auth.TokenFile : DefaultTokenFilePath();
        if (File.Exists(tokenFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(tokenFile));
                var root = doc.RootElement;
                var rt = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
                var at = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
                var cid = root.TryGetProperty("client_id", out var c) ? c.GetString() : null;
                var sec = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;

                if (!string.IsNullOrEmpty(rt))
                {
                    var email = ExtractEmailFromIdToken(root.TryGetProperty("id_token", out var idt) ? idt.GetString() : null) ?? "local-gemini-user";
                    AddIfValid(new AccountEntry
                    {
                        ClientId = string.IsNullOrEmpty(cid) ? DefaultAntigravityClientId : cid,
                        ClientSecret = string.IsNullOrEmpty(sec) ? DefaultAntigravityClientSecret : sec,
                        RefreshToken = rt,
                        Email = email,
                        Source = "File (~/.gemini/oauth_creds.json)",
                        InitialAccessToken = at
                    });
                    _log.LogInformation("Loaded Antigravity credentials from {File} for account {Email}", tokenFile, email);
                    return list;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to parse token file {Path}", tokenFile);
            }
        }

        // 4. gcloud ADC 文件探测 (最后降级)
        var adcFile = !string.IsNullOrEmpty(_auth.AdcFile) ? _auth.AdcFile : DefaultAdcFilePath();
        if (File.Exists(adcFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(adcFile));
                var root = doc.RootElement;
                var cid = root.TryGetProperty("client_id", out var c) ? c.GetString() : null;
                var sec = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
                var rt = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
                var acct = root.TryGetProperty("account", out var acc) ? acc.GetString() : "adc-user";

                if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(sec) && !string.IsNullOrEmpty(rt))
                {
                    AddIfValid(new AccountEntry
                    {
                        ClientId = cid,
                        ClientSecret = sec,
                        RefreshToken = rt,
                        Email = acct ?? "adc-user",
                        Source = "ADC File"
                    });
                    _log.LogInformation("Loaded ADC credentials from {File} for account {Email}", adcFile, acct);
                    return list;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to parse ADC file {Path}", adcFile);
            }
        }

        if (list.Count == 0)
        {
            throw new TokenRefreshException(
                "未找到有效的 Antigravity 登录凭据！请确保本机已在 Antigravity 中登录账号，或在 Windows Credential Manager 中存在 'gemini:antigravity'，或设置 ANTIGRAVITY_REFRESH_TOKEN。");
        }

        _log.LogInformation("Total Antigravity accounts loaded in pool: {Count}", list.Count);
        return list;
    }

    private static AccountEntry? TryReadWindowsCredential(string targetName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            if (CredRead(targetName, 1 /* CRED_TYPE_GENERIC */, 0, out var pCred))
            {
                try
                {
                    var cred = Marshal.PtrToStructure<NativeCredential>(pCred);
                    if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                    {
                        var bytes = new byte[cred.CredentialBlobSize];
                        Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                        var json = Encoding.UTF8.GetString(bytes);

                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        string? refreshToken = null;
                        string? accessToken = null;
                        DateTimeOffset? expiry = null;

                        if (root.TryGetProperty("token", out var tok))
                        {
                            refreshToken = tok.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                            accessToken = tok.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                            if (tok.TryGetProperty("expiry", out var exp) && exp.GetString() is { } expStr)
                            {
                                if (DateTimeOffset.TryParse(expStr, out var dto)) expiry = dto;
                            }
                        }

                        if (string.IsNullOrEmpty(refreshToken))
                        {
                            refreshToken = root.TryGetProperty("refresh_token", out var rt2) ? rt2.GetString() : null;
                        }

                        if (!string.IsNullOrEmpty(refreshToken))
                        {
                            var idToken = root.TryGetProperty("id_token", out var idt) ? idt.GetString() : null;
                            var email = ExtractEmailFromIdToken(idToken) ?? cred.UserName ?? "antigravity-user";

                            return new AccountEntry
                            {
                                ClientId = DefaultAntigravityClientId,
                                ClientSecret = DefaultAntigravityClientSecret,
                                RefreshToken = refreshToken,
                                Email = email,
                                Source = "Windows Credential Manager (gemini:antigravity)",
                                InitialAccessToken = accessToken,
                                InitialExpiry = expiry
                            };
                        }
                    }
                }
                finally
                {
                    CredFree(pCred);
                }
            }
        }
        catch
        {
            // Windows Credential Manager 读取异常降级
        }
        return null;
    }

    private static string? ExtractEmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload
            };
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string DefaultTokenFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".gemini", "oauth_creds.json");
    }

    private static string DefaultAdcFilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "gcloud", "application_default_credentials.json");
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "gcloud", "application_default_credentials.json");
    }

    // Windows P/Invoke
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
