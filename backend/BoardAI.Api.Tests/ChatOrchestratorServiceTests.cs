using System.Diagnostics;
using BoardAI.Api.Controllers;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class ChatOrchestratorServiceTests
{
    private const string ValidPlanArguments = """{"plan":{"queries":[{"relation":"list"}]}}""";
    private const string InvalidPlanArguments = """{"plan":""";

    private static string DistinctPlanArguments(int index)
        => "{\"plan\":{\"queries\":[{\"relation\":\"list\",\"entity\":\"concept_" + index + "\"}]}}";

    [Fact]
    public async Task ProcessAsync_WhenLlmAlwaysRequestsTool_StopsAtRoundLimitAndSummarizesOnce()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLLMService(call =>
        {
            if (call.HasTools)
            {
                // 上限只用于让复现测试在失败时快速结束，避免旧实现无限循环。
                return Task.FromResult(call.Index > 20
                    ? FinalResponse("TOO_MANY_ROUNDS")
                    : ToolCallResponse(DistinctPlanArguments(call.Index), call.Index));
            }

            return Task.FromResult(FinalResponse("FINAL"));
        });
        var sut = CreateService(llm, rules, CreateOptions(maxToolRounds: 3));

        var reply = await sut.ProcessAsync("testgame", History());

        Assert.Equal("FINAL", reply);
        Assert.Equal(3, llm.ToolCallCount);
        Assert.Equal(1, llm.SummaryCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenToolKeepsFailingSameWay_StopsAtRepeatedFailureLimitAndSummarizesOnce()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLLMService(call =>
        {
            if (call.HasTools)
            {
                return Task.FromResult(call.Index > 20
                    ? FinalResponse("TOO_MANY_ROUNDS")
                    : ToolCallResponse(InvalidPlanArguments, call.Index));
            }

            return Task.FromResult(FinalResponse("FINAL"));
        });
        var sut = CreateService(llm, rules, CreateOptions(maxToolRounds: 10, maxRepeatedFailures: 3));

        var reply = await sut.ProcessAsync("testgame", History());

        Assert.Equal("FINAL", reply);
        Assert.Equal(3, llm.ToolCallCount);
        Assert.Equal(1, llm.SummaryCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenSingleRoundExceedsToolCallLimit_OnlyExecutesLimitAndSummarizes()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        List<ChatMessage>? summaryMessages = null;
        var llm = new FakeLLMService(call =>
        {
            if (call.HasTools)
            {
                return Task.FromResult(MultiToolCallResponse(
                    call.Index,
                    DistinctPlanArguments(1),
                    DistinctPlanArguments(2),
                    DistinctPlanArguments(3),
                    DistinctPlanArguments(4),
                    DistinctPlanArguments(5)));
            }

            summaryMessages = call.Messages;
            return Task.FromResult(FinalResponse("FINAL"));
        });
        var sut = CreateService(llm, rules, CreateOptions(
            maxToolRounds: 3,
            maxToolCallsPerRound: 2,
            maxTotalToolCalls: 10));

        var reply = await sut.ProcessAsync("testgame", History());

        Assert.Equal("FINAL", reply);
        Assert.Equal(1, llm.ToolCallCount);
        Assert.Equal(1, llm.SummaryCount);
        Assert.NotNull(summaryMessages);
        var toolMessages = summaryMessages!.Where(message => message.Role == "tool").ToList();
        Assert.Equal(5, toolMessages.Count);
        Assert.Equal(2, toolMessages.Count(message => !message.Content!.Contains("未执行")));
        Assert.Equal(3, toolMessages.Count(message => message.Content!.Contains("未执行")));
    }

    [Fact]
    public async Task ProcessAsync_WhenTotalToolCallsExceedLimit_StopsAndSummarizes()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        List<ChatMessage>? summaryMessages = null;
        var llm = new FakeLLMService(call =>
        {
            if (call.HasTools)
            {
                return Task.FromResult(ToolCallResponse(DistinctPlanArguments(call.Index), call.Index));
            }

            summaryMessages = call.Messages;
            return Task.FromResult(FinalResponse("FINAL"));
        });
        var sut = CreateService(llm, rules, CreateOptions(
            maxToolRounds: 6,
            maxToolCallsPerRound: 4,
            maxTotalToolCalls: 2));

        var reply = await sut.ProcessAsync("testgame", History());

        Assert.Equal("FINAL", reply);
        Assert.Equal(3, llm.ToolCallCount);
        Assert.Equal(1, llm.SummaryCount);
        Assert.NotNull(summaryMessages);
        var toolMessages = summaryMessages!.Where(message => message.Role == "tool").ToList();
        Assert.Equal(3, toolMessages.Count);
        Assert.Equal(2, toolMessages.Count(message => !message.Content!.Contains("未执行")));
        Assert.Equal(1, toolMessages.Count(message => message.Content!.Contains("未执行")));
    }

    [Fact]
    public async Task ProcessAsync_WhenRequestIsCancelled_StopsWithoutFurtherRequestsOrSummary()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLLMService(async call =>
        {
            firstCallStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, call.Token);
            return FinalResponse("NEVER");
        });
        var sut = CreateService(llm, rules, CreateOptions(maxToolRounds: 3, maxRequestSeconds: 30));

        using var cts = new CancellationTokenSource();
        var processTask = sut.ProcessAsync("testgame", History(), cancellationToken: cts.Token);
        await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, llm.ToolCallCount);
        Assert.Equal(0, llm.SummaryCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenTotalBudgetExpires_ReturnsIncompleteReplyInsteadOfThrowing()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLLMService(async call =>
        {
            firstCallStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, call.Token);
            return FinalResponse("NEVER");
        });
        var sut = CreateService(llm, rules, CreateOptions(maxToolRounds: 3, maxRequestSeconds: 1));

        var sw = Stopwatch.StartNew();
        var processTask = sut.ProcessAsync("testgame", History());
        await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var reply = await processTask.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.Contains("时间上限", reply);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"elapsed={sw.Elapsed}");
        Assert.Equal(1, llm.ToolCallCount);
        Assert.Equal(0, llm.SummaryCount);
    }

    [Fact]
    public async Task ProcessAsync_NormalMultiStepQuery_CompletesWithoutTriggeringLimits()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new FakeLLMService(call =>
        {
            if (!call.HasTools)
                return Task.FromResult(FinalResponse("UNEXPECTED_SUMMARY"));

            if (call.Index == 1)
                return Task.FromResult(ToolCallResponse(ValidPlanArguments, call.Index));

            return Task.FromResult(FinalResponse("FINAL"));
        });
        var sut = CreateService(llm, rules, CreateOptions(maxToolRounds: 3, maxRepeatedFailures: 3));

        var reply = await sut.ProcessAsync("testgame", History());

        Assert.Equal("FINAL", reply);
        Assert.Equal(2, llm.ToolCallCount);
        Assert.Equal(0, llm.SummaryCount);
    }

    [Fact]
    public async Task ChatController_Post_PropagatesRequestAbortedToOrchestrator()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLLMService(async call =>
        {
            firstCallStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, call.Token);
            return FinalResponse("NEVER");
        });
        var sut = CreateService(llm, rules, CreateOptions(maxToolRounds: 3, maxRequestSeconds: 30));

        using var requestAborted = new CancellationTokenSource();
        var controller = new ChatController(sut)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = requestAborted.Token }
            }
        };

        var actionTask = controller.Post(new ChatRequest
        {
            GameId = "testgame",
            Messages = History()
        });
        await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        requestAborted.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => actionTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, llm.ToolCallCount);
        Assert.Equal(0, llm.SummaryCount);
    }

    private static ChatOrchestratorService CreateService(
        FakeLLMService llm,
        GameRulesService rules,
        LLMOptions options)
        => new(llm, rules, Options.Create(options), NullLogger<ChatOrchestratorService>.Instance);

    private static LLMOptions CreateOptions(
        int maxToolRounds = 3,
        int maxRequestSeconds = 10,
        int maxRepeatedFailures = 3,
        int maxToolCallsPerRound = 4,
        int maxTotalToolCalls = 12)
        => new()
        {
            MaxToolRounds = maxToolRounds,
            MaxRequestSeconds = maxRequestSeconds,
            MaxRepeatedFailures = maxRepeatedFailures,
            MaxToolCallsPerRound = maxToolCallsPerRound,
            MaxTotalToolCalls = maxTotalToolCalls,
            MaxConversationMessages = 24
        };

    private static List<ChatMessage> History() =>
        new() { new ChatMessage { Role = "user", Content = "测试问题" } };

    private static LLMChatResponse ToolCallResponse(string arguments, int index)
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
                                Function = new FunctionCall
                                {
                                    Name = "execute_plan",
                                    Arguments = arguments
                                }
                            }
                        }
                    }
                }
            }
        };

    private static LLMChatResponse MultiToolCallResponse(int index, params string[] arguments)
        => new()
        {
            Choices = new List<LLMChoice>
            {
                new()
                {
                    Message = new ChatMessage
                    {
                        Role = "assistant",
                        ToolCalls = arguments
                            .Select((argument, offset) => new ToolCall
                            {
                                Id = $"call_{index}_{offset}",
                                Function = new FunctionCall
                                {
                                    Name = "execute_plan",
                                    Arguments = argument
                                }
                            })
                            .ToList()
                    }
                }
            }
        };

    private static LLMChatResponse FinalResponse(string content)
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

    private sealed class FakeLLMService : ILLMService
    {
        private readonly Func<FakeCall, Task<LLMChatResponse>> _handler;
        private int _toolCallCount;
        private int _summaryCount;

        public FakeLLMService(Func<FakeCall, Task<LLMChatResponse>> handler)
        {
            _handler = handler;
        }

        public int ToolCallCount => Volatile.Read(ref _toolCallCount);
        public int SummaryCount => Volatile.Read(ref _summaryCount);

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            var hasTools = tools is { Count: > 0 };
            var index = hasTools
                ? Interlocked.Increment(ref _toolCallCount)
                : Interlocked.Increment(ref _summaryCount);

            return _handler(new FakeCall(hasTools, index, messages, cancellationToken));
        }
    }

    private sealed record FakeCall(
        bool HasTools,
        int Index,
        List<ChatMessage> Messages,
        CancellationToken Token);
}
