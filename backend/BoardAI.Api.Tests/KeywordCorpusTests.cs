using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public class KeywordCorpusTests
{
    [Fact]
    public async Task SnapshotCorpus_PreservesStringBoundaries_AndDoesNotLeakMutableResults()
    {
        using var content = CreateContent();
        var search = new RulesSearchService(new RulesConceptCatalog(content), null);
        using var scope = RulesSnapshotScope.Enter(content.GetSnapshot("test"));
        Assert.Empty(search.KeywordSearch("test", "alphabeta"));
        Assert.Empty(search.KeywordSearch("test", "property_only"));
        var first = Assert.Single(search.KeywordSearch("test", "alpha"));
        first.Name = "corrupted";
        first.Description = "corrupted";
        first.Media?.Clear();
        var second = Assert.Single(search.KeywordSearch("test", "alpha"));
        Assert.Equal("Widget", second.Name);
        Assert.NotEqual("corrupted", second.Description);
        Assert.NotEmpty(second.Media!);
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => search.SearchConceptsAsync("test", "alpha beta")));
        Assert.All(results, result => Assert.Equal("widget", Assert.Single(result.Results).Id));
        Assert.Empty(search.KeywordSearch("test", "ontology::alpha"));
    }

    [Fact]
    public void ReplacedSnapshot_RebuildsCorpus_WhileOldScopeRetainsItsData()
    {
        using var content = CreateContent();
        var search = new RulesSearchService(new RulesConceptCatalog(content), null);
        var old = content.GetSnapshot("test");
        using (RulesSnapshotScope.Enter(old)) Assert.Single(search.KeywordSearch("test", "alpha"));
        content.SetDocumentForTesting("content/games/test/concepts.json",
            """{"objects":[{"id":"new","name":{"zh":"gamma"}}]}""");
        var updated = content.GetSnapshot("test");
        using (RulesSnapshotScope.Enter(updated))
        {
            Assert.Empty(search.KeywordSearch("test", "alpha"));
            Assert.Single(search.KeywordSearch("test", "gamma"));
        }
        using (RulesSnapshotScope.Enter(old)) Assert.Single(search.KeywordSearch("test", "alpha"));
    }

    private static RulesContentStore CreateContent()
    {
        var content = new RulesContentStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        content.SetDocumentForTesting("content/ontology/concepts.json", """{"concepts":[]}""");
        content.SetDocumentForTesting("content/games/test/concepts.json", """
            {"objects":[{"id":"widget","name":{"zh":"Widget"},
              "description":{"zh":"alpha"},"nested":["beta",{"property_only":42}],
              "media":{"component":["image.png"]}}]}
            """);
        return content;
    }
}
