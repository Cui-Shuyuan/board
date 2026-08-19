using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

public partial class GameRulesService
{
    private JsonDocument LoadOntology()
    {
        var path = Path.Combine(_basePath, "ontology", "concepts.json");
        return LoadJson(path);
    }

    private JsonDocument? LoadGameConcepts(string game)
    {
        var path = Path.Combine(_basePath, "games", game, "concepts.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private JsonDocument? LoadOntologyFlow()
    {
        var path = Path.Combine(_basePath, "ontology", "flow.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private JsonDocument? LoadGameFlow(string game)
    {
        var path = Path.Combine(_basePath, "games", game, "flow.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private JsonDocument? LoadGameInstances(string game)
    {
        var path = Path.Combine(_basePath, "games", game, "instances.json");
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
        if (_loadedFiles.TryGetValue(path, out var doc)) return doc;
        var json = File.ReadAllText(path);
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        _loadedFiles[path] = document;
        return document;
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
            name.ValueKind == JsonValueKind.Object &&
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
            desc.ValueKind == JsonValueKind.Object &&
            desc.TryGetProperty("zh", out var zh))
        {
            return zh.GetString();
        }
        return null;
    }

    private static IEnumerable<string> Tokenize(string query)
    {
        return query.Split(
            new[] { ' ', '\t', '\n', '\r', '，', '。', '、', '？', '！', '；', '：', '"', '"', '+', '-', '_', '.', '/', '<', '>' },
            StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct();
    }

    private static bool MatchesSummary(ConceptSummary summary, string term)
    {
        return summary.Id.Contains(term, StringComparison.InvariantCultureIgnoreCase) ||
               summary.Name.Contains(term, StringComparison.InvariantCultureIgnoreCase);
    }

    private static bool ContainsTerm(JsonElement element, string term)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Contains(term, StringComparison.InvariantCultureIgnoreCase) ?? false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsTerm(property.Value, term)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsTerm(item, term)) return true;
                }
                break;
        }
        return false;
    }
}
