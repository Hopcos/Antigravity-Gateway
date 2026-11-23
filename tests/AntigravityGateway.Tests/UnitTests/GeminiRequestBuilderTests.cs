using AntigravityGateway.Models.OpenAi;
using AntigravityGateway.Services;
using Xunit;

namespace AntigravityGateway.Tests.UnitTests;

public class GeminiRequestBuilderTests
{
    [Fact]
    public void Build_TranslatesSystemAndUserMessages_InlinesIntoUserTurnByDefault()
    {
        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-high",
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "You are a test assistant." },
                new() { Role = "user", Content = "Hello!" }
            }
        };

        var gemini = GeminiRequestBuilder.Build(req);

        // 默认模式下 system 指令内联至首回合 user 前缀，避免 Google CloudCode PA 针对外部 Agent 系统的 systemInstruction 触发 429 配额拦截
        Assert.Null(gemini.SystemInstruction);
        Assert.Single(gemini.Contents);
        Assert.Equal("user", gemini.Contents[0].Role);
        Assert.Single(gemini.Contents[0].Parts);
        Assert.Contains("You are a test assistant.", gemini.Contents[0].Parts[0].Text);
        Assert.Contains("Hello!", gemini.Contents[0].Parts[0].Text);
    }

    [Fact]
    public void Build_TranslatesSystemAndUserMessages_ExplicitSystemInstructionMode()
    {
        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-high",
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "You are a test assistant." },
                new() { Role = "user", Content = "Hello!" }
            }
        };

        var gemini = GeminiRequestBuilder.Build(req, inlineSystemInstruction: false);

        Assert.NotNull(gemini.SystemInstruction);
        Assert.Single(gemini.SystemInstruction.Parts);
        Assert.Equal("You are a test assistant.", gemini.SystemInstruction.Parts[0].Text);

        Assert.Single(gemini.Contents);
        Assert.Equal("user", gemini.Contents[0].Role);
        Assert.Equal("Hello!", gemini.Contents[0].Parts[0].Text);
    }

    [Fact]
    public void Build_MultiTurnToolInteraction_RestoresThoughtSignatureAndFunctionResponse()
    {
        var toolName = "calculator";
        var thoughtSig = "test_signature_xyz_123";
        var toolCallId = ToolCallIds.Encode(toolName, thoughtSig);

        var req = new ChatCompletionRequest
        {
            Model = "gemini-3.8-flash-high",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "What is 2+2?" },
                new()
                {
                    Role = "assistant",
                    ToolCalls = new List<OpenAiToolCall>
                    {
                        new()
                        {
                            Id = toolCallId,
                            Function = new OpenAiToolCallFunction
                            {
                                Name = toolName,
                                Arguments = "{\"expr\":\"2+2\"}"
                            }
                        }
                    }
                },
                new()
                {
                    Role = "tool",
                    ToolCallId = toolCallId,
                    Content = "4"
                }
            }
        };

        var gemini = GeminiRequestBuilder.Build(req);

        Assert.Equal(3, gemini.Contents.Count);

        // Turn 1: user
        Assert.Equal("user", gemini.Contents[0].Role);

        // Turn 2: assistant / model with thought signature
        Assert.Equal("model", gemini.Contents[1].Role);
        var fnCallPart = gemini.Contents[1].Parts[0];
        Assert.NotNull(fnCallPart.FunctionCall);
        Assert.Equal(toolName, fnCallPart.FunctionCall.Name);
        Assert.Equal(thoughtSig, fnCallPart.ThoughtSignature);

        // Turn 3: tool / user function response with decoded tool name
        Assert.Equal("user", gemini.Contents[2].Role);
        var fnRespPart = gemini.Contents[2].Parts[0];
        Assert.NotNull(fnRespPart.FunctionResponse);
        Assert.Equal(toolName, fnRespPart.FunctionResponse.Name);
    }

    [Fact]
    public void Build_ConsecutiveUserMessages_ConsolidatesAdjacentTextParts()
    {
        var req = new ChatCompletionRequest
        {
            Model = "gemini-2.5-flash-lite",
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "你是什么模型？" },
                new() { Role = "user", Content = "Current runtime context snapshot." },
                new() { Role = "user", Content = "Current working directory: E:\\temp\\test-dsh" }
            }
        };

        var gemini = GeminiRequestBuilder.Build(req);

        // 验证 3 个连续 user 消息不仅合并为 1 个 Content 回合，其 3 个 text 节点还被压实合并为单一文本 Part
        Assert.Single(gemini.Contents);
        Assert.Equal("user", gemini.Contents[0].Role);
        Assert.Single(gemini.Contents[0].Parts);
        Assert.Contains("你是什么模型？", gemini.Contents[0].Parts[0].Text);
        Assert.Contains("Current runtime context snapshot.", gemini.Contents[0].Parts[0].Text);
        Assert.Contains("Current working directory: E:\\temp\\test-dsh", gemini.Contents[0].Parts[0].Text);
    }
}
