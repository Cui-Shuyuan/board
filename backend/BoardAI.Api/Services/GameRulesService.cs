using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService : IDisposable
{
    private readonly string _basePath;

    private readonly VectorSearchService? _vectorSearch;

    private readonly RulesDocumentStore _documentStore;


    /// <summary>概念引用正则——注解（AnnotateReferences）与一层扩展（GetConceptsWithExpansion）共用。</summary>
    private static readonly Regex ConceptRefRegex = new(
        @"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>",
        RegexOptions.Compiled);


    /// <summary>不转义 &lt;&gt; 的序列化选项——引用提取必须看到字面 &lt;concept_id&gt;（与工具返回的 ToolResultOptions 同理）。</summary>
    private static readonly JsonSerializerOptions RelaxedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };


    private static readonly string[] ConceptArrayTypes = { "objects", "actions", "triggers", "conditions" };

    private static readonly string[] InstanceArrayTypes = { "effects", "modules", "cards", "continent_tiles", "sites", "chips" };


    public GameRulesService(IOptions<RulesOptions> options, VectorSearchService? vectorSearch = null)
    {
        // Rules:BasePath is an optional override; when empty, resolve the
        // repository root with the same portable logic used by the API host.
        _basePath = BoardPaths.ResolveBasePath(options.Value.BasePath);
        _vectorSearch = vectorSearch;
        _documentStore = new RulesDocumentStore(ClearDerivedCaches);
    }


    public void Dispose() => _documentStore.Dispose();


    public IReadOnlyList<string> GetGames()
    {
        var gamesDir = Path.Combine(_basePath, "content", "games");
        if (!Directory.Exists(gamesDir)) return Array.Empty<string>();
        return Directory.GetDirectories(gamesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList()!;
    }


    public IReadOnlyList<string> GetConceptTypes(string game)
    {
        var types = new List<string> { "ontology" };
        var concepts = LoadGameConcepts(game);
        if (concepts != null)
        {
            foreach (var type in ConceptArrayTypes)
            {
                if (concepts.RootElement.TryGetProperty(type, out _))
                    types.Add(type);
            }
            types.Add("top_level_refs");
            types.Add("flow");
        }
        var instances = LoadGameInstances(game);
        if (instances != null)
        {
            foreach (var type in InstanceArrayTypes)
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
            var ontology = LoadOntology();
            return ExtractArrayConcepts(ontology, "concepts");
        }

        if (type == "flow")
        {
            var flow = LoadGameFlow(game);
            return flow == null ? Array.Empty<ConceptSummary>() : ExtractFlowConcepts(flow);
        }

        var concepts = LoadGameConcepts(game);
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

        if (ConceptArrayTypes.Contains(type))
        {
            return ExtractArrayConcepts(concepts, type);
        }

        if (InstanceArrayTypes.Contains(type))
        {
            var instances = LoadGameInstances(game);
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
            var inst = LoadGameInstances(game);
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
        var (ns, localId) = ParseNamespace(id);

        // Search ontology when no namespace or explicitly "ontology"
        if (string.IsNullOrEmpty(ns) || ns == "ontology")
        {
            var ontology = LoadOntology();
            if (TryFindInArray(ontology.RootElement, "concepts", localId, out var ontologyConcept))
                results.Add(ontologyConcept);
        }

        // Search game concepts when no namespace
        if (string.IsNullOrEmpty(ns))
        {
            var concepts = LoadGameConcepts(game);
            if (concepts != null)
            {
                foreach (var type in ConceptArrayTypes)
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
            var flow = LoadGameFlow(game);
            if (flow != null)
            {
                if (TryFindFlowProcedure(flow.RootElement, localId, out var procedure))
                    results.Add(procedure);
            }

            // Search instances (effects, modules, cards, tiles, sites, chips)
            var instances = LoadGameInstances(game);
            if (instances != null)
            {
                foreach (var type in InstanceArrayTypes)
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


    private JsonDocument LoadOntology()
    {
        var path = Path.Combine(_basePath, "content", "ontology", "concepts.json");
        return LoadJson(path);
    }


    private JsonDocument? LoadGameConcepts(string game)
    {
        var path = Path.Combine(_basePath, "content", "games", game, "concepts.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }


    private JsonDocument? LoadOntologyFlow()
    {
        var path = Path.Combine(_basePath, "content", "ontology", "flow.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }


    private JsonDocument? LoadGameFlow(string game)
    {
        var path = Path.Combine(_basePath, "content", "games", game, "flow.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }


    private JsonDocument? LoadGameInstances(string game)
    {
        var path = Path.Combine(_basePath, "content", "games", game, "instances.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }


    private static (string? ns, string localId) ParseNamespace(string id)
    {
        // Strip surrounding angle brackets if any, e.g. "<ontology::resource>" -> "ontology::resource"
        var trimmed = id.Trim('<', '>');
        var parts = trimmed.Split(new[] { "::" }, StringSplitOptions.None);
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
        {
            return (parts[0], parts[1]);
        }
        return (null, trimmed);
    }


    private static IEnumerable<string> GetTopLevelRefKeys(JsonDocument concepts)
    {
        var results = new List<string>();
        foreach (var property in concepts.RootElement.EnumerateObject())
        {
            if (ConceptArrayTypes.Contains(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            results.Add(property.Name);
        }
        return results;
    }


    private JsonDocument LoadJson(string path)
    {
        return _documentStore.GetDocument(path);
    }


    /// <summary>
    /// 任意被缓存规则文档变化时清空派生缓存，下次访问会从新文档重建。
    /// 这里不区分 game：保持简单，且避免遗漏任何依赖规则 JSON 的缓存。
    /// </summary>
    private void ClearDerivedCaches()
    {
        _nameMaps.Clear();
        _exactLookups.Clear();
        _conceptTypeMaps.Clear();
        _flowPositions = null;
        _scoreTableGame = null;
        _scoreTable = null;
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
                Description = ExtractDescriptionZh(item),
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


    private static string? ExtractDescriptionZh(JsonElement element)
    {
        if (element.TryGetProperty("description", out var desc) &&
            desc.TryGetProperty("zh", out var zh))
        {
            return zh.GetString();
        }
        return null;
    }
}
