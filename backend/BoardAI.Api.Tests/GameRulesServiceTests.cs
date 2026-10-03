using System.Text.Json;

namespace BoardAI.Api.Tests;

public sealed class GameRulesServiceTests
{
    [Fact]
    public void GetConceptTypes_ReturnsFixtureConceptTypes()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();

        var types = service.GetConceptTypes("testgame");

        Assert.Contains("ontology", types);
        Assert.Contains("objects", types);
        Assert.Contains("actions", types);
    }

    [Fact]
    public void GetConcepts_WithExactId_ReturnsWidget()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();

        var concepts = service.GetConcepts("testgame", "widget");

        Assert.Single(concepts);
        Assert.Equal("widget", concepts[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ExecutePlanAsync_ExplainWithExactId_ReturnsMatchedWidget()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();
        using var planDocument = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "explain", "entity": "widget" }
              ]
            }
            """);

        var result = await service.ExecutePlanAsync("testgame", planDocument.RootElement);

        Assert.Single(result.Results);
        var item = result.Results[0];
        Assert.Equal("ok", item.Status);
        Assert.Single(item.Matched);
        Assert.Equal("widget", item.Matched[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ExecutePlanAsync_Condition_ReturnsConditionCostAndTarget()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();
        using var planDocument = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "condition", "entity": "use_widget" }
              ]
            }
            """);

        var result = await service.ExecutePlanAsync("testgame", planDocument.RootElement);

        Assert.Single(result.Results);
        var item = result.Results[0];
        Assert.Equal("ok", item.Status);
        Assert.NotNull(item.Condition);
        Assert.NotNull(item.Cost);
        Assert.NotNull(item.Target);
    }

    [Fact]
    public async Task ExecutePlanAsync_List_ReturnsNonEmptyCatalog()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();
        using var planDocument = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "list", "entity": "" }
              ]
            }
            """);

        var result = await service.ExecutePlanAsync("testgame", planDocument.RootElement);

        Assert.Single(result.Results);
        var item = result.Results[0];
        Assert.Equal("ok", item.Status);
        Assert.NotNull(item.Catalog);
        Assert.True(item.Catalog.TotalCount > 0);
    }

    [Fact]
    public async Task ExecutePlanAsync_AfterGameConceptsChange_RebuildsDerivedIndex()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();

        using var initialPlanDocument = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "explain", "entity": "小装置" }
              ]
            }
            """);
        var initial = await service.ExecutePlanAsync("testgame", initialPlanDocument.RootElement);

        Assert.Single(initial.Results);
        Assert.Equal("ok", initial.Results[0].Status);
        Assert.Equal("widget", initial.Results[0].Matched[0].GetProperty("id").GetString());

        fixture.WriteGameConcepts("小装置改", "使用小装置改");

        using var updatedPlanDocument = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "explain", "entity": "小装置改" }
              ]
            }
            """);
        var updated = await service.ExecutePlanAsync("testgame", updatedPlanDocument.RootElement);

        Assert.Single(updated.Results);
        Assert.Equal("ok", updated.Results[0].Status);
        Assert.Equal("exact_name_zh", updated.Results[0].Source);
        Assert.Equal("widget", updated.Results[0].Matched[0].GetProperty("id").GetString());

        // 中文名存在子串重叠时，名称包含兜底仍会命中 contain_unique。
        // 本测试锁定：派生索引按最新文档重建，并优先命中精确名。
    }

    [Fact]
    public async Task SearchConceptsAsync_WithoutVectorSearch_FindsWidgetByKeyword()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();

        var result = await service.SearchConceptsAsync("testgame", "小装置");

        Assert.Contains(result.Results, r => r.Id == "widget");
    }
}
