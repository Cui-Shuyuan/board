using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class FlowScopeAndMembersTests
{
    [Fact]
    public async Task SameLocalStepUnderTwoParents_IsResolvedOnlyByCanonicalPath()
    {
        using var fixture = new RulesFixture();
        WriteFlow(fixture, """
        {
          "procedures": [
            {
              "id": "root",
              "name": { "zh": "根" },
              "<ontology::pipeline>": {
                "options": [
                  {
                    "id": "parent_a",
                    "name": { "zh": "父甲" },
                    "loop": { "count": 3 },
                    "condition": "ready_a",
                    "<ontology::pipeline>": {
                      "options": [ { "id": "step", "name": { "zh": "同名步骤" } } ]
                    }
                  },
                  {
                    "id": "parent_b",
                    "name": { "zh": "父乙" },
                    "do_after": [ "parent_a" ],
                    "<ontology::pipeline>": {
                      "options": [ { "id": "step", "name": { "zh": "同名步骤" } } ]
                    }
                  }
                ]
              }
            }
          ]
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var ambiguousDoc = JsonDocument.Parse("""{"queries":[{"relation":"flow","scope":"local","entity":"step"}]}""");

        var ambiguous = Assert.Single((await plan.ExecutePlanAsync("testgame", ambiguousDoc.RootElement)).Results);
        Assert.Equal("unresolved", ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates!.Count);
        Assert.Equal(2, ambiguous.Candidates.Select(candidate => candidate.Path).Distinct().Count());

        var parentAPath = ambiguous.Candidates.Single(candidate => candidate.Describe().Contains("parent_a")).Path;
        using var exactDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            queries = new[] { new { relation = "flow", scope = "local", entity = parentAPath } }
        }));
        var exact = Assert.Single((await plan.ExecutePlanAsync("testgame", exactDoc.RootElement)).Results);
        Assert.Equal("ok", exact.Status);
        Assert.Equal(new[] { parentAPath }, exact.ResolvedTargets);
        Assert.NotEqual(parentAPath, ambiguous.Candidates.Single(candidate => candidate.Describe().Contains("parent_b")).Path);
    }

    [Fact]
    public async Task LocalFlow_ReturnsTargetAndNecessaryParentContext_WithoutWholeGameFallback()
    {
        using var fixture = new RulesFixture();
        WriteFlow(fixture, """
        {
          "procedures": [
            {
              "id": "game",
              "name": { "zh": "游戏" },
              "options": [
                {
                  "id": "round_loop",
                  "name": { "zh": "回合循环" },
                  "loop": { "until": "game_end" },
                  "options": [
                    {
                      "id": "harvest",
                      "name": { "zh": "收获" },
                      "condition": "harvest_round",
                      "options": [
                        { "id": "feeding", "name": { "zh": "喂养" }, "do_after": [ "field" ] },
                        { "id": "breeding", "name": { "zh": "繁殖" } }
                      ]
                    },
                    { "id": "unrelated_action", "name": { "zh": "无关行动" } }
                  ]
                }
              ]
            }
          ]
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var localDoc = JsonDocument.Parse("""{"queries":[{"relation":"flow","scope":"local","entity":"harvest","include":["children","position","conditions"]}]}""");
        var local = Assert.Single((await plan.ExecutePlanAsync("testgame", localDoc.RootElement)).Results);

        Assert.Equal("ok", local.Status);
        var payload = Assert.Single(local.Payloads);
        Assert.Equal(QueryPayloadTypes.FlowSlice, payload.Type);
        var data = PayloadData(payload);
        Assert.Equal("local", data.GetProperty("scope").GetString());
        Assert.Equal("harvest", data.GetProperty("target").GetProperty("id").GetString());
        var ancestors = data.GetProperty("ancestors").EnumerateArray().ToList();
        Assert.Contains(ancestors, a => a.GetProperty("id").GetString() == "round_loop"
            && a.GetProperty("loop").ValueKind == JsonValueKind.Object);
        var children = data.GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(new[] { "feeding", "breeding" }, children.Select(child => child.GetProperty("id").GetString()));
        Assert.DoesNotContain("unrelated_action", payload.Content!.Value.GetRawText());

        using var feedingDoc = JsonDocument.Parse("""{"queries":[{"relation":"flow","scope":"local","entity":"feeding"}]}""");
        var feeding = Assert.Single((await plan.ExecutePlanAsync("testgame", feedingDoc.RootElement)).Results);
        var feedingData = PayloadData(Assert.Single(feeding.Payloads));
        Assert.Contains(feedingData.GetProperty("ancestors").EnumerateArray(),
            a => a.GetProperty("id").GetString() == "harvest");
        Assert.Empty(feedingData.GetProperty("children").EnumerateArray());

        using var gameDoc = JsonDocument.Parse("""{"queries":[{"relation":"flow","scope":"game"}]}""");
        var game = Assert.Single((await plan.ExecutePlanAsync("testgame", gameDoc.RootElement)).Results);
        Assert.Equal("ok", game.Status);
        Assert.Equal(6, PayloadData(game.Payloads[0]).GetProperty("node_count").GetInt32());

        using var invalidDoc = JsonDocument.Parse("""{"queries":[{"relation":"flow","scope":"local"}]}""");
        var invalid = Assert.Single((await plan.ExecutePlanAsync("testgame", invalidDoc.RootElement)).Results);
        Assert.Equal("invalid", invalid.Status);
        Assert.Contains(invalid.Omissions, omission => omission.Reason == QueryOmissionReasons.InvalidRequest);

        using var missingScopeDoc = JsonDocument.Parse("""{"queries":[{"relation":"flow","entity":"feeding"}]}""");
        var missingScope = Assert.Single((await plan.ExecutePlanAsync("testgame", missingScopeDoc.RootElement)).Results);
        Assert.Equal("invalid", missingScope.Status);
    }

    [Fact]
    public async Task CatalogPagination_ReconstructsExactlyOnceWithNoLossOrDuplicates()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.GameConceptsPath, """
        {
          "objects": [
            { "id": "obj_a", "name": { "zh": "甲" } },
            { "id": "obj_b", "name": { "zh": "乙" } },
            { "id": "obj_c", "name": { "zh": "丙" } }
          ],
          "actions": [ { "id": "act_a", "name": { "zh": "行动甲" } } ],
          "triggers": [], "conditions": []
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        var seen = new List<string>();
        var offset = 0;
        var total = 0;
        while (true)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                queries = new[] { new { relation = "list", offset, limit = 2 } }
            }));
            var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement)).Results);
            var page = item.Payloads.Single(payload => payload.Type == QueryPayloadTypes.CatalogPage);
            total = page.Data!["total_count"].GetInt32();
            var ids = page.Content!.Value.GetProperty("ByType").EnumerateObject()
                .SelectMany(property => property.Value.EnumerateArray())
                .Select(concept => concept.GetProperty("Id").GetString()!)
                .ToList();
            seen.AddRange(ids);
            if (!page.Data!["has_more"].GetBoolean()) break;
            offset += ids.Count;
        }

        Assert.Equal(total, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Members_UsesOnlyExplicitCollectionsOrSchemaRelations_AndDistinguishesUnknownCoverage()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.GameConceptsPath, """
        {
          "objects": [
            { "id": "thing", "name": { "zh": "类别" }, "abstract": true },
            { "id": "by_specifies", "name": { "zh": "指定实例" }, "specifies": "<thing>" },
            { "id": "by_instance", "name": { "zh": "个体实例" }, "instance_of": "<thing>" },
            { "id": "by_extends_chain", "name": { "zh": "继承实例" }, "specifies": "<derived_thing>" },
            { "id": "derived_thing", "name": { "zh": "派生类别" }, "specifies": "<thing>" },
            { "id": "only_mentions", "name": { "zh": "只提到" }, "description": { "zh": "见 <thing>" } },
            { "id": "explicit_empty", "name": { "zh": "空集合" }, "members": [] },
            { "id": "opaque", "name": { "zh": "未知全集" } }
          ],
          "actions": [], "triggers": [], "conditions": []
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var membersDoc = JsonDocument.Parse("""{"queries":[{"relation":"members","entity":"thing"}]}""");
        var members = Assert.Single((await plan.ExecutePlanAsync("testgame", membersDoc.RootElement)).Results);

        Assert.Equal("ok", members.Status);
        Assert.Equal(QueryCoverageStatuses.Complete, members.Coverage);
        var setPayload = members.Payloads.Single(payload => payload.Type == QueryPayloadTypes.MemberSet);
        var body = setPayload.Content!.Value;
        var memberIds = body.GetProperty("members").EnumerateArray()
            .Select(member => member.GetProperty("id").GetString()).ToList();
        Assert.Contains("by_specifies", memberIds);
        Assert.Contains("by_instance", memberIds);
        Assert.Contains("by_extends_chain", memberIds);
        Assert.Contains("derived_thing", memberIds);
        Assert.DoesNotContain("only_mentions", memberIds);

        using var pageDoc = JsonDocument.Parse("""{"queries":[{"relation":"members","entity":"thing","offset":0,"limit":2}]}""");
        var page = Assert.Single((await plan.ExecutePlanAsync("testgame", pageDoc.RootElement)).Results);
        Assert.Equal(QueryCoverageStatuses.Partial, page.Coverage);
        Assert.Contains(page.Omissions, omission => omission.Reason == QueryOmissionReasons.ContinuationRequired
            && omission.Continuation?.Offset == 2);
        var pageSet = page.Payloads.Single(payload => payload.Type == QueryPayloadTypes.MemberSet).Content!.Value;
        Assert.True(pageSet.GetProperty("has_more").GetBoolean());
        Assert.Equal(2, pageSet.GetProperty("members").GetArrayLength());
        Assert.Equal(4, pageSet.GetProperty("total_count").GetInt32());

        using var emptyDoc = JsonDocument.Parse("""{"queries":[{"relation":"members","entity":"explicit_empty"}]}""");
        var empty = Assert.Single((await plan.ExecutePlanAsync("testgame", emptyDoc.RootElement)).Results);
        Assert.Equal(QueryCoverageStatuses.Complete, empty.Coverage);
        Assert.Empty(empty.Omissions);
        Assert.Equal(0, empty.Payloads.Single(payload => payload.Type == QueryPayloadTypes.MemberSet)
            .Content!.Value.GetProperty("total_count").GetInt32());

        using var opaqueDoc = JsonDocument.Parse("""{"queries":[{"relation":"members","entity":"opaque"}]}""");
        var opaque = Assert.Single((await plan.ExecutePlanAsync("testgame", opaqueDoc.RootElement)).Results);
        Assert.Equal(QueryCoverageStatuses.Partial, opaque.Coverage);
        Assert.Contains(opaque.Omissions, omission => omission.Reason == QueryOmissionReasons.NoRuleCoverage);
        Assert.Contains(opaque.Payloads, payload => payload.Type == QueryPayloadTypes.Definition);
    }

    [Fact]
    public async Task SnapshotChange_RebuildsIndex_AndInFlightSnapshotRemainsConsistent()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        content.SetDocumentForTesting("content/games/testgame/flow.json", """
        { "procedures": [ { "id": "first", "name": { "zh": "第一" } } ] }
        """);
        var snapshot1 = content.GetSnapshot("testgame");
        FlowStructureIndex index1;
        using (RulesSnapshotScope.Enter(snapshot1))
            index1 = new RulesFlowService(content).GetFlowIndex("testgame");

        content.SetDocumentForTesting("content/games/testgame/flow.json", """
        { "procedures": [ { "id": "second", "name": { "zh": "第二" } } ] }
        """);
        var snapshot2 = content.GetSnapshot("testgame");
        Assert.NotEqual(snapshot1.Version, snapshot2.Version);
        FlowStructureIndex index2;
        using (RulesSnapshotScope.Enter(snapshot2))
            index2 = new RulesFlowService(content).GetFlowIndex("testgame");

        Assert.True(index1.ByPath.Keys.Any(path => path.Contains("first")));
        Assert.False(index1.ByPath.Keys.Any(path => path.Contains("second")));
        Assert.True(index2.ByPath.Keys.Any(path => path.Contains("second")));
        Assert.Equal(snapshot1.Version, index1.RulesVersion);

        using (RulesSnapshotScope.Enter(snapshot1))
        {
            Assert.Same(index1, new RulesFlowService(content).GetFlowIndex("testgame"));
        }
    }

    [Theory]
    [InlineData("agricola", "harvest_feeding", "harvest")]
    [InlineData("wingspan", "player_turn", "player_action_round")]
    public async Task RealGames_LocalFlowContractsUseStructure(string game, string entity, string expectedAncestorId)
    {
        using var content = new RulesContentStore(BoardPaths.GetBasePath());
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            queries = new[] { new { relation = "flow", scope = "local", entity } }
        }));
        var item = Assert.Single((await plan.ExecutePlanAsync(game, doc.RootElement)).Results);

        Assert.Equal("ok", item.Status);
        var payload = Assert.Single(item.Payloads);
        Assert.Equal(QueryPayloadTypes.FlowSlice, payload.Type);
        Assert.Contains(entity, payload.CanonicalPath);
        var ancestors = PayloadData(payload).GetProperty("ancestors").EnumerateArray()
            .Select(ancestor => ancestor.GetProperty("id").GetString()).ToList();
        Assert.Contains(expectedAncestorId, ancestors);
    }

    [Fact]
    public async Task Wingspan_HabitatMembership_ComesFromDeclaredSchemaRelations()
    {
        using var content = new RulesContentStore(BoardPaths.GetBasePath());
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"members","entity":"habitat"}]}""");
        var item = Assert.Single((await plan.ExecutePlanAsync("wingspan", doc.RootElement)).Results);

        Assert.Equal("ok", item.Status);
        var set = item.Payloads.Single(payload => payload.Type == QueryPayloadTypes.MemberSet);
        var ids = set.Content!.Value.GetProperty("members").EnumerateArray()
            .Select(member => member.GetProperty("id").GetString()).ToList();
        Assert.Contains("forest_habitat", ids);
        Assert.Contains("grassland_habitat", ids);
        Assert.Contains("wetland_habitat", ids);
        Assert.DoesNotContain("habitat", ids);
    }

    private static JsonElement PayloadData(QueryPayload payload)
        => JsonSerializer.SerializeToElement(payload.Data);

    private static void WriteFlow(RulesFixture fixture, string json)
        => File.WriteAllText(fixture.GameFlowPath, json);

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

internal static class ConceptSummaryTestExtensions
{
    public static string Describe(this ConceptSummary candidate)
        => candidate.Id + "|" + candidate.Path + "|" + candidate.Description;
}
