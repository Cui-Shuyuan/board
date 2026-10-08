using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesPlanServiceTests
{
    [Fact]
    public async Task ExecutePlanAsync_ExplainExactId_ReturnsWidget()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "explain", "entity": "widget" }
              ]
            }
            """);

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.Equal("exact_id", item.Source);
        Assert.Single(item.Matched);
        Assert.Equal("widget", GetId(item.Matched[0]));
    }

    [Fact]
    public async Task ExecutePlanAsync_Condition_ReturnsConditionCostTarget()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "condition", "entity": "use_widget" }
              ]
            }
            """);

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.NotNull(item.Condition);
        Assert.NotNull(item.Cost);
        Assert.NotNull(item.Target);
    }

    [Fact]
    public async Task ExecutePlanAsync_List_ReturnsCatalog()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "list", "entity": "" }
              ]
            }
            """);

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.NotNull(item.Catalog);
        Assert.True(item.Catalog.TotalCount > 0);
    }

    [Fact]
    public async Task ExecutePlanAsync_UnsupportedRelation_ReturnsUnsupported()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "bogus", "entity": "widget" }
              ]
            }
            """);

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("unsupported", item.Status);
        Assert.Contains("支持列表", item.Message);
    }

    [Fact]
    public async Task ExecutePlanAsync_Flow_WhenGameFlowExists_ReturnsMatchedGameConcept()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(
            fixture.GameFlowPath,
            """
            {
              "procedures": [
                { "id": "game", "name": { "zh": "游戏流程" } }
              ]
            }
            """);

        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "flow", "entity": "" }
              ]
            }
            """);

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.Single(item.Matched);
        Assert.Equal("game", GetId(item.Matched[0]));
    }

    [Fact]
    public async Task ExecutePlanAsync_Identify_QuestionHit_ReturnsMatchedConcept()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "identify", "entity": "某个外观描述" }
              ]
            }
            """);

        var result = await plan.ExecutePlanAsync(
            "testgame",
            document.RootElement,
            question: "小装置在哪里");

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.Equal("question_hit", item.Source);
        Assert.Single(item.Matched);
        Assert.Equal("widget", GetId(item.Matched[0]));
    }

    [Fact]
    public async Task ExecutePlanAsync_PreCancelledToken_ThrowsBeforeExecuting()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "explain", "entity": "widget" }
              ]
            }
            """);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => plan.ExecutePlanAsync("testgame", document.RootElement, cancellationToken: cts.Token));
    }

    private static RulesPlanService CreateService(RulesContentStore content)
    {
        var catalog = new RulesConceptCatalog(content);
        var search = new RulesSearchService(catalog, vectorSearch: null);
        var nameIndex = new RulesNameIndexService(catalog, content);
        var flow = new RulesFlowService(content);
        var reference = new RulesReferenceService(catalog, nameIndex);
        var facts = new RulesFactService(content);

        return new RulesPlanService(
            catalog,
            search,
            nameIndex,
            flow,
            reference,
            facts,
            vectorSearch: null);
    }

    private static string GetId(JsonElement element)
        => element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
}
