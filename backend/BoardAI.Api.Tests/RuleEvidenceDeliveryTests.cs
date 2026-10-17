using System.Text.Encodings.Web;
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
    public void Projector_FinalSerializedPayload_RespectsBudget_WithManyCandidates()
    {
        var plan = PlanWithQueries(("q1", "rule_lookup", payloads: new[]
        {
            Definition("rule_a", JsonSerializer.SerializeToElement(new
            {
                id = "rule_a",
                name = new { zh = "规则甲" },
                description = new { zh = new string('甲', 1400) }
            })),
            Definition("rule_b", JsonSerializer.SerializeToElement(new
            {
                id = "rule_b",
                name = new { zh = "规则乙" },
                description = new { zh = new string('乙', 1400) }
            }))
        }));
        plan.Results[0].Candidates = Enumerable.Range(0, 80)
            .Select(index => new ConceptSummary
            {
                Id = $"candidate_{index}",
                Path = $"candidate_{index}",
                Name = $"候选{index}",
                Score = 1f - index / 1000f,
                ReadStatus = "not_read",
                TargetResolved = false
            })
            .ToList();

        var budget = 1800;
        var projected = new RuleToolResultProjector(budget).Project("testgame", plan);

        Assert.True(SerializedProjectedLength(projected) <= budget,
            $"actual={SerializedProjectedLength(projected)}, budget={budget}");
        Assert.True(projected.SerializedChars <= budget,
            $"reported={projected.SerializedChars}, budget={budget}");
        Assert.Contains("预算", projected.Note);
    }

    [Fact]
    public void Projector_MetadataOnlyOverBudget_ReturnsBoundedExplicitFallback()
    {
        var plan = PlanWithQueries(("q1", "explain", payloads: Array.Empty<QueryPayload>()));
        plan.Results[0].Status = "unresolved";
        plan.Results[0].Resolution = QueryResolutionStatuses.Candidates;
        plan.Results[0].Coverage = QueryCoverageStatuses.Unknown;
        plan.Results[0].Candidates = Enumerable.Range(0, 200)
            .Select(index => new ConceptSummary
            {
                Id = $"candidate_{index}",
                Path = $"candidate_{index}",
                Name = $"候选{index}" + new string('名', 20),
                Description = new string('描', 100),
                ReadStatus = "not_read",
                TargetResolved = false
            })
            .ToList();

        var budget = 1000;
        var projected = new RuleToolResultProjector(budget).Project("testgame", plan);

        Assert.Empty(projected.EvidenceCatalog);
        Assert.True(SerializedProjectedLength(projected) <= budget,
            $"actual={SerializedProjectedLength(projected)}, budget={budget}");
        Assert.Contains("预算", projected.Note);
        Assert.Contains(projected.Queries[0].Omissions,
            omission => omission.Reason == QueryOmissionReasons.BudgetTruncated);
    }

    [Fact]
    public async Task LegacyToolMessage_RespectsFinalSerializedBudget_AtToolBoundary()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        const int budget = 1200;
        var capturedToolMessage = string.Empty;
        var llm = new BudgetCapturingLlm(message =>
        {
            if (message.Contains("evidence_catalog", StringComparison.Ordinal))
                capturedToolMessage = message;
        });
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
                MaxEvidencePackChars = budget,
                MaxRepeatedFailures = 2
            }),
            NullLogger<ChatOrchestratorService>.Instance);

        var result = await sut.ProcessWithEvidenceAsync("testgame",
            new List<ChatMessage> { new() { Role = "user", Content = "测试问题" } });

        Assert.False(string.IsNullOrWhiteSpace(capturedToolMessage));
        Assert.True(capturedToolMessage.Length <= budget,
            $"actual={capturedToolMessage.Length}, budget={budget}");
        using var doc = JsonDocument.Parse(capturedToolMessage);
        Assert.True(doc.RootElement.TryGetProperty("evidence_catalog", out _)
            || doc.RootElement.TryGetProperty("note", out _));
        Assert.Equal("unavailable", result.Orchestration!.Outcome);
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

    private static int SerializedProjectedLength(ProjectedRuleQueryResult projected)
        => JsonSerializer.Serialize(projected, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).Length;

    private sealed class BudgetCapturingLlm : ILLMService
    {
        private readonly Action<string> _captureTool;
        private int _calls;

        public BudgetCapturingLlm(Action<string> captureTool) => _captureTool = captureTool;

        public Task<LLMChatResponse> ChatWithMessagesAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var content in messages
                         .Where(message => message.Role == "tool")
                         .Select(message => message.Content ?? ""))
                _captureTool(content);

            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new LLMChatResponse
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
                                        Function = new FunctionCall
                                        {
                                            Name = "execute_plan",
                                            Arguments = """{"plan":{"queries":[{"relation":"list","limit":200}]}}"""
                                        }
                                    }
                                }
                            }
                        }
                    }
                });
            }

            return Task.FromResult(new LLMChatResponse
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
                                    Id = "call_2",
                                    Function = new FunctionCall
                                    {
                                        Name = "submit_answer",
                                        Arguments = """{"outcome":"unavailable","text":"资料预算不足。","unavailable_reason":"budget_exhausted"}"""
                                    }
                                }
                            }
                        }
                    }
                }
            });
        }
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
            var evidenceId = FindEvidenceId(messages);
            var arguments = JsonSerializer.Serialize(new
            {
                outcome = "answer",
                text = "FINAL",
                evidence_ids = evidenceId == null ? Array.Empty<string>() : new[] { evidenceId }
            });
            return Task.FromResult(new LLMChatResponse
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
                                    Id = "submit_1",
                                    Function = new FunctionCall { Name = "submit_answer", Arguments = arguments }
                                }
                            }
                        }
                    }
                }
            });
        }

        private static string? FindEvidenceId(List<ChatMessage> messages)
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
                    // ignore non-projected tool messages
                }
            }
            return null;
        }
    }
}
