using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesSearchServiceTests
{
    [Fact]
    public void KeywordSearch_IdContainsQuery_ReturnsWidget()
    {
        var catalog = new FakeRulesConceptCatalog(
            types: new[] { "objects" },
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[] { Widget() }
            });

        var service = new RulesSearchService(catalog, vectorSearch: null);

        var results = service.KeywordSearch("testgame", "widget");

        Assert.Contains(results, r => r.Id == "widget");
    }

    [Fact]
    public void KeywordSearch_NameContainsQueryButIdDoesNot_ReturnsConcept()
    {
        var catalog = new FakeRulesConceptCatalog(
            types: new[] { "objects" },
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[]
                {
                    new ConceptSummary
                    {
                        Id = "concept_a",
                        Name = "blue widget",
                        Type = "objects"
                    }
                }
            });

        var service = new RulesSearchService(catalog, vectorSearch: null);

        var results = service.KeywordSearch("testgame", "widget");

        Assert.Contains(results, r => r.Id == "concept_a");
    }

    [Fact]
    public void KeywordSearch_DescriptionContainsQuery_ReturnsConcept()
    {
        var catalog = new FakeRulesConceptCatalog(
            types: new[] { "objects" },
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[] { new ConceptSummary { Id = "box", Name = "盒子", Type = "objects" } }
            },
            details: new Dictionary<string, string>
            {
                ["box"] = """{"description":{"zh":"A hidden treasure"}}"""
            });

        var service = new RulesSearchService(catalog, vectorSearch: null);

        var results = service.KeywordSearch("testgame", "hidden");

        Assert.Contains(results, r => r.Id == "box");
    }

    [Fact]
    public void ListAllConceptIds_GroupsByTypeAndCounts()
    {
        var catalog = new FakeRulesConceptCatalog(
            types: new[] { "objects", "actions" },
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[] { Widget() },
                ["actions"] = new[] { new ConceptSummary { Id = "use_widget", Name = "使用小装置", Type = "actions" } }
            });

        var service = new RulesSearchService(catalog, vectorSearch: null);

        var result = service.ListAllConceptIds("testgame");

        Assert.Equal(2, result.TotalCount);
        Assert.Contains("objects", result.ByType.Keys);
        Assert.Contains("actions", result.ByType.Keys);
        Assert.Equal("widget", result.ByType["objects"].Single().Id);
        Assert.Equal("use_widget", result.ByType["actions"].Single().Id);
    }

    [Fact]
    public async Task SearchConceptsAsync_EmptyQuery_ReturnsEmptyResults()
    {
        var service = new RulesSearchService(new FakeRulesConceptCatalog(), vectorSearch: null);

        var result = await service.SearchConceptsAsync("testgame", "   ");

        Assert.Empty(result.Results);
        Assert.Equal("   ", result.Query);
        Assert.Empty(result.SplitTerms);
    }

    [Fact]
    public async Task SearchConceptsAsync_WithoutVectorSearch_UsesKeywordChannel()
    {
        var catalog = new FakeRulesConceptCatalog(
            types: new[] { "objects" },
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[] { Widget() }
            });
        var service = new RulesSearchService(catalog, vectorSearch: null);

        var result = await service.SearchConceptsAsync("testgame", "widget");

        Assert.Contains(result.Results, r => r.Id == "widget");
        Assert.NotEmpty(result.SplitTerms);
    }

    [Fact]
    public async Task SearchConceptsAsync_PopulatesDescriptionFromConceptDetail()
    {
        var catalog = new FakeRulesConceptCatalog(
            types: new[] { "objects" },
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[] { Widget() }
            },
            details: new Dictionary<string, string>
            {
                ["widget"] = """{"description":{"zh":"A useful widget"}}"""
            });
        var service = new RulesSearchService(catalog, vectorSearch: null);

        var result = await service.SearchConceptsAsync("testgame", "widget");

        var widget = Assert.Single(result.Results, r => r.Id == "widget");
        Assert.Equal("A useful widget", widget.Description);
    }


    [Fact]
    public async Task SearchConceptsAsync_FullKeywordQueryRequiresAllTermsInOneConcept()
    {
        var catalog = new FakeRulesConceptCatalog(
            conceptsByType: new Dictionary<string, IReadOnlyList<ConceptSummary>>
            {
                ["objects"] = new[] { new ConceptSummary { Id = "aid", Name = "辅助卡", Type = "objects" } }
            },
            details: new Dictionary<string, string>
            {
                ["aid"] = """{"description":{"zh":"用于查看规则摘要的卡"}}"""
            });
        var service = new RulesSearchService(catalog, vectorSearch: null);
        var result = await service.SearchConceptsAsync("testgame", "point bonus 卡");
        var aid = Assert.Single(result.Results);
        Assert.Equal(0, aid.PhraseScore!.Keyword);
        Assert.True(aid.Score < .05f);
    }

    private static ConceptSummary Widget() => new()
    {
        Id = "widget",
        Name = "小装置",
        Type = "objects",
        Description = ""
    };

    private sealed class FakeRulesConceptCatalog : IRulesConceptCatalog
    {
        private readonly IReadOnlyList<string> _types;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ConceptSummary>> _conceptsByType;
        private readonly IReadOnlyDictionary<string, string> _details;

        public FakeRulesConceptCatalog(
            IReadOnlyList<string>? types = null,
            IReadOnlyDictionary<string, IReadOnlyList<ConceptSummary>>? conceptsByType = null,
            IReadOnlyDictionary<string, string>? details = null)
        {
            _types = types ?? Array.Empty<string>();
            _conceptsByType = conceptsByType ?? new Dictionary<string, IReadOnlyList<ConceptSummary>>();
            _details = details ?? new Dictionary<string, string>();
        }

        public IReadOnlyList<string> GetConceptTypes(string game) => _types;

        public IReadOnlyList<ConceptSummary> ListConcepts(string game, string type)
            => _conceptsByType.TryGetValue(type, out var concepts)
                ? concepts
                : Array.Empty<ConceptSummary>();

        public JsonElement? GetConcept(string game, string id)
        {
            if (!_details.TryGetValue(id, out var json))
                return null;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
