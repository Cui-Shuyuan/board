using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class PromptIsolationTests
{
    [Fact]
    public async Task LegacyMode_SendsOnlyLegacyPromptAndExecutePlanTool()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        CapturedCall? captured = null;
        var llm = new CapturingLlm(call =>
        {
            if (call.HasTools)
            {
                captured = call;
                return ToolCall("execute_plan", """{"plan":{"queries":[{"relation":"list"}]}}""");
            }
            return Final("LEGACY_FINAL");
        });
        var sut = CreateService(llm, rules, "legacy", "LEGACY_ONLY {game_name}", "PROGRAM_ONLY {game_name}");

        var reply = await sut.ProcessAsync("testgame", History());

        Assert.Equal("LEGACY_FINAL", reply);
        Assert.NotNull(captured);
        Assert.Equal("execute_plan", Assert.Single(captured!.ToolNames));
        var system = captured.Messages.First(message => message.Role == "system").Content!;
        Assert.Contains("LEGACY_ONLY testgame", system);
        Assert.DoesNotContain("PROGRAM_ONLY", system);
        Assert.Equal("system", captured.Messages[0].Role);
        Assert.Contains(captured.Messages, message => message.Role == "user" && message.Content == "测试问题");
        var schema = Assert.Single(captured.ToolSchemas);
        Assert.Contains("\"name\":\"execute_plan\"", schema);
        Assert.Contains("\"queries\"", schema);
        Assert.Contains("\"required\":[\"relation\"]", schema);
    }

    [Fact]
    public async Task ProgramMode_SendsOnlyProgramPromptAndStageSpecificTools()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var calls = new List<CapturedCall>();
        var llm = new CapturingLlm(call =>
        {
            calls.Add(call);
            if (call.ToolNames.Contains("submit_understanding", StringComparer.Ordinal))
            {
                return ToolCall("submit_understanding", """
                {"request_kind":"rules","questions":[
                  {"entity":"use_widget","relation":"condition","intent":"使用小装置需要什么条件"}
                ]}
                """);
            }
            return ToolCall("submit_answer", """
            {"kind":"answer","reply":"使用小装置前要先满足条件。","question_results":[
              {"question_id":"q1","status":"answered","evidence_ids":["e1"]}
            ]}
            """);
        });
        var sut = CreateService(llm, rules, "program", "LEGACY_ONLY {game_name}", "PROGRAM_ONLY {game_name}");

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("使用小装置前要先满足条件。", result.Reply);
        Assert.Equal(2, calls.Count);
        var understandCall = Assert.Single(calls, call => call.ToolNames.Contains("submit_understanding", StringComparer.Ordinal));
        var answerCall = Assert.Single(calls, call => call.ToolNames.Contains("submit_answer", StringComparer.Ordinal));
        Assert.Equal("submit_understanding", Assert.Single(understandCall.ToolNames));
        Assert.Equal("submit_answer", Assert.Single(answerCall.ToolNames));
        Assert.DoesNotContain("execute_plan", understandCall.ToolNames);
        Assert.DoesNotContain("execute_plan", answerCall.ToolNames);
        Assert.Contains("\"name\":\"submit_understanding\"", Assert.Single(understandCall.ToolSchemas));
        Assert.Contains("\"name\":\"submit_answer\"", Assert.Single(answerCall.ToolSchemas));

        var allSystem = calls.SelectMany(call => call.Messages)
            .Where(message => message.Role == "system")
            .Select(message => message.Content ?? "")
            .ToList();
        Assert.Contains(allSystem, text => text.Contains("PROGRAM_ONLY testgame", StringComparison.Ordinal));
        Assert.Contains(allSystem, text => text.Contains("【理解阶段】", StringComparison.Ordinal));
        Assert.Contains(allSystem, text => text.Contains("【回答阶段】", StringComparison.Ordinal));
        Assert.DoesNotContain(allSystem, text => text.Contains("LEGACY_ONLY", StringComparison.Ordinal));
        Assert.Contains(answerCall.Messages, message =>
            message.Role == "user" && (message.Content ?? "").Contains("evidence_catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyPrompt_DoesNotMasqueradeAsFrozenA992cfeBaseline()
    {
        var path = Path.Combine(BoardPaths.GetBasePath(), "backend", "BoardAI.Api", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var legacy = document.RootElement.GetProperty("LLM").GetProperty("LegacySystemPrompt").GetString() ?? "";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacy))).ToLowerInvariant();

        // 冻结基线仍是 a992cfe/ffac498 的 859260e5…f1343d5；
        // 新候选因 tool schema 增加 flow scope 与 members 而变更，不能复用旧 SHA 做发布声明。
        Assert.NotEqual("859260e5ca6ed8f5154b63dde2fbdad61ad1e1e3bd97261108f7339f7f1343d5", hash);
        Assert.Contains("scope=local", legacy);
        Assert.Contains("members", legacy);
    }

    [Fact]
    public void InvalidOrchestrationMode_IsRejectedExplicitly()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new CapturingLlm(_ => Final("unused"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            CreateService(llm, rules, "typo", "legacy", "program"));

        Assert.Contains("OrchestrationMode", error.Message);
    }

    [Fact]
    public void AppSettings_KeepLegacyAndProgramPromptsSeparate()
    {
        var path = Path.Combine(BoardPaths.GetBasePath(), "backend", "BoardAI.Api", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var llm = document.RootElement.GetProperty("LLM");
        var legacy = llm.GetProperty("LegacySystemPrompt").GetString() ?? "";
        var program = llm.GetProperty("ProgramSystemPrompt").GetString() ?? "";

        Assert.Contains("你只有一个工具 execute_plan", legacy);
        Assert.Contains("查询计划编写", legacy);
        Assert.DoesNotContain("你只有一个工具 execute_plan", program);
        Assert.DoesNotContain("execute_plan", program);
        Assert.DoesNotContain("候选与停止规则", program);
        Assert.False(llm.TryGetProperty("SystemPrompt", out _));
        Assert.Equal("legacy", llm.GetProperty("OrchestrationMode").GetString());
    }

    private static ChatOrchestratorService CreateService(
        CapturingLlm llm,
        GameRulesService rules,
        string mode,
        string legacyPrompt,
        string programPrompt)
        => new(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = mode,
                LegacySystemPrompt = legacyPrompt,
                ProgramSystemPrompt = programPrompt,
                MaxToolRounds = 3,
                MaxRequestSeconds = 10,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxRepeatedFailures = 3,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

    private static List<ChatMessage> History() =>
        new() { new ChatMessage { Role = "user", Content = "测试问题" } };

    private static LLMChatResponse ToolCall(string name, string arguments) => new()
    {
        Choices = new List<LLMChoice>
        {
            new()
            {
                Message = new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "call_1", Function = new FunctionCall { Name = name, Arguments = arguments } }
                    }
                }
            }
        }
    };

    private static LLMChatResponse Final(string content) => new()
    {
        Choices = new List<LLMChoice>
        {
            new() { Message = new ChatMessage { Role = "assistant", Content = content } }
        }
    };

    private sealed class CapturingLlm : ILLMService
    {
        private readonly Func<CapturedCall, LLMChatResponse> _handler;

        public CapturingLlm(Func<CapturedCall, LLMChatResponse> handler) => _handler = handler;

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_handler(new CapturedCall(
                messages.ToList(),
                tools?.Select(tool => tool.Function.Name).ToList() ?? new List<string>(),
                tools?.Select(tool => JsonSerializer.Serialize(tool)).ToList() ?? new List<string>(),
                tools is { Count: > 0 })));
    }

    private sealed record CapturedCall(
        List<ChatMessage> Messages,
        List<string> ToolNames,
        List<string> ToolSchemas,
        bool HasTools);
}
