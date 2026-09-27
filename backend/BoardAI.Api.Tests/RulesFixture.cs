using System.Text.Json;
using System.Text.Json.Nodes;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class RulesFixture : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public RulesFixture()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "boardai-game-rules-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(Root, "content", "ontology"));
        Directory.CreateDirectory(Path.Combine(Root, "content", "games", "testgame"));

        WriteOntologyConcepts();
        WriteGameConcepts("小装置", "使用小装置");
        WriteGameFlow();
    }

    public string Root { get; }

    public string OntologyConceptsPath =>
        Path.Combine(Root, "content", "ontology", "concepts.json");

    public string GameConceptsPath =>
        Path.Combine(Root, "content", "games", "testgame", "concepts.json");

    public string GameFlowPath =>
        Path.Combine(Root, "content", "games", "testgame", "flow.json");

    public void WriteGameConcepts(string widgetName, string actionName)
    {
        var json = new JsonObject
        {
            ["objects"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "widget",
                    ["name"] = new JsonObject
                    {
                        ["zh"] = widgetName,
                        ["en"] = "Widget"
                    },
                    ["description"] = new JsonObject
                    {
                        ["zh"] = "测试对象"
                    }
                }
            },
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "use_widget",
                    ["name"] = new JsonObject
                    {
                        ["zh"] = actionName,
                        ["en"] = "Use Widget"
                    },
                    ["<ontology::condition>"] = new JsonObject { ["id"] = "widget_ready" },
                    ["<ontology::cost>"] = new JsonObject { ["id"] = "widget_cost" },
                    ["target"] = new JsonObject { ["id"] = "widget_target" },
                    ["<ontology::content>"] = new JsonObject { ["id"] = "widget_content" }
                }
            },
            ["triggers"] = new JsonArray(),
            ["conditions"] = new JsonArray()
        };

        File.WriteAllText(GameConceptsPath, json.ToJsonString(WriteOptions));
    }

    public GameRulesService CreateService()
    {
        return new GameRulesService(
            Options.Create(new RulesOptions { BasePath = Root }),
            vectorSearch: null);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖真实断言结果。
        }
    }

    private void WriteOntologyConcepts()
    {
        var json = new JsonObject
        {
            ["concepts"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ontology_widget",
                    ["name"] = new JsonObject
                    {
                        ["zh"] = "测试本体概念",
                        ["en"] = "Test Ontology Concept"
                    },
                    ["abstract"] = true,
                    ["definition"] = "用于后端测试。"
                }
            }
        };

        File.WriteAllText(OntologyConceptsPath, json.ToJsonString(WriteOptions));
    }

    private void WriteGameFlow()
    {
        var json = new JsonObject
        {
            ["procedures"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "main_loop",
                    ["options"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "use_widget" }
                    },
                    ["type"] = "CHOOSE_ONE"
                }
            }
        };

        File.WriteAllText(GameFlowPath, json.ToJsonString(WriteOptions));
    }
}
