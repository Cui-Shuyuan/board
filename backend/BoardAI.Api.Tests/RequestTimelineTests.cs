using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class RequestTimelineTests
{
    [Fact]
    public async Task LegacyRequest_RecordsModelAndToolSpans_ThatReconcileWithTotalElapsed()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new TimedLLMService(async call =>
        {
            if (call.HasTools)
            {
                await Task.Delay(call.Index == 1 ? 25 : 15, call.Token);
                return call.Index == 1
                    ? ToolResponse("""{"plan":{"queries":[{"relation":"list"}]}}""")
                    : FinalResponse("已完成");
            }

            await Task.Delay(10, call.Token);
            return FinalResponse("已完成");
        });
        var sut = new ChatOrchestratorService(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "legacy",
                MaxToolRounds = 3,
                MaxRequestSeconds = 10,
                MaxConversationMessages = 12
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        var result = await sut.ProcessWithEvidenceAsync("testgame",
            new List<ChatMessage> { new() { Role = "user", Content = "测试问题" } });

        var trace = Assert.IsType<ChatOrchestrationTrace>(result.Orchestration);
        Assert.Equal("legacy", trace.Mode);
        Assert.Equal(2, trace.ModelCallCount);
        Assert.Contains(trace.Timeline, span => span.Category == "tool" && span.Name == "execute_plan");
        Assert.Contains(trace.Timeline, span => span.Category == "model" && span.PromptTokens == 11);
        var accounting = Assert.IsType<ChatTimelineAccounting>(trace.TimelineAccounting);
        Assert.True(accounting.IsReconciled);
        Assert.True(accounting.CoveredMs <= accounting.ElapsedMs + 1);
        Assert.True(accounting.UnaccountedMs >= 0);
    }

    [Fact]
    public void ParallelSpans_AreReportedAsOverlap_NotSummedAsElapsed()
    {
        var accounting = ChatTimelineAccountingCalculator.Compute(new[]
        {
            new ChatTimelineSpan { Name = "vector", Category = "search", StartMs = 0, DurationMs = 100, ConcurrencyGroup = "search" },
            new ChatTimelineSpan { Name = "keyword", Category = "search", StartMs = 10, DurationMs = 100, ConcurrencyGroup = "search" }
        }, elapsedMs: 110);

        Assert.True(accounting.IsReconciled);
        Assert.Equal(110, accounting.CoveredMs);
        Assert.Equal(90, accounting.OverlapMs);
        Assert.Equal(0, accounting.UnaccountedMs);
        Assert.Contains("search", accounting.ParallelGroups);
    }

    private static LLMChatResponse ToolResponse(string arguments) => new()
    {
        Usage = new LLMUsage { PromptTokens = 11, CompletionTokens = 3 },
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

    private static LLMChatResponse FinalResponse(string content) => new()
    {
        Usage = new LLMUsage { PromptTokens = 11, CompletionTokens = 5 },
        Choices = new List<LLMChoice>
        {
            new() { Message = new ChatMessage { Role = "assistant", Content = content } }
        }
    };

    private sealed class TimedLLMService : ILLMService
    {
        private readonly Func<TimedCall, Task<LLMChatResponse>> _handler;
        private int _toolCalls;

        public TimedLLMService(Func<TimedCall, Task<LLMChatResponse>> handler) => _handler = handler;

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            var hasTools = tools is { Count: > 0 };
            var index = hasTools ? Interlocked.Increment(ref _toolCalls) : 1;
            return _handler(new TimedCall(hasTools, index, cancellationToken));
        }
    }

    private sealed record TimedCall(bool HasTools, int Index, CancellationToken Token);
}
