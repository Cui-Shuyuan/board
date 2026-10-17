using System.Text.Json;
using System.Text.Json.Nodes;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoardAI.Api.Tests;

public sealed class RuleEvidencePackBuilderTests
{
    [Fact]
    public async Task RelatedEntriesKeepTheirOwnCanonicalPaths()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"root","name":{"zh":"根对象"},"description":{"zh":"引用 <rule_a> 和 <rule_b>"}},
            {"id":"rule_a","name":{"zh":"关联 A"},"description":{"zh":"A 的正文"}},
            {"id":"rule_b","name":{"zh":"关联 B"},"description":{"zh":"B 的正文"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "root", "explain", "根对象是什么")
        }, CancellationToken.None);

        Assert.Contains(pack.EvidenceCatalog, e => e.CanonicalPath == "root");
        Assert.Contains(pack.EvidenceCatalog, e => e.CanonicalPath == "rule_a");
        Assert.Contains(pack.EvidenceCatalog, e => e.CanonicalPath == "rule_b");
        Assert.Equal(3, pack.EvidenceCatalog.Select(e => e.CanonicalPath).Distinct().Count());
        Assert.All(pack.Questions[0].EvidenceBindings.Where(b => b.Role == "related"), binding =>
            Assert.NotEqual("root", pack.EvidenceCatalog.Single(e => e.EvidenceId == binding.EvidenceId).CanonicalPath));
    }

    [Fact]
    public async Task SharedRelatedContentIsDeduplicatedButBindingsRetainAllParents()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"cand_a","name":{"zh":"小装置 A"},"description":{"zh":"参考 <shared_rule>"}},
            {"id":"cand_b","name":{"zh":"小装置 B"},"description":{"zh":"参考 <shared_rule>"}},
            {"id":"shared_rule","name":{"zh":"共享规则"},"description":{"zh":"共享正文"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "装置", "explain", "装置是什么")
        }, CancellationToken.None);

        var shared = Assert.Single(pack.EvidenceCatalog.Where(e => e.CanonicalPath == "shared_rule"));
        var sharedBindings = pack.Questions[0].EvidenceBindings.Where(b => b.EvidenceId == shared.EvidenceId).ToList();
        Assert.Equal(2, sharedBindings.Count);
        Assert.Contains(sharedBindings, b => b.ParentPath == "cand_a");
        Assert.Contains(sharedBindings, b => b.ParentPath == "cand_b");
        Assert.Equal(3, pack.EvidenceCatalog.Count);
        Assert.True(pack.Stats.BindingReuses >= 1);
    }

    [Fact]
    public async Task DirectSlotRelatedDoesNotInheritSlotPathAndOwnerSlotsStayIsolated()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"board_a","name":{"zh":"面板 A"},"slots":[
              {"id":"point_bonus","name":{"zh":"奖励槽 A"},"description":{"zh":"引用 <rule_a>"}}
            ]},
            {"id":"board_b","name":{"zh":"面板 B"},"slots":[
              {"id":"point_bonus","name":{"zh":"奖励槽 B"},"description":{"zh":"引用 <rule_b>"}}
            ]},
            {"id":"rule_a","name":{"zh":"规则 A"},"description":{"zh":"A"}},
            {"id":"rule_b","name":{"zh":"规则 B"},"description":{"zh":"B"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "board_a.point_bonus", "explain", "A 奖励槽是什么")
        }, CancellationToken.None);

        var target = Assert.Single(pack.EvidenceCatalog.Where(e => e.CanonicalPath == "board_a.point_bonus"));
        Assert.Contains(pack.EvidenceCatalog, e => e.CanonicalPath == "rule_a");
        Assert.DoesNotContain(pack.EvidenceCatalog, e => e.CanonicalPath == "board_b.point_bonus");
        Assert.DoesNotContain(pack.EvidenceCatalog, e => e.CanonicalPath == "rule_b");
        var related = pack.Questions[0].EvidenceBindings.Where(b => b.Role == "related").ToList();
        Assert.NotEmpty(related);
        Assert.All(related, b => Assert.NotEqual(target.EvidenceId, b.EvidenceId));
        Assert.Contains(pack.Questions[0].EvidenceBindings, b => b.EvidenceId == target.EvidenceId && b.Role == "target");
    }

    [Fact]
    public async Task SameTargetWithDifferentRelationProjectionKeepsBothProjections()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "use_widget", "explain", "使用小装置是什么"),
            Question("q2", "use_widget", "condition", "使用小装置需要什么条件")
        }, CancellationToken.None);

        var items = pack.EvidenceCatalog.Where(e => e.CanonicalPath == "use_widget").ToList();
        Assert.True(items.Count >= 2);
        Assert.Contains(items, e => e.Condition != null);
        Assert.Contains(items, e => e.EvidenceId != items[0].EvidenceId);
        Assert.All(items, e => Assert.False(string.IsNullOrWhiteSpace(e.ContentHash)));
    }

    [Fact]
    public async Task DirectAndCandidateReads_KeepEquivalentRelationFields()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();

        var directBuilder = CreateBuilder(rules);
        var direct = await directBuilder.BuildAsync("testgame", new[]
        {
            Question("q1", "use_widget", "condition", "使用小装置需要什么条件")
        }, CancellationToken.None);
        var directEvidence = direct.EvidenceCatalog.First(e => e.CanonicalPath == "use_widget" && e.Condition != null);
        Assert.NotNull(directEvidence.Condition);

        var candidateBuilder = CreateBuilder(rules);
        var candidatePack = await candidateBuilder.BuildAsync("testgame", new[]
        {
            Question("q1", "装置", "condition", "使用小装置需要什么条件")
        }, CancellationToken.None);
        var candidateEvidence = candidatePack.EvidenceCatalog.First(e => e.CanonicalPath == "use_widget" && e.Condition != null);
        Assert.NotNull(candidateEvidence.Condition);
        Assert.Equal(JsonSerializer.Serialize(directEvidence.Condition),
            JsonSerializer.Serialize(candidateEvidence.Condition));
        Assert.Equal(JsonSerializer.Serialize(directEvidence.Cost),
            JsonSerializer.Serialize(candidateEvidence.Cost));
        Assert.Equal(JsonSerializer.Serialize(directEvidence.Target),
            JsonSerializer.Serialize(candidateEvidence.Target));
        Assert.Equal(JsonSerializer.Serialize(directEvidence.Source),
            JsonSerializer.Serialize(candidateEvidence.Source));
        Assert.Equal(directEvidence.ProjectionComplete, candidateEvidence.ProjectionComplete);
    }

    [Fact]
    public async Task FairPrimaryCoverage_SecondQuestionStillGetsEvidenceBeforeRelated()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"root_a","name":{"zh":"根 A"},"description":{"zh":"引用 <related_a1> <related_a2>"}},
            {"id":"root_b","name":{"zh":"根 B"},"description":{"zh":"引用 <related_b1> <related_b2>"}},
            {"id":"related_a1","name":{"zh":"A1"},"description":{"zh":"a1"}},
            {"id":"related_a2","name":{"zh":"A2"},"description":{"zh":"a2"}},
            {"id":"related_b1","name":{"zh":"B1"},"description":{"zh":"b1"}},
            {"id":"related_b2","name":{"zh":"B2"},"description":{"zh":"b2"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules, maxEvidenceTotal: 2, maxPackChars: 60000);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "root_a", "explain", "根 A 是什么"),
            Question("q2", "root_b", "explain", "根 B 是什么")
        }, CancellationToken.None);

        Assert.Equal(2, pack.EvidenceCatalog.Count);
        Assert.All(pack.Questions, q => Assert.NotEmpty(q.EvidenceBindings));
        Assert.True(pack.OmittedEvidenceCount > 0);
        Assert.Contains(pack.EvidenceCatalog, e => e.CanonicalPath == "root_a");
        Assert.Contains(pack.EvidenceCatalog, e => e.CanonicalPath == "root_b");
    }

    [Fact]
    public void TrimFinder_PrefersRelatedOverCandidateAndUsesBindingRank()
    {
        var builder = CreateBuilder(null!, maxPackChars: 500);
        var pack = new RuleEvidencePack { PackId = "p", Game = "g" };
        var question = new EvidenceQuestionPack { QuestionId = "q1" };
        pack.Questions.Add(question);
        foreach (var (id, rank) in new[] { ("e10", 2), ("e2", 3), ("e3", 10) })
        {
            pack.EvidenceCatalog.Add(new RuleEvidenceItem { EvidenceId = id, CanonicalPath = id, Name = id });
            question.EvidenceBindings.Add(new RuleEvidenceBinding
            {
                EvidenceId = id,
                Role = "related",
                Rank = rank
            });
        }
        question.EvidenceBindings.Add(new RuleEvidenceBinding { EvidenceId = "e_candidate", Role = "candidate", Rank = 20 });
        pack.EvidenceCatalog.Add(new RuleEvidenceItem { EvidenceId = "e_candidate", CanonicalPath = "candidate", Name = "candidate" });

        var remove = builder.DebugFindBindingToTrim(pack);

        Assert.NotNull(remove);
        Assert.Equal("related", remove.Value.Binding.Role);
        Assert.Equal("e3", remove.Value.Binding.EvidenceId);
    }

    [Fact]
    public void CharBudget_IsIdempotentAndTheLastTargetDoesNotRemainOversized()
    {
        var builder = CreateBuilder(null!, maxPackChars: 1024);
        var pack = new RuleEvidencePack { PackId = "p", Game = "g" };
        var question = new EvidenceQuestionPack { QuestionId = "q1" };
        pack.Questions.Add(question);
        var big = JsonSerializer.SerializeToElement(new { description = new string('x', 5000) });
        pack.EvidenceCatalog.Add(new RuleEvidenceItem
        {
            EvidenceId = "e1",
            CanonicalPath = "target",
            Name = "target",
            Source = big
        });
        question.EvidenceBindings.Add(new RuleEvidenceBinding { EvidenceId = "e1", Role = "target", Rank = 1 });

        builder.DebugApplyCharBudget(pack);
        var firstOmitted = pack.OmittedItems.Count;
        builder.DebugApplyCharBudget(pack);

        Assert.Equal(firstOmitted, pack.OmittedItems.Count);
        Assert.True(builder.LastPackChars <= 1024);
        Assert.Empty(pack.EvidenceCatalog);
        Assert.Empty(question.EvidenceBindings);
        Assert.Equal("partial_evidence", question.CoverageStatus);
    }

    [Fact]
    public async Task LargeSourceProjectionKeepsNestedConditionAndMarksIncomplete()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"big_rule","name":{"zh":"大规则"},"description":{"zh":"大规则正文"},
             "<ontology::condition>":{"id":"nested_condition","description":{"zh":"必须满足嵌套前提"}},
             "<ontology::content>":{"<ontology::instant_content>":{"description":{"zh":"执行大规则结果"}}},
             "noise":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules, maxEvidenceJsonChars: 500);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "big_rule", "explain", "大规则是什么")
        }, CancellationToken.None);

        var evidence = Assert.Single(pack.EvidenceCatalog);
        Assert.NotNull(evidence.Source);
        Assert.True(evidence.Source!.Value.TryGetProperty("<ontology::condition>", out _));
        Assert.True(evidence.Source!.Value.TryGetProperty("<ontology::content>", out _));
        Assert.False(evidence.ProjectionComplete);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Projection));
    }

    [Fact]
    public void UnverifiableMatchedPath_IsNotFabricated()
    {
        using var fixture = new RulesFixture();
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules);
        using var doc = JsonDocument.Parse("""{"name":{"zh":"没有 id 的元素"}}""");
        var item = new PlanItemResult { Relation = "explain", Entity = "一个普通名字" };
        var question = Question("q1", "一个普通名字", "explain", "是什么");

        var path = builder.DebugResolveMatchedPath("testgame", question, item, doc.RootElement);

        Assert.Equal("", path);
    }

    [Fact]
    public async Task MultiParentExpansionPreservesEveryRelationEdgeWhileDeduplicatingContent()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.OntologyConceptsPath, """
        {
          "concepts":[
            {"id":"dup","name":{"zh":"本体重复项"},"description":{"zh":"引用 <shared_rule>"}}
          ]
        }
        """);
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"dup","name":{"zh":"游戏重复项"},"description":{"zh":"引用 <shared_rule>"}},
            {"id":"shared_rule","name":{"zh":"共享规则"},"description":{"zh":"共享正文"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();

        var read = rules.ReadConceptWithExpansionWithPaths("testgame", "dup");
        Assert.Equal(2, read.Matched.Count);
        Assert.Equal(2, read.Related.Count);
        Assert.All(read.Related, hit => Assert.Equal("shared_rule", hit.CanonicalPath));
        Assert.Equal(new[] { "dup", "ontology::dup" },
            read.Related.Select(hit => hit.ParentPath).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        var builder = CreateBuilder(rules);
        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "dup", "explain", "重复项之间共享什么规则")
        }, CancellationToken.None);

        var shared = Assert.Single(pack.EvidenceCatalog, e => e.CanonicalPath == "shared_rule");
        var sharedBindings = pack.Questions[0].EvidenceBindings
            .Where(binding => binding.EvidenceId == shared.EvidenceId)
            .ToList();
        Assert.Equal(2, sharedBindings.Count);
        Assert.Contains(sharedBindings, binding => binding.ParentPath == "ontology::dup");
        Assert.Contains(sharedBindings, binding => binding.ParentPath == "dup");
    }

    [Fact]
    public async Task TotalContentBudgetStillAllowsDuplicateParentBindings()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"root_a","name":{"zh":"根 A"},"description":{"zh":"引用 <shared_rule>"}},
            {"id":"root_b","name":{"zh":"根 B"},"description":{"zh":"引用 <shared_rule>"}},
            {"id":"shared_rule","name":{"zh":"共享规则"},"description":{"zh":"共享正文"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules, maxEvidenceTotal: 3, maxPackChars: 60000);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "root_a", "explain", "根 A 引用什么"),
            Question("q2", "root_b", "explain", "根 B 引用什么")
        }, CancellationToken.None);

        Assert.Equal(3, pack.EvidenceCatalog.Count);
        var shared = Assert.Single(pack.EvidenceCatalog, e => e.CanonicalPath == "shared_rule");
        Assert.Equal(2, pack.Questions
            .SelectMany(question => question.EvidenceBindings)
            .Count(binding => binding.EvidenceId == shared.EvidenceId));
        Assert.Contains(pack.Questions[0].EvidenceBindings,
            binding => binding.EvidenceId == shared.EvidenceId && binding.ParentPath == "root_a");
        Assert.Contains(pack.Questions[1].EvidenceBindings,
            binding => binding.EvidenceId == shared.EvidenceId && binding.ParentPath == "root_b");
        Assert.True(pack.Stats.BindingReuses >= 1);
    }

    [Fact]
    public async Task OversizedMetadataFallsBackToExplicitIncompletePackWithinFinalBudget()
    {
        using var fixture = new RulesFixture();
        WriteConcepts(fixture, """
        {
          "objects":[
            {"id":"big_rule","name":{"zh":"大规则"},"description":{"zh":"大规则正文"}}
          ],
          "actions":[],"triggers":[],"conditions":[]
        }
        """);
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules, maxPackChars: 1024);
        var hugeIntent = new string('问', 3000);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "big_rule", "explain", hugeIntent)
        }, CancellationToken.None);

        var actualLength = JsonSerializer.Serialize(pack, RulesJsonUtils.RelaxedJsonOptions).Length;
        Assert.True(pack.MetadataBudgetExceeded);
        Assert.Empty(pack.EvidenceCatalog);
        Assert.Equal("budget_insufficient_metadata", pack.Questions[0].CoverageStatus);
        Assert.True(actualLength <= 1024, $"actual pack length was {actualLength}");
        Assert.Equal(actualLength, builder.LastPackChars);
        Assert.Equal(1024, builder.LastStats.EffectiveMaxPackChars);
    }

    [Fact]
    public void ActualSerializedPackIncludingStatsIsBelowBudget()
    {
        var builder = CreateBuilder(null!, maxPackChars: 1024);
        var pack = new RuleEvidencePack { PackId = "p", Game = "g" };
        var question = new EvidenceQuestionPack { QuestionId = "q1" };
        pack.Questions.Add(question);
        var big = JsonSerializer.SerializeToElement(new { description = new string('x', 5000) });
        pack.EvidenceCatalog.Add(new RuleEvidenceItem
        {
            EvidenceId = "e1",
            CanonicalPath = "target",
            Name = "target",
            Source = big
        });
        question.EvidenceBindings.Add(new RuleEvidenceBinding { EvidenceId = "e1", Role = "target", Rank = 1 });

        builder.DebugApplyCharBudget(pack);

        var actualLength = JsonSerializer.Serialize(pack, RulesJsonUtils.RelaxedJsonOptions).Length;
        Assert.True(actualLength <= 1024, $"actual pack length was {actualLength}");
        Assert.Equal(actualLength, builder.LastPackChars);
        Assert.Equal(builder.LastStats.Omitted, pack.OmittedEvidenceCount);
    }

    [Fact]
    public async Task SupplementCannotExceedPerQuestionEvidenceCap()
    {
        using var fixture = new RulesFixture();
        var objects = new JsonArray();
        foreach (var root in new[] { "root_a", "root_b" })
        {
            var refs = string.Join(" ", Enumerable.Range(1, 10).Select(i => $"<{root}_related_{i}>"));
            objects.Add(new JsonObject
            {
                ["id"] = root,
                ["name"] = new JsonObject { ["zh"] = root },
                ["description"] = new JsonObject { ["zh"] = "引用 " + refs }
            });
            for (var i = 1; i <= 10; i++)
            {
                objects.Add(new JsonObject
                {
                    ["id"] = $"{root}_related_{i}",
                    ["name"] = new JsonObject { ["zh"] = $"{root} 关联 {i}" },
                    ["description"] = new JsonObject { ["zh"] = $"{root} 关联正文 {i}" }
                });
            }
        }
        var concepts = new JsonObject
        {
            ["objects"] = objects,
            ["actions"] = new JsonArray(),
            ["triggers"] = new JsonArray(),
            ["conditions"] = new JsonArray()
        };
        File.WriteAllText(fixture.GameConceptsPath, concepts.ToJsonString());
        using var rules = fixture.CreateService();
        var builder = CreateBuilder(rules, maxEvidenceTotal: 24, maxEvidencePerQuestion: 4, maxPackChars: 60000);

        var pack = await builder.BuildAsync("testgame", new[]
        {
            Question("q1", "root_a", "explain", "根 A 引用什么")
        }, CancellationToken.None);
        Assert.Equal(4, pack.Questions[0].EvidenceBindings
            .Select(binding => binding.EvidenceId)
            .Distinct(StringComparer.Ordinal)
            .Count());

        var appended = await builder.AddSupplementAsync(
            pack,
            "testgame",
            Question("q1", "root_b", "explain", "补充根 B 的引用"),
            CancellationToken.None);

        Assert.True(appended);
        var question = Assert.Single(pack.Questions);
        Assert.True(question.EvidenceBindings
            .Select(binding => binding.EvidenceId)
            .Distinct(StringComparer.Ordinal)
            .Count() <= 4);
        Assert.True(pack.OmittedEvidenceCount > 0);
        Assert.Contains(pack.OmittedItems, item =>
            item.Reason == "per_question_budget" && item.QuestionId == "q1");
    }

    private static RuleEvidencePackBuilder CreateBuilder(
        GameRulesService rules,
        int maxEvidenceTotal = 24,
        int maxEvidencePerQuestion = 8,
        int maxEvidenceJsonChars = 4000,
        int maxPackChars = 60000)
        => new(
            rules,
            new LLMOptions
            {
                MaxCandidatesPerQuestion = 3,
                MaxEvidencePerQuestion = maxEvidencePerQuestion,
                MaxEvidenceTotal = maxEvidenceTotal,
                MaxEvidenceJsonChars = maxEvidenceJsonChars,
                MaxEvidencePackChars = maxPackChars
            },
            NullLogger<RuleEvidencePackBuilder>.Instance);

    private static ProgramSubQuestion Question(string id, string entity, string relation, string intent) => new()
    {
        QuestionId = id,
        Entity = entity,
        Relation = relation,
        Intent = intent
    };

    private static void WriteConcepts(RulesFixture fixture, string json) =>
        File.WriteAllText(fixture.GameConceptsPath, json);
}
