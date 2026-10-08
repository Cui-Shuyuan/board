using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesIndexServiceTests : IDisposable
{
    private readonly string _root;

    public RulesIndexServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-index-tests",
            Guid.NewGuid().ToString("N"));

        WriteOntologyConcepts();
        WriteOntologyFlow();
        WriteGameConcepts();
        WriteGameInstances();
        WriteGameFlow();
    }

    [Fact]
    public void GetIndexItems_ExtractsOntologyGameConceptsInstancesAndTopLevelRefs()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var items = service.GetIndexItems("testgame");

        var ontology = Assert.Single(items, i => i.Type == "ontology" && i.ConceptId == "ontology_widget");
        Assert.Equal("本体小装置", ontology.NameZh);
        Assert.Equal("Ontology Widget", ontology.NameEn);
        Assert.Equal("ontology", ontology.Source);
        Assert.Equal("本体小装置", ontology.NameText);

        var objects = Assert.Single(items, i => i.Type == "objects" && i.ConceptId == "widget");
        Assert.Equal("小装置", objects.NameZh);
        Assert.Equal("Widget", objects.NameEn);
        Assert.Equal("game", objects.Source);
        Assert.Equal("小装置", objects.NameText);

        var actions = Assert.Single(items, i => i.Type == "actions" && i.ConceptId == "use_widget");
        Assert.Equal("使用小装置", actions.NameZh);

        var effects = Assert.Single(items, i => i.Type == "effects" && i.ConceptId == "bonus_effect");
        Assert.Equal("奖励效果", effects.NameZh);
        Assert.Equal("Bonus Effect", effects.NameEn);
        Assert.Equal("instances", effects.Source);
        Assert.Equal("奖励效果", effects.NameText);

        var topLevelRef = Assert.Single(items, i => i.Type == "top_level_ref" && i.ConceptId == "turn_structure");
        Assert.Equal("回合结构", topLevelRef.NameZh);
        Assert.Equal("Turn Structure", topLevelRef.NameEn);

        var ontologyFlow = Assert.Single(items, i => i.Type == "flow" && i.ConceptId == "ontology_pipeline_node");
        Assert.Equal("本体管道节点", ontologyFlow.NameZh);
        Assert.Equal("ontology_flow", ontologyFlow.Source);
        Assert.Equal("本体管道节点", ontologyFlow.NameText);
    }

    [Fact]
    public void GetIndexItems_PreservesSourceOrder_OntologyConceptsInstancesTopLevelRefsThenFlows()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var items = service.GetIndexItems("testgame").ToList();

        var ontologyIndex = items.FindIndex(i => i.Type == "ontology" && i.ConceptId == "ontology_widget");
        var objectsIndex = items.FindIndex(i => i.Type == "objects" && i.ConceptId == "widget");
        var actionsIndex = items.FindIndex(i => i.Type == "actions" && i.ConceptId == "use_widget");
        var effectsIndex = items.FindIndex(i => i.Type == "effects" && i.ConceptId == "bonus_effect");
        var topLevelRefIndex = items.FindIndex(i => i.Type == "top_level_ref" && i.ConceptId == "turn_structure");
        var ontologyFlowIndex = items.FindIndex(i => i.Type == "flow" && i.ConceptId == "ontology_pipeline_node");
        var gameFlowIndex = items.FindIndex(i => i.Type == "flow" && i.ConceptId == "main_proc");

        Assert.True(ontologyIndex >= 0);
        Assert.True(objectsIndex > ontologyIndex);
        Assert.True(actionsIndex > objectsIndex);
        Assert.True(effectsIndex > actionsIndex);
        Assert.True(topLevelRefIndex > effectsIndex);
        Assert.True(ontologyFlowIndex > topLevelRefIndex);
        Assert.True(gameFlowIndex > ontologyFlowIndex);
    }

    [Fact]
    public void GetIndexItems_IndexesOnlyStandaloneFlowNodes_RecursesOptions_AndIndexesOntologyPipelineOptions()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var flowItems = service.GetIndexItems("testgame")
            .Where(i => i.Type == "flow")
            .ToList();

        var mainFlow = Assert.Single(flowItems, i => i.ConceptId == "main_proc");
        Assert.Equal("game_flow", mainFlow.Source);
        Assert.Contains(flowItems, i => i.ConceptId == "main_proc");
        Assert.Contains(flowItems, i => i.ConceptId == "nested_standalone");
        Assert.Contains(flowItems, i => i.ConceptId == "trigger_ext");
        Assert.Contains(flowItems, i => i.ConceptId == "ontology_pipeline_node");

        Assert.DoesNotContain(flowItems, i => i.ConceptId == "local_step");
        Assert.DoesNotContain(flowItems, i => i.ConceptId == "local_nested");
        Assert.DoesNotContain(flowItems, i => i.ConceptId == "game");
    }

    [Fact]
    public void GetIndexItems_StripsConceptReferencesFromSearchTexts()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var items = service.GetIndexItems("testgame");

        var widget = Assert.Single(items, i => i.Type == "objects" && i.ConceptId == "widget");
        Assert.DoesNotContain("<ontology::resource>", widget.SearchText);
        Assert.Contains("使用", widget.SearchText);
        Assert.Contains("资源", widget.SearchText);

        var mainFlow = Assert.Single(items, i => i.Type == "flow" && i.ConceptId == "main_proc");
        Assert.DoesNotContain("<ontology::pipeline>", mainFlow.SearchText);
        Assert.DoesNotContain("<", mainFlow.SearchText);
    }

    [Fact]
    public void GetIndexItems_ExtractsBareSlotKeys_SkipsConceptRefKeys_AndCollectsDeepText()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var items = service.GetIndexItems("testgame");

        var slot = Assert.Single(items, i => i.Type == "slot" && i.ConceptId == "population");
        Assert.Equal("widget.population", slot.Path);
        Assert.Equal("widget", slot.OwnerPath);
        Assert.Equal("人口", slot.NameZh);
        Assert.Equal("Population", slot.NameEn);
        Assert.Equal("人口", slot.NameText);
        Assert.Contains("population", slot.SearchText);
        Assert.Contains("人口", slot.SearchText);
        Assert.Contains("深层效果描述", slot.SearchText);

        Assert.DoesNotContain(items, i => i.Type == "slot" && i.ConceptId.StartsWith("<"));
        Assert.DoesNotContain(items, i => i.Type == "slot" && i.ConceptId == "condition_ref");
    }

    [Fact]
    public void GetIndexItems_DistinguishesLocalSlotPathFromGlobalConcept_AndSkipsExplicitSlotMetadata()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var items = service.GetIndexItems("testgame");

        var global = Assert.Single(items, i => i.Type == "objects" && i.ConceptId == "stage_partition");
        Assert.Equal("stage_partition", global.Path);
        Assert.Equal("全局层级分区", global.NameZh);

        var local = Assert.Single(items, i => i.Type == "slot" && i.Path == "widget.stage_partition");
        Assert.Equal("stage_partition", local.ConceptId);
        Assert.Equal("widget", local.OwnerPath);
        Assert.Contains("本地阶段分区槽位", local.SearchText);

        Assert.NotEqual(
            IndexContract.ComputePointId("testgame", "game", global.Path!, false),
            IndexContract.ComputePointId("testgame", "game", local.Path!, false));

        var named = Assert.Single(items, i => i.Type == "slot" && i.ConceptId == "named_slot");
        Assert.Equal("widget.named_slot", named.Path);
        Assert.Equal("widget", named.OwnerPath);
        Assert.Equal("命名槽位", named.NameZh);
        Assert.Equal("Named Slot", named.NameEn);
        Assert.Equal("命名槽位", named.NameText);
        Assert.Contains("木头", named.SearchText);

        // 显式 slot 的 id/name/material 是元数据，不得被当成三个槽位键。
        Assert.DoesNotContain(items, i => i.Type == "slot"
            && (i.ConceptId == "id" || i.ConceptId == "name" || i.ConceptId == "material"));
    }

    [Fact]
    public async Task BuildEmbeddingIndexAsync_WithNullVectorSearch_CompletesWithoutThrowing()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var result = await service.BuildEmbeddingIndexAsync("testgame");

        Assert.Null(result);
        Assert.True(service.GetIndexItems("testgame").Count > 0);
    }

    [Fact]
    public void GetIndexItems_UsesGameSpecificDefinition_WhenIdCollidesWithOntology()
    {
        WriteJson(
            Path.Combine("content", "games", "collisiongame", "concepts.json"),
            """
            {
              "objects": [
                {
                  "id": "ontology_widget",
                  "name": { "zh": "游戏层小装置", "en": "Game Widget" },
                  "description": { "zh": "游戏层专属定义", "en": "Game-specific definition" }
                }
              ],
              "actions": [], "triggers": [], "conditions": []
            }
            """);

        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var item = Assert.Single(service.GetIndexItems("collisiongame"),
            i => i.Type == "objects" && i.ConceptId == "ontology_widget");

        Assert.Equal("game", item.Source);
        Assert.Contains("游戏层专属定义", item.SearchText);
        Assert.DoesNotContain("本体描述", item.SearchText);
    }

    [Fact]
    public void GetIndexItems_MissingGameFiles_StillReturnsOntologyItems()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesIndexService(new RulesConceptCatalog(content), content, null);

        var items = service.GetIndexItems("missing_game");

        Assert.Contains(items, i => i.Type == "ontology" && i.ConceptId == "ontology_widget");
        Assert.DoesNotContain(items, i => i.Type == "objects");
        Assert.DoesNotContain(items, i => i.Type == "flow" && i.ConceptId == "main_proc");
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

    private void WriteOntologyConcepts()
    {
        WriteJson(
            Path.Combine("content", "ontology", "concepts.json"),
            """
            {
              "concepts": [
                {
                  "id": "ontology_widget",
                  "name": { "zh": "本体小装置", "en": "Ontology Widget" },
                  "description": { "zh": "本体描述 <ontology::resource>", "en": "Ontology description" }
                }
              ]
            }
            """);
    }

    private void WriteOntologyFlow()
    {
        WriteJson(
            Path.Combine("content", "ontology", "flow.json"),
            """
            {
              "pipeline": {
                "options": [
                  {
                    "id": "ontology_pipeline_node",
                    "specifies": { "id": "base_pipeline" },
                    "type": "PIPELINE",
                    "name": { "zh": "本体管道节点", "en": "Ontology Pipeline Node" },
                    "description": { "zh": "引用 <ontology::resource> 的管道", "en": "Pipeline ref" }
                  }
                ]
              }
            }
            """);
    }

    private void WriteGameConcepts()
    {
        WriteJson(
            Path.Combine("content", "games", "testgame", "concepts.json"),
            """
            {
              "objects": [
                {
                  "id": "widget",
                  "name": { "zh": "小装置", "en": "Widget" },
                  "description": { "zh": "使用 <ontology::resource> 资源", "en": "Uses resource" },
                  "slots": [
                    {
                      "population": {
                        "id": "population",
                        "name": { "zh": "人口", "en": "Population" },
                        "description": { "zh": "槽位描述", "en": "Slot description" },
                        "effect": {
                          "id": "pop_effect",
                          "name": { "zh": "效果", "en": "Effect" },
                          "description": { "zh": "深层效果描述", "en": "Deep effect" }
                        }
                      }
                    },
                    {
                      "<ontology::condition>": {
                        "id": "condition_ref",
                        "name": { "zh": "跳过条件" }
                      }
                    },
                    {
                      "stage_partition": {
                        "<ontology::effect>": {
                          "description": { "zh": "本地阶段分区槽位", "en": "Local stage partition slot" }
                        }
                      }
                    },
                    {
                      "id": "named_slot",
                      "name": { "zh": "命名槽位", "en": "Named Slot" },
                      "material": { "zh": "木头", "en": "Wood" }
                    }
                  ]
                },
                {
                  "id": "stage_partition",
                  "name": { "zh": "全局层级分区", "en": "Stage Partition" },
                  "description": { "zh": "全局对象", "en": "Global object" }
                }
              ],
              "actions": [
                {
                  "id": "use_widget",
                  "name": { "zh": "使用小装置", "en": "Use Widget" },
                  "description": { "zh": "动作描述", "en": "Action description" }
                }
              ],
              "triggers": [],
              "conditions": [],
              "turn_structure": {
                "id": "turn_structure",
                "name": { "zh": "回合结构", "en": "Turn Structure" },
                "description": { "zh": "顶层引用", "en": "Top-level ref" }
              }
            }
            """);
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
                  "description": { "zh": "实例描述", "en": "Instance description" }
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
                  "id": "main_proc",
                  "specifies": { "id": "base_proc" },
                  "type": "PROCEDURE",
                  "name": { "zh": "主流程", "en": "Main Procedure" },
                  "description": { "zh": "引用 <ontology::pipeline> 的流程", "en": "Procedure ref" },
                  "options": [
                    {
                      "id": "nested_standalone",
                      "extends": { "id": "base_nested" },
                      "type": "OPTION",
                      "name": { "zh": "嵌套独立", "en": "Nested Standalone" },
                      "description": { "zh": "嵌套描述 <ontology::effect>", "en": "Nested ref" }
                    },
                    {
                      "id": "local_nested",
                      "name": { "zh": "嵌套局部" }
                    },
                    "<ontology::resource>"
                  ]
                },
                {
                  "id": "local_step",
                  "name": { "zh": "局部步骤" }
                },
                {
                  "id": "game",
                  "specifies": { "id": "base_game" },
                  "name": { "zh": "游戏容器" }
                }
              ],
              "triggers": [
                {
                  "id": "trigger_ext",
                  "instance_of": { "id": "base_trigger" },
                  "type": "TRIGGER",
                  "name": { "zh": "触发器扩展", "en": "Trigger Ext" },
                  "description": { "zh": "触发器描述", "en": "Trigger description" }
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
