using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class RequestLedgerAndTerminalProtocolTests
{
    [Fact]
    public async Task AutoReadCandidates_ThenSubmitAnswer_NeedsNoExtraConfirmationRound()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"explain","entity":"widget","intent":"小装置是什么"}]}}""");
            if (call.Index == 2)
            {
                var evidenceId = LastEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("evidence id missing");
                return ToolResponse("call_2", "submit_answer",
                    JsonSerializer.Serialize(new
                    {
                        outcome = "answer",
                        text = "小装置是测试对象。",
                        evidence_ids = new[] { evidenceId }
                    }));
            }
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("小装置是测试对象。", result.Reply);
        Assert.Equal(2, llm.CallCount);
        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal("answer", result.Evidence.OrchestrationOutcome);
        Assert.DoesNotContain(llm.AllMessages, message =>
            message.Role == "user" && message.Content?.Contains("先不要直接回答") == true);
    }

    [Fact]
    public async Task LegalClarify_WithNoEvidence_IsAcceptedWithoutRepairRound()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"identify","entity":"小装置","intent":"确认组件"}]}}""");
            if (call.Index == 2)
                return ToolResponse("call_2", "submit_answer",
                    """{"outcome":"clarify","text":"请确认你说的是哪一个组件。","clarification":{"missing_kind":"object_identity","requested_information":"组件的颜色或位置"}}""");
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("请确认你说的是哪一个组件。", result.Reply);
        Assert.Equal(2, llm.CallCount);
        Assert.Equal("clarify", result.Orchestration!.Outcome);
        Assert.Empty(result.Orchestration.ProtocolErrors);
    }

    [Fact]
    public async Task ForgedEvidenceId_GetsBoundedProtocolRepair_ThenValidSubmissionCompletes()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"explain","entity":"widget"}]}}""");
            if (call.Index == 2)
                return ToolResponse("call_2", "submit_answer",
                    """{"outcome":"answer","text":"伪造引用。","evidence_ids":["ev_forged"]}""");
            if (call.Index == 3)
            {
                var evidenceId = LastEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("evidence id missing");
                return ToolResponse("call_3", "submit_answer",
                    JsonSerializer.Serialize(new
                    {
                        outcome = "answer",
                        text = "修复后的回答。",
                        evidence_ids = new[] { evidenceId }
                    }));
            }
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("修复后的回答。", result.Reply);
        Assert.Equal(3, llm.CallCount);
        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Contains(result.Orchestration.ProtocolErrors, error => error == "protocol_validation_error");
        Assert.Contains(llm.AllMessages, message =>
            message.Role == "tool"
            && message.Content?.Contains("未在本请求交付", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ExactDuplicatePlan_IsReused_WithoutSecondBottomExecutionOrEvidenceDuplication()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var planArguments = """{"plan":{"queries":[{"relation":"explain","entity":"widget"}]}}""";
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan", planArguments);
            if (call.Index == 2)
                return ToolResponse("call_2", "execute_plan", planArguments);
            if (call.Index == 3)
            {
                var evidenceId = LastEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("evidence id missing");
                return ToolResponse("call_3", "submit_answer",
                    JsonSerializer.Serialize(new
                    {
                        outcome = "answer",
                        text = "复用结果。",
                        evidence_ids = new[] { evidenceId }
                    }));
            }
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal(3, llm.CallCount);
        Assert.Equal(1, result.Orchestration!.ReusedToolCallCount);
        Assert.Equal(1, result.Orchestration.ExecutedToolCallCount);
        Assert.Equal(1, result.Evidence.QueryCount);
        Assert.Single(result.Evidence.Queries[0].EvidenceIds);

        var toolMessages = llm.LastMessages
            .Where(message => message.Role == "tool")
            .Select(message => message.Content ?? "")
            .Where(content => content.Contains("evidence_catalog", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Equal(toolMessages[0], toolMessages[1]);
    }

    [Fact]
    public async Task DifferentScope_IsNotReusedAsSameQuery()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.GameFlowPath, """
        {
          "procedures": [
            { "id": "game", "options": [
              { "id": "parent_a", "options": [ { "id": "step" } ] },
              { "id": "parent_b", "options": [ { "id": "step" } ] }
            ] }
          ]
        }
        """);
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"flow","scope":"local","entity":"parent_a"}]}}""");
            if (call.Index == 2)
                return ToolResponse("call_2", "execute_plan",
                    """{"plan":{"queries":[{"relation":"flow","scope":"local","entity":"parent_b"}]}}""");
            return FinalResponse("两个局部流程都查过。");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal(0, result.Orchestration!.ReusedToolCallCount);
        Assert.Equal(2, result.Orchestration.ExecutedToolCallCount);
        Assert.Equal(2, result.Evidence.QueryCount);
    }

    [Fact]
    public async Task SelectionQuestion_CompletesWithoutUnrelatedPlayerCountClarification()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"explain","entity":"widget"}]}}""");
            if (call.Index == 2)
            {
                var evidenceId = LastEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("evidence id missing");
                return ToolResponse("call_2", "submit_answer",
                    JsonSerializer.Serialize(new
                    {
                        outcome = "answer",
                        text = "从盒里拿取对应的三张组件。",
                        evidence_ids = new[] { evidenceId }
                    }));
            }
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);
        var history = new List<ChatMessage>
        {
            new() { Role = "user", Content = "从盒里选哪三个" }
        };

        var result = await sut.ProcessWithEvidenceAsync("testgame", history);

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(2, llm.CallCount);
        var userMessages = llm.LastMessages.Where(message => message.Role == "user").ToList();
        Assert.Single(userMessages);
        Assert.Equal("从盒里选哪三个", userMessages[0].Content);
        Assert.DoesNotContain(llm.LastMessages, message =>
            message.Role == "system" && message.Content?.Contains("人数") == true);
    }

    [Fact]
    public async Task PlainFinalText_IsNotAcceptedAsAnswer_AndFailsBoundedly()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"explain","entity":"widget"}]}}""");
            return FinalResponse("绕过证据校验的普通文本");
        });
        var sut = new ChatOrchestratorService(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "legacy",
                MaxToolRounds = 5,
                MaxRequestSeconds = 10,
                MaxRepeatedFailures = 2,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.DoesNotContain("绕过证据校验的普通文本", result.Reply);
        Assert.True(llm.CallCount <= 4, $"calls={llm.CallCount}");
        Assert.Equal("protocol_error", result.Orchestration!.Outcome);
        Assert.Contains("plain_final_submission", result.Orchestration.ProtocolErrors);
        Assert.Contains("terminal_protocol_failed", result.Orchestration.ProtocolErrors);
        Assert.Contains(result.Orchestration.Timeline,
            span => span.Category == "protocol" && span.Name == "terminal_protocol_repair");
        Assert.Contains(result.Orchestration.Timeline,
            span => span.Category == "model" && span.Name == "model_unstructured_final");
        Assert.True(result.Orchestration.ProtocolRepairModelCallCount >= 1);
        Assert.NotEmpty(llm.AllMessages.Where(message =>
            message.Role == "user" && message.Content?.Contains("终态协议校验失败") == true));
    }

    [Fact]
    public async Task ProtocolRepair_ThatSuccessfullySubmitsAnswer_IsStillMarkedAndCounted()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "execute_plan",
                    """{"plan":{"queries":[{"relation":"explain","entity":"widget"}]}}""");
            if (call.Index == 2)
                return FinalResponse("先用普通文本绕过。");
            if (call.Index == 3)
            {
                var evidenceId = LastEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("evidence id missing");
                return ToolResponse("call_3", "submit_answer",
                    JsonSerializer.Serialize(new
                    {
                        outcome = "answer",
                        text = "纠偏后提交的答案。",
                        evidence_ids = new[] { evidenceId }
                    }));
            }
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("纠偏后提交的答案。", result.Reply);
        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Orchestration.ProtocolRepairModelCallCount);
        Assert.Contains(result.Orchestration.Timeline, span =>
            span.Category == "model"
            && span.ProtocolRepair
            && span.Name == "model_protocol_repair_answer_submission");
        Assert.True(result.Orchestration.ProtocolRepairMs >= 0);
    }

    [Fact]
    public async Task Conversation_CanExitThroughStructuredTerminalWithoutEvidence()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return ToolResponse("call_1", "submit_answer",
                    """{"outcome":"conversation","text":"你好，想了解哪款桌游的规则？"}""");
            return FinalResponse("UNEXPECTED");
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("你好，想了解哪款桌游的规则？", result.Reply);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal("conversation", result.Orchestration!.Outcome);
        Assert.Empty(result.Orchestration.ProtocolErrors);
        Assert.Empty(result.Evidence.Queries);
    }

    [Fact]
    public async Task RepeatedInvalidTerminalSubmissions_StopAtHardLimit_WithoutInfiniteLoop()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new ScriptedLlm(call =>
        {
            if (call.Tools is { Count: > 0 } && call.Index <= 3)
                return ToolResponse($"call_{call.Index}", "submit_answer",
                    """{"outcome":"answer","text":"缺少证据的回答。"}""");
            return FinalResponse("达到协议上限后的总结。");
        });
        var sut = new ChatOrchestratorService(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "legacy",
                MaxToolRounds = 3,
                MaxRequestSeconds = 10,
                MaxRepeatedFailures = 2,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.True(llm.CallCount <= 4, $"calls={llm.CallCount}");
        Assert.Contains("总结", result.Reply);
        Assert.Contains(result.Orchestration!.ProtocolErrors,
            error => error == "protocol_validation_error");
    }

    private static ChatOrchestratorService CreateService(ILLMService llm, GameRulesService rules)
        => new(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "legacy",
                MaxToolRounds = 5,
                MaxRequestSeconds = 10,
                MaxRepeatedFailures = 3,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxConversationMessages = 24,
                MaxEvidencePackChars = 60_000
            }),
            NullLogger<ChatOrchestratorService>.Instance);

    private static List<ChatMessage> History() =>
        new() { new ChatMessage { Role = "user", Content = "测试问题" } };

    private static LLMChatResponse ToolResponse(string id, string name, string arguments) => new()
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
                        new()
                        {
                            Id = id,
                            Function = new FunctionCall { Name = name, Arguments = arguments }
                        }
                    }
                }
            }
        }
    };

    private static LLMChatResponse FinalResponse(string content) => new()
    {
        Choices = new List<LLMChoice>
        {
            new() { Message = new ChatMessage { Role = "assistant", Content = content } }
        }
    };

    private static string? LastEvidenceId(List<ChatMessage> messages)
    {
        foreach (var message in messages.AsEnumerable().Reverse())
        {
            if (message.Role != "tool" || string.IsNullOrWhiteSpace(message.Content)) continue;
            try
            {
                using var doc = JsonDocument.Parse(message.Content);
                if (doc.RootElement.TryGetProperty("evidence_catalog", out var catalog)
                    && catalog.ValueKind == JsonValueKind.Array)
                {
                    foreach (var evidence in catalog.EnumerateArray())
                    {
                        if (evidence.TryGetProperty("evidence_id", out var id)
                            && id.ValueKind == JsonValueKind.String)
                            return id.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not a projected tool result.
            }
        }
        return null;
    }

    private sealed class ScriptedLlm : ILLMService
    {
        private readonly Func<ScriptedCall, LLMChatResponse> _handler;
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public List<ChatMessage> AllMessages { get; } = new();
        public List<ChatMessage> LastMessages { get; private set; } = new();

        public ScriptedLlm(Func<ScriptedCall, LLMChatResponse> handler) => _handler = handler;

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount);
            var snapshot = messages.Select(message => new ChatMessage
            {
                Role = message.Role,
                Content = message.Content,
                ToolCallId = message.ToolCallId
            }).ToList();
            lock (AllMessages)
            {
                AllMessages.AddRange(snapshot);
                LastMessages = snapshot;
            }
            return Task.FromResult(_handler(new ScriptedCall(index, messages, tools)));
        }
    }

    private sealed record ScriptedCall(
        int Index,
        List<ChatMessage> Messages,
        List<ToolDefinition>? Tools);
}
