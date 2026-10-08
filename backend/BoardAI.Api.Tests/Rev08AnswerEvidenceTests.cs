using System.Text.Json;
using BoardAI.Api.Controllers;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class Rev08AnswerEvidenceTests
{
    [Fact]
    public async Task ProcessWithEvidenceAsync_NormalRuleHit_IsTier1AndTraceableToRulesVersion()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLlm(call => call.Index == 1
            ? ToolCall(Plan("""{ "relation": "explain", "entity": "widget" }"""))
            : Final("最终回答"));
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("最终回答", result.Reply);
        var evidence = result.Evidence;
        Assert.Equal("tier1", evidence.Tier);
        Assert.True(evidence.IsComplete);
        Assert.True(evidence.HasData);
        Assert.Equal(1, evidence.QueryCount);
        Assert.Equal(1, evidence.OkCount);
        Assert.NotNull(evidence.RulesVersion);

        var query = Assert.Single(evidence.Queries);
        Assert.Equal("explain", query.Relation);
        Assert.Equal("widget", query.Entity);
        Assert.Equal("ok", query.Status);
        Assert.Equal("exact_id", query.Source);
        Assert.Equal(evidence.RulesVersion, query.RulesVersion);
        Assert.Contains(query.Matched, concept => concept.Id == "widget" && concept.Name == "小装置");
    }

    [Fact]
    public async Task ProcessWithEvidenceAsync_CompositePlanWithNoMatch_IsPartialNotTier1()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLlm(call => call.Index == 1
            ? ToolCall(Plan(
                """{ "relation": "explain", "entity": "widget" },"""
                + """{ "relation": "explain", "entity": "规则库里不存在的概念XYZ" }"""))
            : Final("部分回答"));
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        var evidence = result.Evidence;
        Assert.Equal("partial", evidence.Tier);
        Assert.False(evidence.IsComplete);
        Assert.True(evidence.HasData);
        Assert.Equal(2, evidence.QueryCount);
        Assert.Equal(1, evidence.OkCount);
        Assert.Equal(1, evidence.NoMatchCount);
        Assert.Equal(0, evidence.UnresolvedCount);
        Assert.NotNull(evidence.RulesVersion);

        Assert.All(evidence.Queries, query => Assert.Equal(evidence.RulesVersion, query.RulesVersion));

        var missing = Assert.Single(evidence.Queries, query => query.Status == "no_match");
        Assert.Contains("不存在", missing.Entity);
        Assert.False(string.IsNullOrWhiteSpace(missing.Message));

        var hit = Assert.Single(evidence.Queries, query => query.Status == "ok");
        Assert.Contains(hit.Matched, concept => concept.Id == "widget");
    }

    [Fact]
    public async Task ProcessWithEvidenceAsync_IdentifyOnlyCandidates_IsTier2WithCandidates()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLlm(call => call.Index == 1
            ? ToolCall(Plan("""{ "relation": "identify", "entity": "小装置" }"""))
            : Final("候选回答"));
        var sut = CreateService(llm, rules);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        var evidence = result.Evidence;
        Assert.Equal("tier2", evidence.Tier);
        Assert.False(evidence.IsComplete);
        Assert.False(evidence.HasData);
        Assert.True(evidence.HasCandidates);
        var query = Assert.Single(evidence.Queries);
        Assert.Equal("unresolved", query.Status);
        Assert.Contains(query.Candidates, candidate => candidate.Id == "widget");
    }

    [Fact]
    public async Task ProcessWithEvidenceAsync_IdentifyThenExactExplain_RecoversWithTrace()
    {
        using var fixture = new RulesFixture();
        fixture.WriteGameConcepts("白色组件", "执行动作");
        using var rules = fixture.CreateService();
        var llm = new FakeLlm(call => call.Index switch
        {
            1 => ToolCall(Plan("""{ "relation": "identify", "entity": "白色组件" }""")),
            2 => ToolCall(Plan("""{ "relation": "explain", "entity": "widget" }""")),
            _ => Final("已确认的小装置")
        });
        var result = await CreateService(llm, rules).ProcessWithEvidenceAsync("testgame", History());
        Assert.Equal("tier1", result.Evidence.Tier);
        Assert.Equal(0, result.Evidence.PendingCount);
        Assert.Equal(1, result.Evidence.UnresolvedCount);
        Assert.Equal(1, result.Evidence.Queries[0].ResolvedByQueryIndex);
    }

    [Fact]
    public async Task ChatController_Post_ReturnsEvidenceWithoutBreakingReply()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLlm(call => call.Index == 1
            ? ToolCall(Plan("""{ "relation": "explain", "entity": "widget" }"""))
            : Final("控制器最终回答"));
        var orchestrator = CreateService(llm, rules);
        var controller = new ChatController(orchestrator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var action = await controller.Post(new ChatRequest
        {
            GameId = "testgame",
            Messages = History()
        });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal("控制器最终回答", response.Reply);
        Assert.NotNull(response.Evidence);
        Assert.Equal("tier1", response.Evidence!.Tier);
        Assert.Single(response.Evidence.Queries);
    }

    [Fact]
    public void ChatResponse_WithoutEvidence_StaysReplyOnlyForOldClients()
    {
        var json = JsonSerializer.Serialize(new ChatResponse { Reply = "旧客户端回复" });
        using var document = JsonDocument.Parse(json);
        Assert.Equal("旧客户端回复", document.RootElement.GetProperty("Reply").GetString());
        Assert.False(document.RootElement.TryGetProperty("Evidence", out _));
    }

    private static ChatOrchestratorService CreateService(FakeLlm llm, GameRulesService rules)
        => new(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                MaxToolRounds = 3,
                MaxRequestSeconds = 10,
                MaxRepeatedFailures = 3,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxConversationMessages = 24
            }),
            NullLogger<ChatOrchestratorService>.Instance);

    private static List<ChatMessage> History()
        => new() { new ChatMessage { Role = "user", Content = "测试问题" } };

    private static string Plan(params string[] queries)
        => "{\"plan\":{\"queries\":[" + string.Join(",", queries) + "]}}";

    private static LLMChatResponse ToolCall(string arguments)
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
                                Id = "call_1",
                                Function = new FunctionCall { Name = "execute_plan", Arguments = arguments }
                            }
                        }
                    }
                }
            }
        };

    private static LLMChatResponse Final(string content)
        => new()
        {
            Choices = new List<LLMChoice>
            {
                new()
                {
                    Message = new ChatMessage { Role = "assistant", Content = content }
                }
            }
        };

    private sealed class FakeLlm : ILLMService
    {
        private readonly Func<FakeCall, LLMChatResponse> _handler;
        private int _callCount;

        public FakeLlm(Func<FakeCall, LLMChatResponse> handler)
        {
            _handler = handler;
        }

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _callCount);
            return Task.FromResult(_handler(new FakeCall(index)));
        }
    }

    private sealed record FakeCall(int Index);
}
