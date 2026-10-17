using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class QueryResultContractTests
{
    [Fact]
    public async Task List_ReturnsCatalogPagePayload_AndEvidenceCollectorTreatsItAsData()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"list"}]}""");

        var result = await plan.ExecutePlanAsync("testgame", doc.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.Equal(QueryResolutionStatuses.Exact, item.Resolution);
        Assert.Equal(QueryCoverageStatuses.Complete, item.Coverage);
        Assert.Contains(item.Payloads, payload => payload.Type == QueryPayloadTypes.CatalogPage);
        Assert.Empty(item.Omissions);

        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(item));
        var evidence = ChatOrchestratorService.ParseQueryEvidence(serialized.RootElement, result.RulesVersion);
        Assert.True(evidence.HasData);
        Assert.Contains(QueryPayloadTypes.CatalogPage, evidence.PayloadTypes);
    }

    [Fact]
    public async Task ExactHit_ReturnsDefinitionPayload_AndNoCandidates()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":"widget"}]}""");

        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement)).Results);

        Assert.Equal(QueryResolutionStatuses.Exact, item.Resolution);
        Assert.Null(item.Candidates);
        Assert.Equal(new[] { "widget" }, item.ResolvedTargets);
        Assert.Contains(item.Payloads, payload => payload.Type == QueryPayloadTypes.Definition);
    }

    [Fact]
    public async Task SameNamedSlots_ResolveOnlyThroughTheirOwnerPaths()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.GameConceptsPath, """
        {
          "objects": [
            { "id": "owner_a", "name": { "zh": "甲" }, "slots": [ { "id": "slot", "name": { "zh": "同名槽甲" } } ] },
            { "id": "owner_b", "name": { "zh": "乙" }, "slots": [ { "id": "slot", "name": { "zh": "同名槽乙" } } ] }
          ],
          "actions": [], "triggers": [], "conditions": []
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var docA = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":"owner_a.slot"}]}""");
        using var docB = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":"owner_b.slot"}]}""");

        var a = Assert.Single((await plan.ExecutePlanAsync("testgame", docA.RootElement)).Results);
        var b = Assert.Single((await plan.ExecutePlanAsync("testgame", docB.RootElement)).Results);

        Assert.Equal(QueryResolutionStatuses.Exact, a.Resolution);
        Assert.Equal(QueryResolutionStatuses.Exact, b.Resolution);
        Assert.Equal(new[] { "owner_a.slot" }, a.ResolvedTargets);
        Assert.Equal(new[] { "owner_b.slot" }, b.ResolvedTargets);
        Assert.DoesNotContain("owner_b.slot", a.ResolvedTargets);
        Assert.DoesNotContain("owner_a.slot", b.ResolvedTargets);
    }

    [Fact]
    public async Task GameAndOntologySameId_AreNotMixedWithoutNamespace()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.OntologyConceptsPath, """
        {
          "concepts": [
            { "id": "widget", "name": { "zh": "本体小装置" }, "abstract": true, "definition": "本体版" }
          ]
        }
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var gameDoc = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":"widget"}]}""");
        using var ontologyDoc = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":"ontology::widget"}]}""");

        var game = Assert.Single((await plan.ExecutePlanAsync("testgame", gameDoc.RootElement)).Results);
        var ontology = Assert.Single((await plan.ExecutePlanAsync("testgame", ontologyDoc.RootElement)).Results);

        Assert.Equal(new[] { "widget" }, game.ResolvedTargets);
        Assert.Equal(new[] { "ontology::widget" }, ontology.ResolvedTargets);
    }

    [Fact]
    public async Task ConditionWithoutFields_IsPartialWithExplicitOmission()
    {
        using var fixture = new RulesFixture();
        var json = JsonDocument.Parse("""
        {
          "objects": [],
          "actions": [ { "id": "bare_action", "name": { "zh": "无字段行动" } } ],
          "triggers": [], "conditions": []
        }
        """);
        File.WriteAllText(fixture.GameConceptsPath, json.RootElement.GetRawText());
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"condition","entity":"bare_action"}]}""");

        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement)).Results);

        Assert.Equal(QueryCoverageStatuses.Partial, item.Coverage);
        Assert.Contains(item.Omissions, omission => omission.Reason == QueryOmissionReasons.FieldMissing);
        Assert.Contains(item.Payloads, payload =>
            payload.Type == QueryPayloadTypes.RelationFacts
            && payload.Completeness == QueryCoverageStatuses.Partial);
    }

    [Fact]
    public void UnknownPayload_IsUnsupportedInsteadOfOkWithDroppedBody()
    {
        using var doc = JsonDocument.Parse("""
        {
          "Status": "ok",
          "Resolution": "exact",
          "Payloads": [ { "Type": "mystery_payload", "CanonicalPath": "mystery" } ]
        }
        """);

        var evidence = ChatOrchestratorService.ParseQueryEvidence(doc.RootElement, "rules-v1");

        Assert.Equal("unsupported", evidence.Status);
        Assert.Equal(QueryResolutionStatuses.Unsupported, evidence.Resolution);
        Assert.False(evidence.HasData);
        Assert.Contains("mystery_payload", evidence.PayloadTypes);
    }

    [Fact]
    public async Task UnresolvedAndNotFound_AreDistinct()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var unresolvedDoc = JsonDocument.Parse("""{"queries":[{"relation":"identify","entity":"某个未命中的外观描述"}]}""");
        using var missingDoc = JsonDocument.Parse("""{"queries":[{"relation":"explain","entity":""}]}""");

        var unresolved = Assert.Single((await plan.ExecutePlanAsync(
            "testgame", unresolvedDoc.RootElement, "小装置在哪里")).Results);
        var missing = Assert.Single((await plan.ExecutePlanAsync("testgame", missingDoc.RootElement)).Results);

        Assert.Equal(QueryResolutionStatuses.Candidates, unresolved.Resolution);
        Assert.NotEmpty(unresolved.Candidates!);
        Assert.Equal(QueryResolutionStatuses.NotFound, missing.Resolution);
        Assert.Empty(missing.Candidates ?? new List<ConceptSummary>());
    }


    [Fact]
    public async Task EmptyCatalog_IsCompleteEmptySet_NotNotFoundOrUnknown()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.OntologyConceptsPath, """{"concepts":[]}""");
        File.WriteAllText(fixture.GameConceptsPath, """
        {"objects":[],"actions":[],"triggers":[],"conditions":[]}
        """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreatePlan(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"list"}]}""");

        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement)).Results);

        Assert.Equal("ok", item.Status);
        Assert.Equal(QueryCoverageStatuses.Complete, item.Coverage);
        Assert.Equal(0, item.Catalog!.TotalCount);
        var payload = Assert.Single(item.Payloads);
        Assert.Equal(QueryPayloadTypes.CatalogPage, payload.Type);
        Assert.Equal(QueryCoverageStatuses.Complete, payload.Completeness);
        Assert.Empty(item.Omissions);
    }

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
