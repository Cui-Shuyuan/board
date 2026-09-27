using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesNameIndexServiceTests : IDisposable
{
    private readonly string _root;

    public RulesNameIndexServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-name-index-tests",
            Guid.NewGuid().ToString("N"));

        WriteJson(
            Path.Combine("content", "ontology", "concepts.json"),
            """
            {
              "concepts": [
                {
                  "id": "ontology_widget",
                  "name": { "zh": "本体小装置", "en": "Ontology Widget" }
                },
                {
                  "id": "ontology_en_only",
                  "name": { "en": "Only English" }
                }
              ]
            }
            """);

        WriteGameConcepts("小装置");
        WriteGameInstances();
        WriteGameFlow();
    }

    [Fact]
    public void GetNameMap_IncludesGameAndNestedFlowNames_AndSkipsIdEqualsName()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var map = service.GetNameMap("testgame");

        Assert.Equal("小装置", map["widget"]);
        Assert.Equal("使用小装置", map["use_widget"]);
        Assert.Equal("嵌套流程", map["nested_flow"]);
        Assert.False(map.ContainsKey("same_name"));
    }

    [Fact]
    public void GetExactLookup_IndexesZhEnAliases_AndKinds()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var lookup = service.GetExactLookup("testgame");

        var zh = Assert.Single(lookup["小装置"]);
        Assert.Equal("widget", zh.Id);
        Assert.Equal("exact_name_zh", zh.Kind);

        var en = Assert.Single(lookup["Widget"]);
        Assert.Equal("widget", en.Id);
        Assert.Equal("exact_name_en", en.Kind);

        var alias = Assert.Single(lookup["小东西"]);
        Assert.Equal("widget", alias.Id);
        Assert.Equal("exact_alias", alias.Kind);

        var flow = Assert.Single(lookup["嵌套流程"]);
        Assert.Equal("nested_flow", flow.Id);
        Assert.Equal("exact_name_zh", flow.Kind);

        var flowAlias = Assert.Single(lookup["嵌套别名"]);
        Assert.Equal("nested_flow", flowAlias.Id);
        Assert.Equal("exact_alias", flowAlias.Kind);

        var instance = Assert.Single(lookup["奖励效果"]);
        Assert.Equal("bonus_effect", instance.Id);
        Assert.Equal("exact_name_zh", instance.Kind);
    }

    [Fact]
    public void GetExactLookup_BaseNames_KeepAllIdsInStableOrder_WithoutDuplicates()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var lookup = service.GetExactLookup("testgame");

        var hits = lookup["家庭成长"];
        Assert.Equal(2, hits.Count);
        Assert.Equal("family_grow_need_room", hits[0].Id);
        Assert.Equal("family_grow_no_room", hits[1].Id);
        Assert.All(hits, h => Assert.Equal("exact_base", h.Kind));
    }

    [Fact]
    public void GetExactLookup_DeduplicatesSameIdUnderSameKey()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var lookup = service.GetExactLookup("testgame");

        var hit = Assert.Single(lookup["重复名"]);
        Assert.Equal("repeat_id", hit.Id);
        Assert.Equal("exact_name_zh", hit.Kind);
    }

    [Fact]
    public void GetExactLookup_IsCaseInsensitive()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var lookup = service.GetExactLookup("testgame");

        Assert.True(lookup.TryGetValue("WIDGET", out var hits));
        Assert.Contains(hits, h => h.Id == "widget");
    }

    [Fact]
    public void GetNameMap_CachesSameInstance_AndClearRebuilds()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var first = service.GetNameMap("testgame");
        var second = service.GetNameMap("testgame");
        Assert.Same(first, second);

        WriteGameConcepts("新小装置");
        service.Clear();

        var rebuilt = service.GetNameMap("testgame");
        Assert.NotSame(first, rebuilt);
        Assert.Equal("新小装置", rebuilt["widget"]);
    }

    [Fact]
    public void GetExactLookup_OntologyEnglishOnly_AndMissingOptionalFlow_DoesNotThrow()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesNameIndexService(new RulesConceptCatalog(content), content);

        var map = service.GetNameMap("no_game_files");
        var lookup = service.GetExactLookup("no_game_files");

        Assert.NotNull(map);
        Assert.True(lookup.TryGetValue("ONLY ENGLISH", out var hits));
        Assert.Contains(hits, h => h.Id == "ontology_en_only" && h.Kind == "exact_name_en");
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

    private void WriteGameConcepts(string widgetName)
    {
        var json =
            $$"""
            {
              "objects": [
                {
                  "id": "widget",
                  "name": { "zh": "{{widgetName}}", "en": "Widget" },
                  "aliases": { "zh": ["小东西"], "en": ["Gadget"] }
                },
                {
                  "id": "same_name",
                  "name": { "zh": "same_name", "en": "Same Name" }
                },
                {
                  "id": "repeat_id",
                  "name": { "zh": "重复名", "en": "Repeat" },
                  "aliases": { "zh": ["重复名"] }
                }
              ],
              "actions": [
                {
                  "id": "use_widget",
                  "name": { "zh": "使用小装置", "en": "Use Widget" }
                },
                {
                  "id": "family_grow_need_room",
                  "name": { "zh": "家庭成长（需空房间）", "en": "Family Grow With Room" }
                },
                {
                  "id": "family_grow_no_room",
                  "name": { "zh": "家庭成长（无需房间）", "en": "Family Grow Without Room" }
                }
              ],
              "triggers": [],
              "conditions": []
            }
            """;

        WriteJson(Path.Combine("content", "games", "testgame", "concepts.json"), json);
    }

    private void WriteGameInstances()
    {
        WriteJson(
            Path.Combine("content", "games", "testgame", "instances.json"),
            """
            {
              "effects": [
                {
                  "id": "bonus_effect",
                  "name": { "zh": "奖励效果", "en": "Bonus Effect" },
                  "aliases": { "zh": ["奖励"], "en": ["Bonus"] }
                }
              ]
            }
            """);
    }

    private void WriteGameFlow()
    {
        WriteJson(
            Path.Combine("content", "games", "testgame", "flow.json"),
            """
            {
              "procedures": [
                {
                  "id": "main_loop",
                  "type": "CHOOSE_ONE",
                  "options": [
                    {
                      "id": "nested_flow",
                      "specifies": { "id": "base_flow" },
                      "name": { "zh": "嵌套流程", "en": "Nested Flow" },
                      "aliases": { "zh": ["嵌套别名"], "en": ["Nested Alias"] }
                    },
                    { "id": "local_step", "name": { "zh": "局部步骤" } }
                  ]
                }
              ]
            }
            """);
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
