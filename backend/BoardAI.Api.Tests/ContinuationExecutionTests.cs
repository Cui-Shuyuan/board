using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class ContinuationExecutionTests
{
    [Fact]
    public async Task MembersContinuation_IsRegisteredAndExecutesNextOffset()
    {
        using var root = new TempRulesRoot("""
        {
          "objects": [
            {
              "id": "collection",
              "name": { "zh": "集合" },
              "members": [
                { "id": "member_1", "name": { "zh": "成员一" } },
                { "id": "member_2", "name": { "zh": "成员二" } },
                { "id": "member_3", "name": { "zh": "成员三" } }
              ]
            }
          ],
          "actions": [], "triggers": [], "conditions": []
        }
        """);
        JsonElement? secondMemberSet = null;
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return Tool("execute_plan", """{"plan":{"queries":[{"relation":"members","entity":"collection","offset":0,"limit":1}]}}""");
            if (call.Index == 2)
            {
                var continuation = FindContinuation(call.Messages, "members")
                    ?? throw new InvalidOperationException("members continuation missing");
                return Tool("execute_plan", JsonSerializer.Serialize(new
                {
                    plan = new
                    {
                        queries = new[]
                        {
                            new
                            {
                                relation = "explain",
                                entity = "ignored",
                                continuation_handle = continuation.Handle
                            }
                        }
                    }
                }));
            }
            if (call.Index == 3)
            {
                secondMemberSet = FindEvidenceContent(call.Messages, QueryPayloadTypes.MemberSet);
                var evidenceId = FindFirstEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("continuation evidence missing");
                return Tool("submit_answer", JsonSerializer.Serialize(new
                {
                    outcome = "answer",
                    text = "已读取第二页成员。",
                    evidence_ids = new[] { evidenceId }
                }));
            }
            return Final("UNEXPECTED");
        });
        using var rules = root.CreateService();
        var sut = CreateService(llm, rules, 60_000);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.NotNull(secondMemberSet);
        Assert.Equal(1, secondMemberSet!.Value.GetProperty("offset").GetInt32());
        Assert.Equal(1, secondMemberSet.Value.GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task UnregisteredContinuationHandle_IsRejectedWithoutExecutingArbitraryTarget()
    {
        using var root = new TempRulesRoot("""
        {
          "objects": [ { "id": "widget", "name": { "zh": "小装置" } } ],
          "actions": [], "triggers": [], "conditions": []
        }
        """);
        var toolError = string.Empty;
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return Tool("execute_plan",
                    """{"plan":{"queries":[{"relation":"explain","entity":"widget","continuation_handle":"cont_fabricated"}]}}""");
            if (call.Index == 2)
            {
                toolError = call.Messages.LastOrDefault(message => message.Role == "tool")?.Content ?? "";
                return Tool("submit_answer",
                    """{"outcome":"unavailable","text":"无法续读。","unavailable_reason":"unresolved_identity"}""");
            }
            return Final("UNEXPECTED");
        });
        using var rules = root.CreateService();
        var sut = CreateService(llm, rules, 60_000);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("unavailable", result.Orchestration!.Outcome);
        using var errorDoc = JsonDocument.Parse(toolError);
        var errorText = errorDoc.RootElement.GetProperty("error").GetString() ?? "";
        Assert.Contains("未登记", errorText);
        Assert.DoesNotContain("evidence_catalog", toolError);
    }

    [Fact]
    public async Task ReadEvidenceContinuation_ReexecutesRegisteredTarget_AndDeliversUntruncatedEvidence()
    {
        var longA = new string('甲', 700);
        var longB = new string('乙', 700);
        using var root = new TempRulesRoot($$"""
        {
          "objects": [
            { "id": "a", "name": { "zh": "甲规则" }, "description": { "zh": "{{longA}}" } },
            { "id": "b", "name": { "zh": "乙规则" }, "description": { "zh": "{{longB}}" } }
          ],
          "actions": [], "triggers": [], "conditions": []
        }
        """);
        string? omittedEntity = null;
        JsonElement? continuationEvidence = null;
        var llm = new ScriptedLlm(call =>
        {
            if (call.Index == 1)
                return Tool("execute_plan", """{"plan":{"queries":[{"relation":"explain","entity":"a"},{"relation":"explain","entity":"b"}]}}""");
            if (call.Index == 2)
            {
                var continuation = FindContinuation(call.Messages, "read_evidence")
                    ?? throw new InvalidOperationException("read_evidence continuation missing");
                omittedEntity = continuation.Entity;
                return Tool("execute_plan", JsonSerializer.Serialize(new
                {
                    plan = new
                    {
                        queries = new[]
                        {
                            new
                            {
                                relation = "explain",
                                entity = "ignored",
                                continuation_handle = continuation.Handle
                            }
                        }
                    }
                }));
            }
            if (call.Index == 3)
            {
                continuationEvidence = FindEvidenceContent(call.Messages, QueryPayloadTypes.Definition);
                var evidenceId = FindFirstEvidenceId(call.Messages)
                    ?? throw new InvalidOperationException("continuation evidence missing");
                return Tool("submit_answer", JsonSerializer.Serialize(new
                {
                    outcome = "answer",
                    text = "已续读完整规则。",
                    evidence_ids = new[] { evidenceId }
                }));
            }
            return Final("UNEXPECTED");
        });
        using var rules = root.CreateService();
        var sut = CreateService(llm, rules, 2000);

        var result = await sut.ProcessWithEvidenceAsync("testgame", History());

        Assert.Equal("answer", result.Orchestration!.Outcome);
        Assert.NotNull(omittedEntity);
        Assert.NotNull(continuationEvidence);
        Assert.Equal(omittedEntity, continuationEvidence!.Value.GetProperty("id").GetString());
        var deliveredDescription = continuationEvidence.Value.GetProperty("description").GetProperty("zh").GetString();
        Assert.Equal(700, deliveredDescription?.Length ?? 0);
        Assert.True(deliveredDescription == longA || deliveredDescription == longB);
    }

    private static ChatOrchestratorService CreateService(
        ILLMService llm, GameRulesService rules, int maxPackChars)
        => new(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "legacy",
                MaxToolRounds = 4,
                MaxRequestSeconds = 10,
                MaxRepeatedFailures = 3,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxConversationMessages = 24,
                MaxEvidencePackChars = maxPackChars
            }),
            NullLogger<ChatOrchestratorService>.Instance);

    private static List<ChatMessage> History() =>
        new() { new ChatMessage { Role = "user", Content = "测试问题" } };

    private static LLMChatResponse Tool(string name, string arguments) => new()
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
                            Id = "call_" + Guid.NewGuid().ToString("N")[..6],
                            Function = new FunctionCall { Name = name, Arguments = arguments }
                        }
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

    private sealed record ContinuationInfo(string Handle, string Operation, string? Entity);

    private static ContinuationInfo? FindContinuation(List<ChatMessage> messages, string operation)
    {
        foreach (var content in ToolContents(messages).Reverse())
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("queries", out var queries)) continue;
            foreach (var query in queries.EnumerateArray())
            {
                if (!query.TryGetProperty("omissions", out var omissions)) continue;
                foreach (var omission in omissions.EnumerateArray())
                {
                    if (!omission.TryGetProperty("continuation", out var continuation)
                        || continuation.ValueKind != JsonValueKind.Object)
                        continue;
                    var op = continuation.TryGetProperty("operation", out var opProp)
                        ? opProp.GetString() ?? ""
                        : "";
                    if (op != operation) continue;
                    var handle = continuation.TryGetProperty("handle", out var handleProp)
                        ? handleProp.GetString() ?? ""
                        : "";
                    var entity = continuation.TryGetProperty("entity", out var entityProp)
                        ? entityProp.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(handle))
                        return new ContinuationInfo(handle, op, entity);
                }
            }
        }
        return null;
    }

    private static JsonElement? FindEvidenceContent(List<ChatMessage> messages, string type)
    {
        foreach (var content in ToolContents(messages).Reverse())
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("evidence_catalog", out var catalog)) continue;
            foreach (var evidence in catalog.EnumerateArray())
            {
                if (!evidence.TryGetProperty("type", out var typeProp)
                    || typeProp.GetString() != type)
                    continue;
                if (evidence.TryGetProperty("content", out var contentElement)
                    && contentElement.ValueKind != JsonValueKind.Null)
                    return contentElement.Clone();
                if (evidence.TryGetProperty("data", out var dataElement)
                    && dataElement.ValueKind != JsonValueKind.Null)
                    return dataElement.Clone();
            }
        }
        return null;
    }

    private static string? FindFirstEvidenceId(List<ChatMessage> messages)
    {
        foreach (var content in ToolContents(messages).Reverse())
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("evidence_catalog", out var catalog)) continue;
            foreach (var evidence in catalog.EnumerateArray())
            {
                if (evidence.TryGetProperty("evidence_id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                    return id.GetString();
            }
        }
        return null;
    }

    private static IEnumerable<string> ToolContents(List<ChatMessage> messages)
        => messages.Where(message => message.Role == "tool")
            .Select(message => message.Content ?? "")
            .Where(content => content.Contains("evidence_catalog", StringComparison.Ordinal));

    private sealed class ScriptedLlm : ILLMService
    {
        private readonly Func<ScriptedCall, LLMChatResponse> _handler;
        private int _calls;

        public ScriptedLlm(Func<ScriptedCall, LLMChatResponse> handler) => _handler = handler;

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls);
            return Task.FromResult(_handler(new ScriptedCall(index, messages)));
        }
    }

    private sealed record ScriptedCall(int Index, List<ChatMessage> Messages);

    private sealed class TempRulesRoot : IDisposable
    {
        public TempRulesRoot(string conceptsJson)
        {
            Root = Path.Combine(Path.GetTempPath(),
                "boardai-continuation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "content", "ontology"));
            Directory.CreateDirectory(Path.Combine(Root, "content", "games", "testgame"));
            File.WriteAllText(Path.Combine(Root, "content", "ontology", "concepts.json"),
                """{"concepts":[]}""");
            File.WriteAllText(Path.Combine(Root, "content", "games", "testgame", "concepts.json"),
                conceptsJson);
            File.WriteAllText(Path.Combine(Root, "content", "games", "testgame", "flow.json"),
                """{"procedures":[{"id":"game","options":[]}]}""");
        }

        public string Root { get; }

        public GameRulesService CreateService()
            => new(Options.Create(new RulesOptions { BasePath = Root }), vectorSearch: null);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Test cleanup is best effort.
            }
        }
    }
}
