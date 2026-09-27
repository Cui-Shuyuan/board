using System.Text.Json;
using System.Text.Json.Nodes;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesReferenceServiceTests : IDisposable
{
    private readonly string _root;

    public RulesReferenceServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-reference-tests",
            Guid.NewGuid().ToString("N"));

        WriteJson(
            Path.Combine("content", "ontology", "concepts.json"),
            """
            {
              "concepts": [
                {
                  "id": "ontology_resource",
                  "name": { "zh": "资源", "en": "Resource" },
                  "description": { "zh": "本体资源" }
                }
              ]
            }
            """);

        WriteGameConcepts();
    }

    [Fact]
    public void AnnotateReferences_MapsGameAndOntologyReferences()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);
        var service = new RulesReferenceService(catalog, new RulesNameIndexService(catalog, content));

        var annotated = service.AnnotateReferences(
            "使用 <tool> 和 <ontology::ontology_resource>，未知 <unknown_ref>；无引用。",
            "testgame");

        Assert.Equal(
            "使用 <tool>(工具) 和 <ontology::ontology_resource>(资源)，未知 <unknown_ref>；无引用。",
            annotated);
        Assert.Equal("没有引用", service.AnnotateReferences("没有引用", "testgame"));
    }

    [Fact]
    public void GetConceptsWithExpansion_ReturnsMatchedAndDirectRelated()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);
        var service = new RulesReferenceService(catalog, new RulesNameIndexService(catalog, content));

        var result = service.GetConceptsWithExpansion("testgame", "widget");

        Assert.Single(result.Matched);
        Assert.Equal("widget", GetId(result.Matched[0]));
        Assert.Equal(new[] { "tool", "ontology_resource" }, result.Related.Select(GetId));
        Assert.DoesNotContain("widget", result.Related.Select(GetId));
    }

    [Fact]
    public void GetConceptsWithExpansion_TruncatesAtMaxRelatedConcepts()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);
        var service = new RulesReferenceService(catalog, new RulesNameIndexService(catalog, content));

        var result = service.GetConceptsWithExpansion("testgame", "many_refs");

        Assert.Single(result.Matched);
        Assert.Equal(10, result.Related.Count);
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"extra_{i:D2}"),
            result.Related.Select(GetId));
        Assert.Contains("前 10 个", result.Note);
    }

    [Fact]
    public void ExpandRelated_LightMode_SkipsOntologyReferences()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);
        var service = new RulesReferenceService(catalog, new RulesNameIndexService(catalog, content));
        var matched = catalog.GetConcepts("testgame", "mixed_refs").ToList();

        var related = service.ExpandRelated("testgame", matched, light: true);

        Assert.Equal(new[] { "tool" }, related.Select(GetId));
    }

    [Fact]
    public void GetConceptsWithExpansion_MissingConcept_ReturnsEmpty()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);
        var service = new RulesReferenceService(catalog, new RulesNameIndexService(catalog, content));

        var result = service.GetConceptsWithExpansion("testgame", "missing_ref");

        Assert.Empty(result.Matched);
        Assert.Empty(result.Related);
    }

    [Fact]
    public void ExpandRelated_NonLightMode_IncludesOntologyReferences()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);
        var service = new RulesReferenceService(catalog, new RulesNameIndexService(catalog, content));
        var matched = catalog.GetConcepts("testgame", "mixed_refs").ToList();

        var related = service.ExpandRelated("testgame", matched, light: false);

        Assert.Equal(new[] { "tool", "ontology_resource" }, related.Select(GetId));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖真实断言结果。
        }
    }

    private static string GetId(JsonElement element)
        => element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";

    private void WriteGameConcepts()
    {
        var objects = new JsonArray
        {
            MakeConcept("tool", "工具"),
            MakeConcept("widget", "小装置", "使用 <tool> 和 <ontology::ontology_resource>，再 <tool>"),
            MakeConcept("mixed_refs", "混合引用", "<tool> 与 <ontology::ontology_resource>"),
            MakeConcept(
                "many_refs",
                "很多引用",
                "引用 " + string.Join(" ", Enumerable.Range(0, 12).Select(i => $"<extra_{i:D2}>")))
        };

        for (var i = 0; i < 12; i++)
            objects.Add(MakeConcept($"extra_{i:D2}", $"额外{i:D2}"));

        var root = new JsonObject
        {
            ["objects"] = objects,
            ["actions"] = new JsonArray(),
            ["triggers"] = new JsonArray(),
            ["conditions"] = new JsonArray()
        };

        WriteJson(
            Path.Combine("content", "games", "testgame", "concepts.json"),
            root.ToJsonString());
    }

    private static JsonObject MakeConcept(string id, string nameZh, string? refs = null)
    {
        var concept = new JsonObject
        {
            ["id"] = id,
            ["name"] = new JsonObject
            {
                ["zh"] = nameZh,
                ["en"] = id
            }
        };

        if (refs != null)
        {
            concept["description"] = new JsonObject
            {
                ["zh"] = refs
            };
        }

        return concept;
    }

    private void WriteJson(string relativePath, string json)
    {
        var path = Path.Combine(_root, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, json);
    }
}
