using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class RuleEvidenceDeliveryTests
{
    [Fact]
    public void Projector_PutsSharedEvidenceOnce_AndLinksBothQueries()
    {
        var meaning = new
        {
            id = "widget",
            name = new { zh = "小装置" },
            description = new { zh = "完整定义" }
        };
        var definition = JsonSerializer.SerializeToElement(meaning);
        var plan = PlanWithQueries(
            ("q1", "explain", payloads: new[] { Definition("widget", definition) }),
            ("q2", "explain", payloads: new[] { Definition("widget", definition) }));
        var projected = new RuleToolResultProjector(60_000).Project("testgame", plan);

        var evidence = Assert.Single(projected.EvidenceCatalog);
        Assert.Equal(2, projected.Queries.Count);
        Assert.All(projected.Queries, query => Assert.Equal(new[] { evidence.EvidenceId }, query.EvidenceIds));
        Assert.Equal("完整定义",
            evidence.Content!.Value.GetProperty("description").GetProperty("zh").GetString());
    }

    [Fact]
    public void Projector_DoesNotTruncateLongRuleText_WhenItFitsBudget()
    {
        var longBody = "规则开头。" + new string('甲', 6000) + "每回合至多一次，除非另有说明。";
        var content = JsonSerializer.SerializeToElement(new
        {
            id = "long_rule",
            name = new { zh = "长规则" },
            description = new { zh = longBody }
        });
        var plan = PlanWithQueries(("q1", "explain", payloads: new[] { Definition("long_rule", content) }));

        var projected = new RuleToolResultProjector(20_000).Project("testgame", plan);

        var evidence = Assert.Single(projected.EvidenceCatalog);
        var preserved = evidence.Content!.Value.GetProperty("description").GetProperty("zh").GetString();
        Assert.Equal(longBody, preserved);
        Assert.EndsWith("每回合至多一次，除非另有说明。", preserved);
        Assert.Empty(projected.Queries[0].Omissions);
    }

    [Fact]
    public void Projector_OversizedAtomBecomesExplicitOmission_NotTruncatedContent()
    {
        var secret = new string('秘', 6000);
        var content = JsonSerializer.SerializeToElement(new
        {
            id = "huge_rule",
            name = new { zh = "超大规则" },
            description = new { zh = secret + "完整结尾" }
        });
        var plan = PlanWithQueries(("q1", "explain", payloads: new[] { Definition("huge_rule", content) }));

        var projected = new RuleToolResultProjector(2000).Project("testgame", plan);

        Assert.Empty(projected.EvidenceCatalog);
        var query = Assert.Single(projected.Queries);
        Assert.Equal(QueryCoverageStatuses.Partial, query.Coverage);
        var omission = Assert.Single(query.Omissions);
        Assert.Equal(QueryOmissionReasons.Oversized, omission.Reason);
        Assert.NotNull(omission.Continuation);
        Assert.Equal("read_evidence", omission.Continuation!.Operation);
        Assert.DoesNotContain(secret[..100], JsonSerializer.Serialize(projected));
        Assert.True(projected.SerializedChars <= 2000,
            $"serialized={projected.SerializedChars}");
    }

    [Fact]
    public void Projector_KeepsDifferentParameterizedInstancesSeparate()
    {
        var left = JsonSerializer.SerializeToElement(new { id = "instance_a", name = new { zh = "实例甲" }, value = 1 });
        var right = JsonSerializer.SerializeToElement(new { id = "instance_b", name = new { zh = "实例乙" }, value = 2 });
        var plan = PlanWithQueries(
            ("q1", "explain", payloads: new[] { Definition("instance_a", left) }),
            ("q2", "explain", payloads: new[] { Definition("instance_b", right) }));

        var projected = new RuleToolResultProjector(60_000).Project("testgame", plan);

        Assert.Equal(2, projected.EvidenceCatalog.Count);
        Assert.Equal(2, projected.EvidenceCatalog.Select(e => e.CanonicalPath).Distinct().Count());
        Assert.NotEqual(
            projected.Queries[0].EvidenceIds.Single(),
            projected.Queries[1].EvidenceIds.Single());
    }

    [Fact]
    public async Task LegacyToolMessage_UsesThinEvidenceCatalog_DeliveredToModel()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var llm = new CapturingLlm();
        var sut = new ChatOrchestratorService(
            llm,
            rules,
            Options.Create(new LLMOptions
            {
                OrchestrationMode = "legacy",
                MaxToolRounds = 3,
                MaxRequestSeconds = 10,
                MaxToolCallsPerRound = 4,
                MaxTotalToolCalls = 12,
                MaxConversationMessages = 12,
                MaxEvidencePackChars = 60_000
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        await sut.ProcessWithEvidenceAsync("testgame",
            new List<ChatMessage> { new() { Role = "user", Content = "测试问题" } });

        var captured = Assert.Single(llm.ToolContents);
        using var doc = JsonDocument.Parse(captured);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("evidence_catalog", out var catalog));
        Assert.True(root.TryGetProperty("queries", out var queries));
        Assert.False(root.TryGetProperty("Results", out _));
        Assert.False(root.TryGetProperty("Matched", out _));
        var query = Assert.Single(queries.EnumerateArray());
        var evidenceId = Assert.Single(query.GetProperty("evidence_ids").EnumerateArray()).GetString();
        Assert.Contains(catalog.EnumerateArray(), evidence =>
            evidence.GetProperty("evidence_id").GetString() == evidenceId
            && evidence.GetProperty("canonical_path").GetString() == "widget");
        Assert.Equal(1, CountOccurrences(captured, "测试对象"));
    }

    private static PlanExecutionResult PlanWithQueries(params (string Id, string Relation, QueryPayload[] Payloads)[] queries)
    {
        var result = new PlanExecutionResult
        {
            RequestId = "plan_test",
            Game = "testgame",
            RulesVersion = "rules-v1"
        };
        for (var i = 0; i < queries.Length; i++)
        {
            var query = queries[i];
            result.Results.Add(new PlanItemResult
            {
                QueryId = query.Id,
                QueryIndex = i,
                Relation = query.Relation,
                Entity = "entity",
                Intent = "intent",
                Status = "ok",
                Source = "exact_id",
                Resolution = QueryResolutionStatuses.Exact,
                Coverage = QueryCoverageStatuses.Complete,
                ResolvedTargets = query.Payloads.Select(p => p.CanonicalPath).Distinct().ToList(),
                Payloads = query.Payloads.ToList()
            });
        }
        return result;
    }

    private static QueryPayload Definition(string path, JsonElement content) => new()
    {
        Type = QueryPayloadTypes.Definition,
        CanonicalPath = path,
        RulesVersion = "rules-v1",
        Completeness = QueryCoverageStatuses.Complete,
        DefinitionSource = "exact_id",
        Content = content
    };

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private sealed class CapturingLlm : ILLMService
    {
        private int _calls;
        public List<string> ToolContents { get; } = new();

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            var toolContents = messages.Where(message => message.Role == "tool")
                .Select(message => message.Content ?? "")
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .ToList();
            ToolContents.AddRange(toolContents);
            if (_calls == 1)
            {
                var message = new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "call_1",
                            Function = new FunctionCall
                            {
                                Name = "execute_plan",
                                Arguments = """{"plan":{"queries":[{"relation":"explain","entity":"widget"}]}}"""
                            }
                        }
                    }
                };
                return Task.FromResult(new LLMChatResponse
                {
                    Choices = new List<LLMChoice> { new() { Message = message } }
                });
            }
            return Task.FromResult(new LLMChatResponse
            {
                Choices = new List<LLMChoice>
                {
                    new() { Message = new ChatMessage { Role = "assistant", Content = "FINAL" } }
                }
            });
        }
    }
}
