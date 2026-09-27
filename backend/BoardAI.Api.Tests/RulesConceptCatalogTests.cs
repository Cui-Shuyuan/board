using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesConceptCatalogTests : IDisposable
{
    private readonly string _root;

    public RulesConceptCatalogTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-concept-catalog-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(_root, "content", "ontology"));
        Directory.CreateDirectory(Path.Combine(_root, "content", "games", "testgame"));

        WriteJson(
            Path.Combine("content", "ontology", "concepts.json"),
            """
            {
              "concepts": [
                {
                  "id": "ontology_widget",
                  "name": { "zh": "本体小装置" }
                },
                {
                  "id": "shared",
                  "name": { "zh": "本体共享" },
                  "origin": "ontology"
                }
              ]
            }
            """);

        WriteJson(
            Path.Combine("content", "games", "testgame", "concepts.json"),
            """
            {
              "objects": [
                {
                  "id": "widget",
                  "name": { "zh": "小装置" },
                  "description": { "zh": "测试对象" }
                }
              ],
              "actions": [
                {
                  "id": "use_widget",
                  "name": { "zh": "使用小装置" },
                  "<trigger>": { "<condition>": "<condition_ready>" }
                }
              ],
              "triggers": [],
              "conditions": [
                {
                  "id": "condition_ready",
                  "name": { "zh": "条件就绪" }
                }
              ],
              "shared": {
                "id": "shared",
                "name": { "zh": "游戏共享" },
                "origin": "game"
              }
            }
            """);

        WriteJson(
            Path.Combine("content", "games", "testgame", "flow.json"),
            """
            {
              "procedures": [
                {
                  "id": "flow_widget",
                  "specifies": { "id": "base_flow" },
                  "name": { "zh": "流程小装置" }
                },
                {
                  "id": "main_loop",
                  "type": "CHOOSE_ONE",
                  "options": [
                    { "id": "flow_widget" }
                  ]
                }
              ]
            }
            """);
    }

    [Fact]
    public void GetConceptTypes_ReturnsExpectedOrder()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var types = catalog.GetConceptTypes("testgame");

        Assert.Equal(
            new[] { "ontology", "objects", "actions", "triggers", "conditions", "top_level_refs", "flow" },
            types);
    }

    [Fact]
    public void ListConcepts_ObjectsAndFlow_ReturnExpectedSummaries()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var objects = catalog.ListConcepts("testgame", "objects");
        var widget = Assert.Single(objects);
        Assert.Equal("widget", widget.Id);
        Assert.Equal("小装置", widget.Name);
        Assert.Equal("objects", widget.Type);
        Assert.Equal("测试对象", widget.Description);

        var flow = catalog.ListConcepts("testgame", "flow");
        var flowWidget = Assert.Single(flow);
        Assert.Equal("flow_widget", flowWidget.Id);
        Assert.Equal("流程小装置", flowWidget.Name);
        Assert.Equal("flow", flowWidget.Type);
    }

    [Fact]
    public void ListConcepts_UnknownType_ReturnsEmpty()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        Assert.Empty(catalog.ListConcepts("testgame", "unknown_type"));
    }

    [Fact]
    public void GetConcepts_WithGameConceptId_ReturnsWidget()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var concepts = catalog.GetConcepts("testgame", "widget");

        var widget = Assert.Single(concepts);
        Assert.Equal("widget", widget.GetProperty("id").GetString());
        Assert.Equal("测试对象", widget.GetProperty("description").GetProperty("zh").GetString());
    }

    [Fact]
    public void GetConcepts_WithOntologyNamespace_ReturnsOntologyConcept()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var concepts = catalog.GetConcepts("testgame", "ontology::ontology_widget");

        var concept = Assert.Single(concepts);
        Assert.Equal("ontology_widget", concept.GetProperty("id").GetString());
        Assert.Equal("本体小装置", concept.GetProperty("name").GetProperty("zh").GetString());
    }

    [Fact]
    public void GetConcepts_WithFlowNodeId_ReturnsFlowNode_AndMissingIdReturnsEmpty()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var flowNodes = catalog.GetConcepts("testgame", "flow_widget");

        var flowWidget = Assert.Single(flowNodes);
        Assert.Equal("flow_widget", flowWidget.GetProperty("id").GetString());
        Assert.Empty(catalog.GetConcepts("testgame", "missing_id"));
    }

    [Fact]
    public void GetConcept_ReturnsFirstResult_AndMissingIdReturnsNull()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var first = catalog.GetConcept("testgame", "shared");

        Assert.NotNull(first);
        Assert.Equal("ontology", first.Value.GetProperty("origin").GetString());
        Assert.Null(catalog.GetConcept("testgame", "missing_id"));
    }

    [Fact]
    public void GetActionConditions_WithInlineTrigger_ReturnsReferencedCondition()
    {
        using var content = new RulesContentStore(_root);
        var catalog = new RulesConceptCatalog(content);

        var conditions = catalog.GetActionConditions("testgame", "use_widget");

        var condition = Assert.Single(conditions);
        Assert.Equal("condition_ready", condition.GetProperty("id").GetString());
        Assert.Equal("条件就绪", condition.GetProperty("name").GetProperty("zh").GetString());
        Assert.Empty(catalog.GetActionConditions("testgame", "missing_action"));
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

    private void WriteJson(string relativePath, string json)
    {
        var path = Path.Combine(_root, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, json);
    }
}
