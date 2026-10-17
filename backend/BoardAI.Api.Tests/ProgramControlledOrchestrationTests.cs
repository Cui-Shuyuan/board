using System.Text.Json;
using System.Text.Json.Nodes;
using BoardAI.Api.Controllers;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class ProgramControlledOrchestrationTests
{
    [Fact]
    public async Task NormalRuleQuestion_UsesUnderstandAndRespondOnly_WithFormalEvidence()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
            {
                return Structured("submit_understanding", """
                {
                  "request_kind":"rules",
                  "questions":[{"entity":"widget","relation":"explain","intent":"小装置是什么"}]
                }
                """);
            }
            if (call.ToolName == "submit_answer")
            {
                return Structured("submit_answer", AnswerJson("answer", "小装置是测试对象。", "q1", "answered", "e1"));
            }
            throw new InvalidOperationException("unexpected tool " + call.ToolName);
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置是什么"));

        Assert.Equal("小装置是测试对象。", result.Reply);
        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(2, result.Orchestration.ModelCallCount);
        Assert.Equal(0, result.Orchestration.SupplementCount);
        Assert.Equal(0, result.Orchestration.RepairCount);
        Assert.True(result.Evidence.EvidenceReadCount >= 1);
        Assert.Equal(1, result.Evidence.CitedEvidenceCount);
        Assert.Equal("answer", result.Evidence.OrchestrationOutcome);
        Assert.Equal("q1", Assert.Single(result.Orchestration.Questions).QuestionId);
        Assert.Equal("answered", result.Orchestration.Questions[0].OutcomeStatus);
        Assert.Equal(2, llm.CallCount);
    }

    [Fact]
    public async Task Conversation_EndsAfterUnderstanding_WithoutRuleRetrieval()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            Assert.Equal("submit_understanding", call.ToolName);
            return Structured("submit_understanding", """
            {"request_kind":"conversation","reply":"你好，我在。","questions":[]}
            """);
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("你好"));

        Assert.Equal("你好，我在。", result.Reply);
        Assert.Equal("conversation", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Orchestration.ModelCallCount);
        Assert.Equal(0, result.Evidence.EvidenceReadCount);
        Assert.Equal(1, llm.CallCount);
    }

    [Fact]
    public async Task AmbiguousObject_AllowsClarifyWithoutCandidateConfirmationLoop()
    {
        using var fixture = new RulesFixture();
        AddSecondWidget(fixture);

        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"装置","relation":"explain","intent":"装置是什么"}]}
                """);
            return Structured("submit_answer", AnswerJson("clarify", "你指的是小装置还是大装置？", "q1", "clarify", null));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("装置是什么"));

        Assert.Equal("clarify", result.Orchestration!.Outcome);
        Assert.Equal(0, result.Orchestration.RepairCount);
        Assert.Equal(2, result.Orchestration.ModelCallCount);
        Assert.Equal(0, result.Evidence.PendingCount);
        Assert.Equal(2, llm.CallCount);
    }

    [Fact]
    public async Task NeedMore_RunsOneBoundedSupplementThenAnswers()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var calls = 0;
        var llm = new FakeProgramLlm(call =>
        {
            calls++;
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"widget","relation":"explain","intent":"小装置怎么使用"}]}
                """);
            if (calls == 2)
            {
                return Structured("submit_answer", """
                {
                  "kind":"need_more",
                  "reply":"",
                  "question_results":[{"question_id":"q1","status":"unavailable","evidence_ids":[]}],
                  "need_more":{
                    "question_id":"q1",
                    "missing_information":"还需要使用条件",
                    "supplement_entity":"use_widget",
                    "supplement_relation":"condition",
                    "supplement_intent":"使用小装置需要什么条件"
                  }
                }
                """);
            }
            return Structured("submit_answer", AnswerJson("answer", "需要满足条件后再使用。", "q1", "answered", "e1"));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置怎么使用"));

        Assert.True(result.Orchestration!.Outcome == "answer",
            "outcome=" + result.Orchestration.Outcome + " errors=" + string.Join(" | ", result.Orchestration.ProtocolErrors) + " reply=" + result.Reply);
        Assert.Equal(3, result.Orchestration.ModelCallCount);
        Assert.Equal(1, result.Orchestration.SupplementCount);
        Assert.Equal(0, result.Orchestration.RepairCount);
        Assert.Equal(3, llm.CallCount);
    }

    [Fact]
    public async Task SecondNeedMore_StopsWithoutAdditionalModelCall()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"widget","relation":"explain","intent":"小装置怎么使用"}]}
                """);
            if (call.Index == 2)
            {
                return Structured("submit_answer", """
                {
                  "kind":"need_more","reply":"",
                  "question_results":[{"question_id":"q1","status":"unavailable","evidence_ids":[]}],
                  "need_more":{"question_id":"q1","missing_information":"条件","supplement_entity":"use_widget","supplement_relation":"condition","supplement_intent":"使用条件"}
                }
                """);
            }
            return Structured("submit_answer", """
            {
              "kind":"need_more","reply":"",
              "question_results":[{"question_id":"q1","status":"unavailable","evidence_ids":[]}],
              "need_more":{"question_id":"q1","missing_information":"仍缺条件","supplement_entity":"widget","supplement_relation":"explain","supplement_intent":"再补充"}
            }
            """);
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置怎么使用"));

        Assert.True(result.Orchestration!.Outcome == "unavailable",
            "outcome=" + result.Orchestration.Outcome + " errors=" + string.Join(" | ", result.Orchestration.ProtocolErrors) + " reply=" + result.Reply);
        Assert.Equal(3, result.Orchestration.ModelCallCount);
        Assert.Equal(1, result.Orchestration.SupplementCount);
        Assert.Equal(3, llm.CallCount);
    }

    [Fact]
    public async Task InvalidEvidenceReference_TriggersOneRepairBeforeAcceptance()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"widget","relation":"explain","intent":"小装置是什么"}]}
                """);
            if (call.Index == 2)
                return Structured("submit_answer", AnswerJson("answer", "错误引用。", "q1", "answered", "e999"));
            return Structured("submit_answer", AnswerJson("answer", "小装置是测试对象。", "q1", "answered", "e1"));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置是什么"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Orchestration.RepairCount);
        Assert.Equal(3, result.Orchestration.ModelCallCount);
        Assert.Contains(result.Orchestration.ProtocolErrors, error => error.Contains("evidence_id", StringComparison.Ordinal));
        Assert.Equal(3, llm.CallCount);
    }

    [Fact]
    public async Task MultiQuestion_NotNeededQueryIsAcceptedWithoutForcingAnotherRetrieval()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {
                  "request_kind":"rules",
                  "questions":[
                    {"entity":"widget","relation":"explain","intent":"小装置是什么"},
                    {"entity":"use_widget","relation":"condition","intent":"使用条件"}
                  ]
                }
                """);
            return Structured("submit_answer", """
            {
              "kind":"answer",
              "reply":"小装置是测试对象。",
              "question_results":[
                {"question_id":"q1","status":"answered","evidence_ids":["e1"]},
                {"question_id":"q2","status":"not_needed","evidence_ids":[],"reason":"与用户实际问的子问题重复"}
              ]
            }
            """);
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置是什么"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(2, result.Orchestration.ModelCallCount);
        Assert.Equal(0, result.Orchestration.RepairCount);
        Assert.Equal("not_needed", result.Orchestration.Questions[1].OutcomeStatus);
    }

    [Fact]
    public async Task UnknownObject_ReturnsUnavailableWithoutTreatingLackOfMatchAsAbsoluteNonExistence()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"完全不存在的对象XYZ","relation":"explain","intent":"它是什么"}]}
                """);
            return Structured("submit_answer", AnswerJson("unavailable", "规则库里没有确认到这个对象的信息。", "q1", "unavailable", null));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("完全不存在的对象XYZ是什么"));

        Assert.Equal("unavailable", result.Orchestration!.Outcome);
        Assert.Equal("unavailable", result.Evidence.OrchestrationOutcome);
        Assert.Contains("没有确认", result.Reply);
        Assert.Equal(0, result.Orchestration.RepairCount);
    }

    [Fact]
    public async Task FormalLocalSlot_IsReadAsEvidenceWithOwnerPath()
    {
        using var fixture = new RulesFixture();
        WriteSlotConcepts(fixture);
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"board_a.point_bonus","relation":"explain","intent":"A 面板奖励槽是什么"}]}
                """);
            return Structured("submit_answer", AnswerJson("answer", "这是 A 面板的奖励槽。", "q1", "answered", "e1"));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("A 面板奖励槽是什么"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Evidence.EvidenceReadCount);
        var matched = Assert.Single(result.Evidence.Queries[0].Matched);
        Assert.Equal("board_a.point_bonus", matched.Id);
        Assert.DoesNotContain(result.Evidence.Queries[0].Matched, item => item.Id == "board_b.point_bonus");
    }

    [Fact]
    public async Task CandidateFormalRead_CanBeCitedWithoutAnotherConfirmationRound()
    {
        using var fixture = new RulesFixture();
        AddSecondWidget(fixture);
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"装置","relation":"explain","intent":"装置是什么"}]}
                """);
            return Structured("submit_answer", AnswerJson("answer", "两个装置都是测试组件。", "q1", "answered", "e1"));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("装置是什么"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(0, result.Orchestration.RepairCount);
        Assert.Equal(2, result.Orchestration.ModelCallCount);
        Assert.True(result.Evidence.EvidenceReadCount >= 1);
        Assert.True(result.Orchestration.Questions[0].CandidateCount >= 2);
    }

    [Fact]
    public async Task MalformedUnderstandingJson_RepairsOnceThenContinues()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding" && call.Index == 1)
                return Structured("submit_understanding", "{bad json", call.Index);
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"widget","relation":"explain","intent":"小装置是什么"}]}
                """, call.Index);
            return Structured("submit_answer", AnswerJson("answer", "小装置是测试对象。", "q1", "answered", "e1"), call.Index);
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置是什么"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Orchestration.RepairCount);
        Assert.Equal(3, result.Orchestration.ModelCallCount);
        Assert.Contains(result.Orchestration.ProtocolErrors, error => error.Contains("JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedRepairBudget_IsNotExceededAfterSupplement()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"widget","relation":"explain","intent":"小装置怎么使用"}]}
                """, call.Index);
            if (call.Index == 2)
            {
                return Structured("submit_answer", """
                {
                  "kind":"need_more","reply":"",
                  "question_results":[{"question_id":"q1","status":"unavailable","evidence_ids":[]}],
                  "need_more":{"question_id":"q1","missing_information":"条件","supplement_entity":"use_widget","supplement_relation":"condition","supplement_intent":"使用条件"}
                }
                """, call.Index);
            }
            return Structured("submit_answer", AnswerJson("answer", "错误引用。", "q1", "answered", "e999"), call.Index);
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置怎么使用"));

        Assert.Equal("protocol_error", result.Orchestration!.Outcome);
        Assert.Equal(4, result.Orchestration.ModelCallCount);
        Assert.Equal(1, result.Orchestration.RepairCount);
        Assert.Equal(4, llm.CallCount);
    }

    [Fact]
    public async Task RequestTimeout_ReturnsIncompleteReplyOnProgramPath()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.Token);
            return Structured("submit_understanding", """{"request_kind":"conversation","reply":"x","questions":[]}""");
        });
        var sut = new ChatOrchestratorService(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "program",
                MaxModelCallsPerRequest = 4,
                MaxRequestSeconds = 1,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("测试"));

        Assert.Contains("时间上限", result.Reply);
        Assert.Equal("request_timeout", result.Orchestration!.Outcome);
        Assert.Equal(1, llm.CallCount);
    }

    [Fact]
    public async Task CandidateSummaryIdCannotBeUsedAsEvidence()
    {
        using var fixture = new RulesFixture();
        AddSecondWidget(fixture);
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"装置","relation":"explain","intent":"装置是什么"}]}
                """);
            if (call.Index == 2)
                return Structured("submit_answer", AnswerJson("answer", "候选摘要不能当事实。", "q1", "answered", "large_widget"));
            return Structured("submit_answer", AnswerJson("answer", "装置是测试组件。", "q1", "answered", "e1"));
        });
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("装置是什么"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Orchestration.RepairCount);
        Assert.Contains(result.Orchestration.ProtocolErrors, error => error.Contains("evidence_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvidencePackBudget_OmitsLowPriorityEvidenceAndReportsIt()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[
                  {"entity":"widget","relation":"explain","intent":"小装置是什么"},
                  {"entity":"use_widget","relation":"condition","intent":"使用小装置需要什么条件"}
                ]}
                """);
            return Structured("submit_answer", """
            {
              "kind":"answer",
              "reply":"小装置是测试对象。",
              "question_results":[
                {"question_id":"q1","status":"answered","evidence_ids":["e1"]},
                {"question_id":"q2","status":"not_needed","evidence_ids":[],"reason":"预算测试中不需要额外子问题"}
              ]
            }
            """);
        });
        var sut = new ChatOrchestratorService(
            llm, rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "program",
                MaxModelCallsPerRequest = 4,
                MaxCandidatesPerQuestion = 3,
                MaxEvidencePerQuestion = 8,
                MaxEvidenceTotal = 1,
                MaxEvidenceJsonChars = 4000,
                MaxEvidencePackChars = 60000,
                MaxSupplementQueries = 1,
                MaxRequestSeconds = 10,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History("小装置是什么，使用小装置需要什么条件"));

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.Equal(1, result.Evidence.EvidenceReadCount);
        Assert.True(result.Orchestration.OmittedEvidenceCount > 0);
    }

    [Fact]
    public async Task ChatController_ProgramMode_ReturnsOrchestrationTraceAndReply()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeProgramLlm(call =>
        {
            if (call.ToolName == "submit_understanding")
                return Structured("submit_understanding", """
                {"request_kind":"rules","questions":[{"entity":"widget","relation":"explain","intent":"小装置是什么"}]}
                """);
            return Structured("submit_answer", AnswerJson("answer", "小装置是测试对象。", "q1", "answered", "e1"));
        });
        var sut = CreateService(llm, rules);
        var controller = new ChatController(sut)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var action = await controller.Post(new ChatRequest
        {
            GameId = "testgame",
            Messages = History("小装置是什么")
        });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal("小装置是测试对象。", response.Reply);
        Assert.NotNull(response.Evidence);
        Assert.NotNull(response.Orchestration);
        Assert.Equal("answer", response.Orchestration!.Outcome);
        Assert.Equal(2, response.Orchestration.ModelCallCount);
    }

    [Fact]
    public async Task ClientCancellation_PropagatesFromProgramPath()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeProgramLlm(async call =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, call.Token);
            return Structured("submit_understanding", """{"request_kind":"conversation","reply":"x","questions":[]}""");
        });
        var sut = CreateService(llm, rules);
        using var cts = new CancellationTokenSource();
        var task = sut.ProcessWithEvidenceAsync("testgame", History("测试"), cancellationToken: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static void AddSecondWidget(RulesFixture fixture)
    {
        var root = JsonNode.Parse(File.ReadAllText(fixture.GameConceptsPath))!.AsObject();
        root["objects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "large_widget",
            ["name"] = new JsonObject { ["zh"] = "大装置", ["en"] = "Large Widget" },
            ["description"] = new JsonObject { ["zh"] = "另一个装置" }
        });
        File.WriteAllText(fixture.GameConceptsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSlotConcepts(RulesFixture fixture)
    {
        File.WriteAllText(fixture.GameConceptsPath, """
        {
          "objects": [
            {
              "id": "board_a",
              "name": { "zh": "A 面板", "en": "Board A" },
              "description": { "zh": "第一个测试面板" },
              "slots": [
                {
                  "id": "point_bonus",
                  "name": { "zh": "A 奖励槽" },
                  "description": { "zh": "A 面板上的奖励槽" }
                }
              ]
            },
            {
              "id": "board_b",
              "name": { "zh": "B 面板", "en": "Board B" },
              "description": { "zh": "第二个测试面板" },
              "slots": [
                {
                  "id": "point_bonus",
                  "name": { "zh": "B 奖励槽" },
                  "description": { "zh": "B 面板上的奖励槽" }
                }
              ]
            }
          ],
          "actions": [],
          "triggers": [],
          "conditions": []
        }
        """);
    }

    private static ChatOrchestratorService CreateService(FakeProgramLlm llm, GameRulesService rules)
        => new(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "program",
                MaxModelCallsPerRequest = 4,
                MaxCandidatesPerQuestion = 3,
                MaxEvidencePerQuestion = 8,
                MaxEvidenceTotal = 24,
                MaxEvidenceJsonChars = 4000,
                MaxEvidencePackChars = 60000,
                MaxSupplementQueries = 1,
                MaxRequestSeconds = 10,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

    private static List<ChatMessage> History(string question) =>
        new() { new ChatMessage { Role = "user", Content = question } };

    private static string AnswerJson(
        string kind,
        string reply,
        string questionId,
        string status,
        string? evidenceId)
    {
        var evidence = evidenceId == null ? "[]" : $"[\"{evidenceId}\"]";
        return $$"""
        {
          "kind":"{{kind}}",
          "reply":{{JsonSerializer.Serialize(reply)}},
          "question_results":[{"question_id":"{{questionId}}","status":"{{status}}","evidence_ids":{{evidence}}}]
        }
        """;
    }

    private static LLMChatResponse Structured(string toolName, string arguments, int index = 1)
        => new()
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
                                Id = $"call_{index}",
                                Function = new FunctionCall { Name = toolName, Arguments = arguments }
                            }
                        }
                    }
                }
            }
        };

    private sealed class FakeProgramLlm : ILLMService
    {
        private readonly Func<FakeCall, Task<LLMChatResponse>> _handler;
        private int _calls;

        public FakeProgramLlm(Func<FakeCall, LLMChatResponse> handler)
            : this(call => Task.FromResult(handler(call)))
        {
        }

        public FakeProgramLlm(Func<FakeCall, Task<LLMChatResponse>> handler) => _handler = handler;

        public int CallCount => Volatile.Read(ref _calls);

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls);
            var toolName = tools?.FirstOrDefault()?.Function.Name ?? "";
            return _handler(new FakeCall(index, toolName, messages, cancellationToken));
        }
    }

    private sealed record FakeCall(int Index, string ToolName, List<ChatMessage> Messages, CancellationToken Token);
}
