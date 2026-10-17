using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesPlanServiceTests
{
    [Theory]
    [InlineData(.85f, .65f, false)]
    [InlineData(.60f, .85f, true)]
    [InlineData(.70f, .70f, true)]
    [InlineData(0f, .40f, false)]
    public void IntentConfirmationUsesCompetingEvidence(float objectScore, float actionScore, bool expected)
    {
        var candidates = new[]
        {
            new ConceptSummary { Id = "widget", Score = objectScore },
            new ConceptSummary { Id = "use_widget", Score = actionScore }
        };
        Assert.Equal(expected, RulesPlanService.NeedsIntentTargetConfirmation(candidates, new[] { "widget" }));
    }

    [Theory]
    [InlineData("小装置是什么")]
    [InlineData("小装置到底是什么")]
    public async Task DefinitionWordingPreservesExactIdentity(string intent)
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            queries = new[] { new { relation = "explain", entity = "小装置", intent } }
        }));
        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", document.RootElement)).Results);
        Assert.Equal("ok", item.Status);
        Assert.Equal("widget", GetId(Assert.Single(item.Matched)));
    }

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
    public async Task ExecutePlanAsync_Identify_QuestionNameIsOnlyAFallbackCandidate()
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
        Assert.Equal("unresolved", item.Status);
        Assert.Equal("", item.Source);
        Assert.Empty(item.Matched);
        Assert.Equal("widget", Assert.Single(item.Candidates!).Id);
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


    [Fact]
    public async Task Identify_ColorQualifierIsNotReplacedByGenericQuestionName()
    {
        using var fixture = new RulesFixture();
        File.WriteAllText(fixture.GameConceptsPath,
            """
            {"objects":[
              {"id":"dice","name":{"zh":"骰子"},"description":{"zh":"产生随机数的组件"}},
              {"id":"activation_die","name":{"zh":"激活骰"},"description":{"zh":"白色的六面骰子，用来激活模组"}}
            ]}
            """);
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var doc = JsonDocument.Parse("""{"queries":[{"relation":"identify","entity":"白色 骰子"}]}""");
        var result = await plan.ExecutePlanAsync("testgame", doc.RootElement, "白色骰子");
        var item = Assert.Single(result.Results);
        Assert.Equal("unresolved", item.Status);
        Assert.Empty(item.Matched);
        Assert.Equal("activation_die", item.Candidates![0].Id);
    }

    [Theory]
    [InlineData("explain", "小装置的量子护盾怎么使用")]
    [InlineData("condition", "使用小装置后能启动量子护盾吗")]
    public async Task UnknownEntity_QuestionNameOnlySuppliesCandidates(string relation, string question)
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            queries = new[] { new { relation, entity = "量子护盾" } }
        }));
        var item = Assert.Single((await plan.ExecutePlanAsync("testgame", doc.RootElement, question)).Results);
        Assert.Equal("unresolved", item.Status);
        Assert.Equal("", item.Source);
        Assert.Empty(item.Matched);
        Assert.NotEmpty(item.Candidates!);
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

    [Theory]
    [InlineData("{\"relation\":\"explain\",\"entity\":\"widget\"}", "客人原问句", "客人原问句")]
    [InlineData("{\"relation\":\"explain\",\"entity\":\"小装置\",\"intent\":\"设置时如何使用小装置\"}", "客人原问句", "设置时如何使用小装置")]
    public async Task ExecutePlanAsync_SingleQueryIntentIsPreservedOrFallsBackToQuestion(
        string query, string question, string expectedIntent)
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse("{\"queries\":[" + query + "]}");

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement, question);

        var item = Assert.Single(result.Results);
        Assert.Equal(expectedIntent, item.Intent);
        Assert.Equal(0, item.QueryIndex);
        if (!string.IsNullOrEmpty(query) && query.Contains("\"intent\""))
            Assert.Equal(new[] { "小装置", "设置时如何使用小装置" }, item.RetrievalQueries);
        else
            Assert.Equal(new[] { "widget" }, item.RetrievalQueries);
    }

    [Fact]
    public async Task ExecutePlanAsync_ExplicitIdIdentityIsNotReplacedByScenarioIntent()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """{"queries":[{"relation":"explain","entity":"widget","intent":"设置时如何摆放小装置"}]}""");

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement);

        var item = Assert.Single(result.Results);
        Assert.Equal("ok", item.Status);
        Assert.Equal("exact_id", item.Source);
        Assert.Equal("widget", GetId(Assert.Single(item.Matched)));
    }

    [Fact]
    public async Task ExecutePlanAsync_MultipleQueriesWithoutIntent_DoNotReuseWholeQuestionForEachQuery()
    {
        using var fixture = new RulesFixture();
        using var content = new RulesContentStore(fixture.Root);
        var plan = CreateService(content);
        using var document = JsonDocument.Parse(
            """{"queries":[{"relation":"explain","entity":"widget"},{"relation":"list","entity":""}]}""");

        var result = await plan.ExecutePlanAsync("testgame", document.RootElement, "整段多子问题原问句");

        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, item => Assert.Equal("", item.Intent));
        Assert.Equal(new[] { 0, 1 }, result.Results.Select(item => item.QueryIndex));
    }

}
