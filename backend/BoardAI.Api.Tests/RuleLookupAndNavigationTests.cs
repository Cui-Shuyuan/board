using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RuleLookupAndNavigationTests
{
    [Fact]
    public async Task RuleLookup_PreservesExactObjectIdentity_AndAutoReadsRuleCandidates()
    {
        using var fixture = CreateRuleFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var explainDoc = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":"crystal"}]}""");
        using var lookupDoc = JsonDocument.Parse("""{"queries":[{"relation":"rule_lookup","entity":"crystal","intent":"水晶怎么获得"}]}""");

        var explain = Assert.Single((await plan.ExecutePlanAsync("testgame", explainDoc.RootElement)).Results);
        var lookup = Assert.Single((await plan.ExecutePlanAsync("testgame", lookupDoc.RootElement)).Results);

        Assert.Equal("ok", lookup.Status);
        Assert.Equal(QueryResolutionStatuses.Exact, lookup.Resolution);
        Assert.Equal(new[] { "crystal" }, lookup.ResolvedTargets);
        Assert.NotNull(lookup.Candidates);
        Assert.True(lookup.Candidates!.Count >= 3);
        Assert.Contains(lookup.Candidates, candidate => candidate.Id == "gain_crystal"
            && candidate.ReadStatus == "auto_read"
            && candidate.TargetResolved == false);
        Assert.DoesNotContain(lookup.ResolvedTargets, path => path.Contains("gain_crystal"));
        Assert.True(lookup.Payloads.Count > explain.Payloads.Count);

        var candidateDefinitions = lookup.Payloads
            .Where(payload => payload.Type == QueryPayloadTypes.Definition
                && payload.DefinitionSource == "auto_candidate_read")
            .ToList();
        Assert.True(candidateDefinitions.Count >= 2);
        var edgeKinds = candidateDefinitions
            .SelectMany(payload => DataJson(payload).GetProperty("reference_edges").EnumerateArray())
            .Select(edge => edge.GetProperty("edge_type").GetString())
            .ToList();
        Assert.Contains("effect", edgeKinds);
        Assert.Contains("cost", edgeKinds);
        Assert.Contains("mention", edgeKinds);
        Assert.All(candidateDefinitions, payload =>
            Assert.False(DataJson(payload).GetProperty("target_resolved").GetBoolean()));

        var summary = lookup.Payloads.Single(payload =>
            payload.Type == QueryPayloadTypes.RelationFacts
            && DataJson(payload).TryGetProperty("candidate_read_count", out _));
        Assert.True(DataJson(summary).GetProperty("identity_resolved").GetBoolean());
        Assert.Equal(3, DataJson(summary).GetProperty("candidate_read_count").GetInt32());
        Assert.True(DataJson(summary).GetProperty("read_truncated").GetBoolean());
        Assert.Contains(lookup.Omissions, omission => omission.Reason == QueryOmissionReasons.BudgetTruncated);
    }

    [Fact]
    public async Task RuleLookup_ContinuationOffset_ReadsNextPage_AndStopsAtEnd()
    {
        using var fixture = new RulesFixture();
        var actions = Enumerable.Range(1, 6).Select(index => $$"""
            { "id": "gold_rule_{{index}}", "name": { "zh": "金币规则{{index}}" },
              "description": { "zh": "如何获得金币。" },
              "<ontology::content>": "<gold>" }
            """);
        File.WriteAllText(fixture.GameConceptsPath, $$"""
        {
          "objects": [ { "id": "gold", "name": { "zh": "金币" } } ],
          "actions": [ {{string.Join(",", actions)}} ],
          "triggers": [], "conditions": []
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var firstDoc = JsonDocument.Parse("""{"queries":[{"relation":"rule_lookup","entity":"gold","intent":"如何获得金币"}]}""");
        var first = Assert.Single((await plan.ExecutePlanAsync("testgame", firstDoc.RootElement)).Results);

        var firstPagePaths = AutoReadPaths(first);
        Assert.Equal(3, firstPagePaths.Count);
        var continuation = first.Omissions
            .Select(omission => omission.Continuation)
            .Single(continuation => continuation?.Operation == "rule_lookup");
        Assert.NotNull(continuation);
        Assert.Equal(3, continuation!.Offset);
        Assert.Equal(3, continuation.Limit);
        Assert.Equal("如何获得金币", continuation.Intent);

        using var secondDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            queries = new[]
            {
                new
                {
                    relation = "rule_lookup",
                    entity = "gold",
                    intent = continuation.Intent,
                    offset = continuation.Offset,
                    limit = continuation.Limit
                }
            }
        }));
        var second = Assert.Single((await plan.ExecutePlanAsync("testgame", secondDoc.RootElement)).Results);

        var allCandidatePaths = first.Candidates!.Select(candidate => candidate.Path).ToList();
        var expectedSecondPage = allCandidatePaths.Skip(3).Take(3).ToList();
        var secondPagePaths = AutoReadPaths(second);
        Assert.Equal(expectedSecondPage, secondPagePaths);
        Assert.Empty(firstPagePaths.Intersect(secondPagePaths, StringComparer.Ordinal));
        Assert.DoesNotContain(second.Omissions, omission =>
            omission.Continuation?.Operation == "rule_lookup");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuleLookup_IntentRanking_IsIndependentOfUnrelatedDirectoryOrder(bool relevantFirst)
    {
        using var fixture = new RulesFixture();
        var unrelated = new[]
        {
            """{ "id": "pay_gold_1", "name": { "zh": "支付金币一" }, "description": { "zh": "支付金币。" }, "<ontology::cost>": "<gold>" }""",
            """{ "id": "pay_gold_2", "name": { "zh": "支付金币二" }, "description": { "zh": "支付金币。" }, "<ontology::cost>": "<gold>" }""",
            """{ "id": "pay_gold_3", "name": { "zh": "支付金币三" }, "description": { "zh": "支付金币。" }, "<ontology::cost>": "<gold>" }"""
        };
        var relevant = """{ "id": "gain_gold", "name": { "zh": "获得金币" }, "description": { "zh": "如何获得金币。" }, "<ontology::content>": "<gold>" }""";
        var actions = relevantFirst
            ? new[] { relevant }.Concat(unrelated)
            : unrelated.Concat(new[] { relevant });
        File.WriteAllText(fixture.GameConceptsPath, $$"""
        {
          "objects": [ { "id": "gold", "name": { "zh": "金币" } } ],
          "actions": [ {{string.Join(",", actions)}} ],
          "triggers": [],
          "conditions": []
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"rule_lookup","entity":"gold","intent":"如何获得金币"}]}""");

        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement)).Results);

        Assert.Equal("ok", item.Status);
        Assert.Equal("gain_gold", item.Candidates![0].Id);
        var autoReadPaths = item.Payloads
            .Where(payload => payload.DefinitionSource == "auto_candidate_read")
            .Select(payload => payload.CanonicalPath)
            .ToList();
        Assert.Contains(autoReadPaths, path => path.Contains("gain_gold", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuleLookup_WithoutExactIdentity_KeepsCandidatesUnresolvedAfterFormalRead()
    {
        using var fixture = CreateRuleFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"rule_lookup","intent":"获得水晶"}]}""");

        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement)).Results);

        Assert.Equal("unresolved", item.Status);
        Assert.Equal(QueryResolutionStatuses.Candidates, item.Resolution);
        Assert.Empty(item.ResolvedTargets);
        Assert.Contains(item.Payloads, payload => payload.DefinitionSource == "auto_candidate_read");
        Assert.Contains(item.Omissions, omission => omission.Reason == QueryOmissionReasons.AmbiguousIdentity);
    }

    [Fact]
    public async Task CostAndTarget_ReadOnlyDeclaredFields_AndExpandFormalDependencies()
    {
        using var fixture = CreateRuleFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var spendDoc = JsonDocument.Parse("""{"queries":[{"relation":"cost","entity":"spend_crystal"}]}""");
        using var gainDoc = JsonDocument.Parse("""{"queries":[{"relation":"cost","entity":"gain_crystal"}]}""");
        using var targetDoc = JsonDocument.Parse("""{"queries":[{"relation":"target","entity":"target_crystal"}]}""");

        var spend = Assert.Single((await plan.ExecutePlanAsync("testgame", spendDoc.RootElement)).Results);
        var gain = Assert.Single((await plan.ExecutePlanAsync("testgame", gainDoc.RootElement)).Results);
        var target = Assert.Single((await plan.ExecutePlanAsync("testgame", targetDoc.RootElement)).Results);

        Assert.NotNull(spend.Cost);
        Assert.Null(spend.Condition);
        Assert.Null(spend.Target);
        Assert.Contains(spend.Payloads, payload => payload.DefinitionSource == "declared_dependency"
            && payload.CanonicalPath == "crystal");
        var spendFacts = spend.Payloads.Single(payload => payload.Type == QueryPayloadTypes.RelationFacts);
        Assert.Equal("cost", DataJson(spendFacts).GetProperty("reference_edges")[0].GetProperty("edge_type").GetString());

        Assert.Null(gain.Cost);
        Assert.Equal(QueryCoverageStatuses.Partial, gain.Coverage);
        Assert.Contains(gain.Omissions, omission => omission.Reason == QueryOmissionReasons.FieldMissing);

        Assert.NotNull(target.Target);
        Assert.Null(target.Cost);
        var targetFacts = target.Payloads.Single(payload => payload.Type == QueryPayloadTypes.RelationFacts);
        Assert.Equal("target", DataJson(targetFacts).GetProperty("reference_edges")[0].GetProperty("edge_type").GetString());
    }

    [Fact]
    public async Task MissingAndBudgetedDependencies_AreExplicitRatherThanDropped()
    {
        using var fixture = CreateRuleFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var missingDoc = JsonDocument.Parse("""{"queries":[{"relation":"cost","entity":"missing_dependency_action"}]}""");
        using var manyDoc = JsonDocument.Parse("""{"queries":[{"relation":"cost","entity":"many_dependency_action"}]}""");

        var missing = Assert.Single((await plan.ExecutePlanAsync("testgame", missingDoc.RootElement)).Results);
        var many = Assert.Single((await plan.ExecutePlanAsync("testgame", manyDoc.RootElement)).Results);

        Assert.Equal(QueryCoverageStatuses.Partial, missing.Coverage);
        Assert.Contains(missing.Omissions, omission => omission.Reason == QueryOmissionReasons.NoRuleCoverage);
        Assert.Equal(QueryCoverageStatuses.Partial, many.Coverage);
        Assert.Contains(many.Omissions, omission => omission.Reason == QueryOmissionReasons.BudgetTruncated
            && omission.Continuation != null);
        Assert.Equal(8, many.Payloads.Count(payload => payload.DefinitionSource == "declared_dependency"));
    }

    [Fact]
    public async Task Compute_ReturnsSupportedFormulaOrExplicitUnsupportedAndMissingParameters()
    {
        using var fixture = CreateRuleFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var supportedDoc = JsonDocument.Parse("""{"queries":[{"relation":"compute","entity":"capacity_holder","intent":"容量是多少"}]}""");
        using var missingDoc = JsonDocument.Parse("""{"queries":[{"relation":"compute","entity":"missing_parameters","intent":"容量是多少"}]}""");
        using var unsupportedDoc = JsonDocument.Parse("""{"queries":[{"relation":"compute","entity":"crystal","intent":"数量是多少"}]}""");

        var supported = Assert.Single((await plan.ExecutePlanAsync("testgame", supportedDoc.RootElement)).Results);
        var missing = Assert.Single((await plan.ExecutePlanAsync("testgame", missingDoc.RootElement)).Results);
        var unsupported = Assert.Single((await plan.ExecutePlanAsync("testgame", unsupportedDoc.RootElement)).Results);

        Assert.Equal("ok", supported.Status);
        Assert.Equal(QueryCoverageStatuses.Complete, supported.Coverage);
        Assert.Contains(supported.Payloads, payload => payload.Type == QueryPayloadTypes.ComputedFact);
        Assert.Contains(supported.Payloads, payload =>
            payload.Type == QueryPayloadTypes.ComputedFact
            && payload.Content!.Value.GetRawText().Contains("computed", StringComparison.Ordinal));

        Assert.Equal("ok", missing.Status);
        Assert.Equal(QueryCoverageStatuses.Partial, missing.Coverage);
        Assert.Contains(missing.Omissions, omission => omission.Reason == QueryOmissionReasons.FieldMissing);

        Assert.Equal("unsupported", unsupported.Status);
        Assert.Contains(unsupported.Omissions, omission => omission.Reason == QueryOmissionReasons.UnsupportedRelation);
    }

    [Fact]
    public async Task RealGames_RuleLookupAndCost_UseDeclaredStructures()
    {
        using var content = new RulesContentStore(BoardPaths.GetBasePath());
        var plan = CreatePlan(content);

        using var agricolaDoc = JsonDocument.Parse("""{"queries":[{"relation":"cost","entity":"build_rooms"}]}""");
        var agricola = Assert.Single((await plan.ExecutePlanAsync("agricola", agricolaDoc.RootElement)).Results);
        Assert.Equal("ok", agricola.Status);
        Assert.NotNull(agricola.Cost);
        Assert.Contains(agricola.Payloads, payload => payload.DefinitionSource == "declared_dependency");

        using var wingspanDoc = JsonDocument.Parse("""{"queries":[{"relation":"rule_lookup","entity":"bird_card","intent":"鸟牌怎么获得"}]}""");
        var wingspan = Assert.Single((await plan.ExecutePlanAsync("wingspan", wingspanDoc.RootElement)).Results);
        Assert.Equal("ok", wingspan.Status);
        Assert.Equal(new[] { "bird_card" }, wingspan.ResolvedTargets);
        Assert.Contains(wingspan.Payloads, payload => payload.DefinitionSource == "auto_candidate_read"
            && payload.CanonicalPath.Contains("draw_cards_action", StringComparison.Ordinal));

        using var puertoRicoDoc = JsonDocument.Parse("""{"queries":[{"relation":"cost","entity":"builder_role"}]}""");
        var puertoRico = Assert.Single((await plan.ExecutePlanAsync("puerto-rico", puertoRicoDoc.RootElement)).Results);
        Assert.Equal(QueryCoverageStatuses.Partial, puertoRico.Coverage);
        Assert.Contains(puertoRico.Omissions, omission => omission.Reason == QueryOmissionReasons.FieldMissing);
    }

    private static JsonElement DataJson(QueryPayload payload)
        => JsonSerializer.SerializeToElement(payload.Data);

    private static RulesFixture CreateRuleFixture()
    {
        var fixture = new RulesFixture();
        var deps = string.Join(",", Enumerable.Range(1, 10)
            .Select(index => $"\"dep{index}\": \"<dep{index}>\""));
        var depsObjects = string.Join(",", Enumerable.Range(1, 10)
            .Select(index => $"{{ \"id\": \"dep{index}\", \"name\": {{ \"zh\": \"依赖{index}\" }} }}"));
        File.WriteAllText(fixture.GameConceptsPath, $$"""
        {
          "objects": [
            { "id": "crystal", "name": { "zh": "水晶" } },
            { "id": "capacity_holder", "name": { "zh": "容量载体" },
              "capacity": {
                "formula": { "op": "*", "operands": [ { "var": "spaces" }, 2 ] },
                "examples": [ { "zh": "两个空间", "bindings": { "spaces": 2 } } ]
              }
            },
            { "id": "missing_parameters", "name": { "zh": "缺参数载体" },
              "capacity": {
                "formula": { "var": "spaces" }
              }
            },
            {{depsObjects}}
          ],
          "actions": [
            { "id": "gain_crystal", "name": { "zh": "获得水晶" }, "<ontology::content>": "<crystal>" },
            { "id": "spend_crystal", "name": { "zh": "花费水晶" }, "<ontology::cost>": "<crystal>" },
            { "id": "mention_crystal", "name": { "zh": "水晶说明" }, "description": { "zh": "关于 <crystal> 的说明" } },
            { "id": "target_crystal", "name": { "zh": "瞄准水晶" }, "target": "<crystal>" },
            { "id": "condition_crystal", "name": { "zh": "水晶条件" }, "<ontology::condition>": "<crystal>" },
            { "id": "missing_dependency_action", "name": { "zh": "缺依赖" }, "<ontology::cost>": "<missing_dep>" },
            { "id": "many_dependency_action", "name": { "zh": "多依赖" }, "<ontology::cost>": { {{deps}} } }
          ],
          "triggers": [],
          "conditions": []
        }
        """);
        return fixture;
    }

    private static List<string> AutoReadPaths(PlanItemResult item)
        => item.Payloads
            .Where(payload => payload.DefinitionSource == "auto_candidate_read")
            .Select(payload => payload.CanonicalPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static RulesPlanService CreatePlan(RulesContentStore content)
    {
        var catalog = new RulesConceptCatalog(content);
        var search = new RulesSearchService(catalog, vectorSearch: null);
        var nameIndex = new RulesNameIndexService(catalog, content);
        var flow = new RulesFlowService(content);
        var reference = new RulesReferenceService(catalog, nameIndex);
        var facts = new RulesFactService(content);
        return new RulesPlanService(catalog, search, nameIndex, flow, reference, facts, vectorSearch: null);
    }
}
