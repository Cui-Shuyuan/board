using System.Text.Json;

namespace BoardAI.Api.Services;

public sealed class RulesConceptCatalog : IRulesConceptCatalog
{
    private readonly RulesContentStore _content;

    public RulesConceptCatalog(RulesContentStore content)
    {
        _content = content;
    }

    public IReadOnlyList<string> GetConceptTypes(string game)
    {
        var types = new List<string> { "ontology" };
        var concepts = _content.LoadGameConcepts(game);
        if (concepts != null)
        {
            foreach (var type in RulesConceptTypes.ConceptArrayTypes)
            {
                if (concepts.RootElement.TryGetProperty(type, out _))
                    types.Add(type);
            }
            types.Add("top_level_refs");
            types.Add("flow");
        }
        var instances = _content.LoadGameInstances(game);
        if (instances != null)
        {
            foreach (var type in RulesConceptTypes.InstanceArrayTypes)
            {
                if (instances.RootElement.TryGetProperty(type, out var arr) && arr.GetArrayLength() > 0)
                    types.Add(type);
            }
        }
        return types;
    }


    public IReadOnlyList<ConceptSummary> ListConcepts(string game, string type)
    {
        if (type == "ontology")
        {
            var ontology = _content.LoadOntology();
            return ExtractArrayConcepts(ontology, "concepts");
        }

        if (type == "flow")
        {
            var flow = _content.LoadGameFlow(game);
            return flow == null ? Array.Empty<ConceptSummary>() : ExtractFlowConcepts(flow);
        }

        var concepts = _content.LoadGameConcepts(game);
        if (concepts == null) return Array.Empty<ConceptSummary>();

        if (type == "top_level_refs")
        {
            var results = new List<ConceptSummary>();
            foreach (var key in GetTopLevelRefKeys(concepts))
            {
                if (concepts.RootElement.TryGetProperty(key, out var element))
                {
                    results.Add(new ConceptSummary
                    {
                        Id = key,
                        Name = ExtractName(element),
                        Type = "top_level_ref"
                    });
                }
            }
            return results;
        }

        if (RulesConceptTypes.ConceptArrayTypes.Contains(type))
        {
            return ExtractArrayConcepts(concepts, type);
        }

        if (RulesConceptTypes.InstanceArrayTypes.Contains(type))
        {
            var instances = _content.LoadGameInstances(game);
            return instances == null ? Array.Empty<ConceptSummary>() : ExtractArrayConcepts(instances, type);
        }

        if (type == "slots")
        {
            var results = new List<ConceptSummary>();
            void CollectSlots(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    if (node.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var slot in slots.EnumerateArray())
                        {
                            if (slot.ValueKind != JsonValueKind.Object) continue;
                            foreach (var prop in slot.EnumerateObject())
                            {
                                if (prop.Name.StartsWith("<")) continue;
                                results.Add(new ConceptSummary { Id = prop.Name, Name = "", Type = "slot" });
                            }
                        }
                    }
                    foreach (var prop in node.EnumerateObject())
                        CollectSlots(prop.Value);
                }
                else if (node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in node.EnumerateArray())
                        CollectSlots(item);
                }
            }
            CollectSlots(concepts.RootElement);
            var inst = _content.LoadGameInstances(game);
            if (inst != null) CollectSlots(inst.RootElement);
            return results;
        }

        return Array.Empty<ConceptSummary>();
    }


    public IReadOnlyList<JsonElement> GetConcepts(string game, string id)
    {
        var results = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(id)) return results;

        // Parse namespace prefix if present, e.g. "ontology::resource"
        var (ns, localId) = RulesTextUtils.ParseNamespace(id);

        // Search ontology when no namespace or explicitly "ontology"
        if (string.IsNullOrEmpty(ns) || ns == "ontology")
        {
            var ontology = _content.LoadOntology();
            if (TryFindInArray(ontology.RootElement, "concepts", localId, out var ontologyConcept))
                results.Add(ontologyConcept);
        }

        // Search game concepts when no namespace
        if (string.IsNullOrEmpty(ns))
        {
            var concepts = _content.LoadGameConcepts(game);
            if (concepts != null)
            {
                foreach (var type in RulesConceptTypes.ConceptArrayTypes)
                {
                    if (TryFindInArray(concepts.RootElement, type, localId, out var concept))
                        results.Add(concept);
                }

                foreach (var key in GetTopLevelRefKeys(concepts))
                {
                    if (key == localId && concepts.RootElement.TryGetProperty(key, out var topLevel))
                        results.Add(topLevel);
                }
            }

            // Search flow
            var flow = _content.LoadGameFlow(game);
            if (flow != null)
            {
                if (TryFindFlowProcedure(flow.RootElement, localId, out var procedure))
                    results.Add(procedure);
            }

            // Search instances (effects, modules, cards, tiles, sites, chips)
            var instances = _content.LoadGameInstances(game);
            if (instances != null)
            {
                foreach (var type in RulesConceptTypes.InstanceArrayTypes)
                {
                    if (TryFindInArray(instances.RootElement, type, localId, out var instance))
                        results.Add(instance);
                }
            }

            // Search slots (bare-key slot names within concepts, e.g. population/expansion)
            if (concepts != null)
            {
                var slot = FindSlot(concepts.RootElement, localId);
                if (slot.HasValue)
                    results.Add(slot.Value);
            }
        }

        return results;
    }


    /// <summary>递归在 slots 数组中查找裸键槽位 (与 Python rebuild_index 的 slot 提取一致)</summary>
    private static JsonElement? FindSlot(JsonElement node, string slotId)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var slot in slots.EnumerateArray())
                {
                    if (slot.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in slot.EnumerateObject())
                    {
                        if (prop.Name == slotId)
                            return prop.Value;
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
            {
                var found = FindSlot(prop.Value, slotId);
                if (found.HasValue) return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var found = FindSlot(item, slotId);
                if (found.HasValue) return found;
            }
        }
        return null;
    }


    public JsonElement? GetConcept(string game, string id)
    {
        var concepts = GetConcepts(game, id);
        return concepts.Count > 0 ? concepts[0] : null;
    }


    public IReadOnlyList<JsonElement> GetActionConditions(string game, string actionId)
    {
        var action = GetConcept(game, actionId);
        if (!action.HasValue) return Array.Empty<JsonElement>();

        var result = new List<JsonElement>();

        if (action.Value.TryGetProperty("<trigger>", out var triggerRef))
        {
            JsonElement triggerElement;
            if (triggerRef.ValueKind == JsonValueKind.String)
            {
                var triggerId = triggerRef.GetString()?.Trim('<', '>');
                if (string.IsNullOrEmpty(triggerId)) return result;
                var trigger = GetConcept(game, triggerId);
                if (!trigger.HasValue) return result;
                triggerElement = trigger.Value;
            }
            else if (triggerRef.ValueKind == JsonValueKind.Object)
            {
                triggerElement = triggerRef;
            }
            else
            {
                return result;
            }

            if (triggerElement.TryGetProperty("<condition>", out var triggerCondition))
            {
                var conditionId = triggerCondition.GetString()?.Trim('<', '>');
                if (!string.IsNullOrEmpty(conditionId))
                {
                    var condition = GetConcept(game, conditionId);
                    if (condition.HasValue)
                        result.Add(condition.Value);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> GetTopLevelRefKeys(JsonDocument concepts)
    {
        var results = new List<string>();
        foreach (var property in concepts.RootElement.EnumerateObject())
        {
            if (RulesConceptTypes.ConceptArrayTypes.Contains(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            results.Add(property.Name);
        }
        return results;
    }

    private static IReadOnlyList<ConceptSummary> ExtractArrayConcepts(JsonDocument doc, string propertyName)
    {
        var results = new List<ConceptSummary>();
        if (!doc.RootElement.TryGetProperty(propertyName, out var array)) return results;
        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? string.Empty;
            var summary = new ConceptSummary
            {
                Id = id,
                Name = ExtractName(item),
                Type = propertyName,
                Description = RulesTextUtils.ExtractDescriptionZh(item),
                Media = ExtractMedia(item)
            };
            results.Add(summary);
        }
        return results;
    }


    private static Dictionary<string, JsonElement>? ExtractMedia(JsonElement item)
    {
        if (!item.TryGetProperty("media", out var mediaElement) || mediaElement.ValueKind != JsonValueKind.Object)
            return null;

        var media = new Dictionary<string, JsonElement>();
        foreach (var property in mediaElement.EnumerateObject())
        {
            // 支持 string 或 string[]，其他类型忽略
            if (property.Value.ValueKind == JsonValueKind.String ||
                property.Value.ValueKind == JsonValueKind.Array)
            {
                media[property.Name] = property.Value.Clone();
            }
        }
        return media.Count > 0 ? media : null;
    }


    private static IReadOnlyList<ConceptSummary> ExtractFlowConcepts(JsonDocument flow)
    {
        var results = new List<ConceptSummary>();
        var seen = new HashSet<string>();
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!flow.RootElement.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                WalkFlowForSummaries(item, results, seen);
            }
        }
        return results;
    }


    private static void WalkFlowForSummaries(JsonElement node, List<ConceptSummary> results, HashSet<string> seen)
    {
        // 只汇总独立概念节点（有 id 且有 specifies/extends/instance_of）；
        // 匿名容器（pipeline 等）不汇总但继续下钻
        // (2026-08-13: 无 id 直接 return 曾导致嵌套在 pipeline 中的流程节点
        //  从 list_concept_ids 缺失——与 rebuild_index 同源修复)
        if (node.TryGetProperty("id", out var idProp))
        {
            var id = idProp.GetString() ?? string.Empty;
            // game 等通用容器不进关键词搜索与目录（整体流程走 get_game_flow 工具）
            if (!string.IsNullOrEmpty(id) && id != "game" && IsStandaloneFlowNode(node) && seen.Add(id))
            {
                results.Add(new ConceptSummary
                {
                    Id = id,
                    Name = ExtractName(node),
                    Type = "flow"
                });
            }
        }

        // 递归无条件下钻：events、options、容器字段
        foreach (var arrayKey in new[] { "events", "options" })
        {
            if (!node.TryGetProperty(arrayKey, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    WalkFlowForSummaries(item, results, seen);
            }
        }
        foreach (var containerKey in new[] {
            "<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
            "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
            "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
            "<ontology::instant_content>", "<ontology::instant_cost>",
            "<ontology::continuous_effect>", "<ontology::effect>" })
        {
            if (node.TryGetProperty(containerKey, out var container) && container.ValueKind == JsonValueKind.Object)
                WalkFlowForSummaries(container, results, seen);
        }
    }


    private static bool TryFindInArray(JsonElement root, string arrayProperty, string id, out JsonElement found)
    {
        found = default;
        if (!root.TryGetProperty(arrayProperty, out var array)) return false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idProp) && idProp.GetString() == id)
            {
                found = item;
                return true;
            }
        }
        return false;
    }


    private static bool TryFindFlowProcedure(JsonElement root, string id, out JsonElement found)
    {
        found = default;

        // search both procedures and triggers at top level, then recurse
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!root.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (TryFindFlowNodeRecursive(item, id, out found))
                    return true;
            }
        }

        return false;
    }


    private static bool TryFindFlowNodeRecursive(JsonElement node, string id, out JsonElement found)
    {
        found = default;
        if (node.TryGetProperty("id", out var idProp) && idProp.GetString() == id)
        {
            found = node;
            return true;
        }

        // recurse into events, options, and content containers
        foreach (var arrayKey in new[] { "events", "options" })
        {
            if (!node.TryGetProperty(arrayKey, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (TryFindFlowNodeRecursive(item, id, out found))
                    return true;
            }
        }

        // recurse into container fields: pipelines, procedures, content, cost, condition, effect
        // (2026-08-13: 补 <ontology::pipeline> 等 procedure 持有键——此前嵌套在 pipeline
        //  中的流程节点（如 event_era_scoring）get_concept 查不到，导致 LLM 反复搜索)
        foreach (var containerKey in new[] {
            "<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
            "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
            "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
            "<ontology::instant_content>", "<ontology::instant_cost>",
            "<ontology::continuous_effect>", "<ontology::effect>" })
        {
            if (node.TryGetProperty(containerKey, out var container) && container.ValueKind == JsonValueKind.Object)
            {
                if (TryFindFlowNodeRecursive(container, id, out found))
                    return true;
            }
        }

        return false;
    }


    private static string ExtractName(JsonElement element)
    {
        if (element.TryGetProperty("name", out var name) &&
            name.TryGetProperty("zh", out var zh))
        {
            return zh.GetString() ?? string.Empty;
        }
        if (element.TryGetProperty("id", out var id))
        {
            return id.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// 独立流程概念节点判据（与 GameRulesService.Index 的同名逻辑一致）：
    /// 带层级关系字段的节点才进入 flow 目录，避免 pipeline 局部步骤成为目录噪音。
    /// </summary>
    private static bool IsStandaloneFlowNode(JsonElement node) =>
        node.TryGetProperty("specifies", out _)
        || node.TryGetProperty("extends", out _)
        || node.TryGetProperty("instance_of", out _);
}
