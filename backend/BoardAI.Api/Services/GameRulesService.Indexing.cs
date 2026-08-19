using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

public partial class GameRulesService
{
    public IReadOnlyList<ConceptIndexItem> GetIndexItems(string game)
    {
        var result = new List<ConceptIndexItem>();

        // ontology 概念（不限定 game）
        foreach (var c in ListConcepts(game, "ontology"))
        {
            var detail = GetConcept(game, c.Id);
            result.Add(new ConceptIndexItem
            {
                ConceptId = c.Id,
                Type = "ontology",
                Source = "ontology",
                NameZh = c.Name,
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result, "ontology");
        }

        // ontology 顶层引用（如 meta），与 Python rebuild_index 的 extract_concepts 对齐
        var ontologyDoc = LoadOntology();
        foreach (var key in GetTopLevelRefKeys(ontologyDoc))
        {
            if (!ontologyDoc.RootElement.TryGetProperty(key, out var element)) continue;
            var summary = new ConceptSummary
            {
                Id = key,
                Name = ExtractName(element),
                Type = "top_level_ref"
            };
            JsonElement? detail = element;
            result.Add(new ConceptIndexItem
            {
                ConceptId = key,
                Type = "top_level_ref",
                Source = "ontology",
                NameZh = ExtractNameZh(element),
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(summary, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result, "ontology");
        }

        foreach (var type in ConceptArrayTypes)
        {
            foreach (var c in ListConcepts(game, type))
            {
                var detail = GetConcept(game, c.Id);
                result.Add(new ConceptIndexItem
                {
                    ConceptId = c.Id,
                    Type = type,
                    Source = "game",
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result, "game");
            }
        }

        // instances.json 实例
        foreach (var type in InstanceArrayTypes)
        {
            foreach (var c in ListConcepts(game, type))
            {
                var detail = GetConcept(game, c.Id);
                result.Add(new ConceptIndexItem
                {
                    ConceptId = c.Id,
                    Type = type,
                    Source = "instances",
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result, "instances");
            }
        }

        // 顶层引用
        var gameConcepts = LoadGameConcepts(game);
        foreach (var c in ListConcepts(game, "top_level_refs"))
        {
            JsonElement? detail = gameConcepts != null && gameConcepts.RootElement.TryGetProperty(c.Id, out var el)
                ? el
                : (JsonElement?)null;
            result.Add(new ConceptIndexItem
            {
                ConceptId = c.Id,
                Type = "top_level_ref",
                Source = "game",
                NameZh = detail.HasValue ? ExtractNameZh(detail.Value) : "",
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result, "game");
        }

        // ontology/flow.json
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null)
        {
            ExtractFlowItems(ontologyFlow.RootElement, result, "ontology_flow");
        }

        // flow.json 流程
        var flow = LoadGameFlow(game);
        if (flow != null)
        {
            ExtractFlowItems(flow.RootElement, result, "game_flow");
        }

        return result;
    }

    /// <summary>递归提取 slots 元素 (裸键槽名如 population/expansion 作为概念, 与 Python rebuild_index 一致)</summary>
    private static void ExtractSlots(JsonElement node, List<ConceptIndexItem> result, string source)
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
                        // 裸键 = 槽位名 (可索引); <概念> 键 = 已有定义的概念引用, 跳过
                        if (prop.Name.StartsWith("<") || prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var parts = new List<string>();
                        CollectIndexText(prop.Value, parts);
                        result.Add(new ConceptIndexItem
                        {
                            ConceptId = prop.Name,
                            Type = "slot",
                            Source = source,
                            NameZh = "",
                            NameEn = "",
                            SearchText = string.Join(" ", parts),
                        });
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
                ExtractSlots(prop.Value, result, source);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                ExtractSlots(item, result, source);
        }
    }

    /// <summary>递归收集 id/name/description 文本 (slots 深层效果描述)</summary>
    private static void CollectIndexText(JsonElement node, List<string> parts)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in node.EnumerateObject())
            {
                if (prop.Name == "id")
                    parts.Add(prop.Value.GetString() ?? "");
                else if (prop.Name == "name" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("zh", out var z)) parts.Add(z.GetString() ?? "");
                    if (prop.Value.TryGetProperty("en", out var e)) parts.Add(e.GetString() ?? "");
                }
                else if (prop.Name == "description" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("zh", out var z)) parts.Add(z.GetString() ?? "");
                    if (prop.Value.TryGetProperty("en", out var e)) parts.Add(e.GetString() ?? "");
                }
                else
                {
                    CollectIndexText(prop.Value, parts);
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                CollectIndexText(item, parts);
        }
    }

    private static void ExtractFlowItems(JsonElement root, List<ConceptIndexItem> result, string source)
    {
        // 游戏 flow.json：procedures 树 + triggers 组
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!root.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                WalkFlowNode(item, result, source);
            }
        }

        // ontology flow.json：pipeline.options 数组
        if (root.TryGetProperty("pipeline", out var pipeline) &&
            pipeline.TryGetProperty("options", out var options))
        {
            foreach (var opt in options.EnumerateArray())
            {
                WalkFlowNode(opt, result, source);
            }
        }
    }

    /// <summary>
    /// 独立概念节点判据：带层级关系字段（specifies/extends/instance_of）的节点才是
    /// 可被搜索的概念；仅 id+name 的节点是 pipeline 局部步骤（do_after 引用名），
    /// 不入搜索索引与目录（get_concept 按 id 仍可查到）。
    /// </summary>
    private static bool IsStandaloneFlowNode(JsonElement node) =>
        node.TryGetProperty("specifies", out _)
        || node.TryGetProperty("extends", out _)
        || node.TryGetProperty("instance_of", out _);

    private static void WalkFlowNode(JsonElement node, List<ConceptIndexItem> result, string source)
    {
        // flow 的 options/events 里允许出现裸字符串（例如 "<take_hex_tile>" 或局部步骤名），
        // 它们不是可索引节点，直接跳过。
        if (node.ValueKind != JsonValueKind.Object) return;

        var id = node.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        // 只索引独立概念节点（有 id 且有 specifies/extends/instance_of）：
        // 局部步骤（仅 id+name，do_after 引用用）不入索引——短名短描述是向量噪音，
        // 且同 id 跨位置重复互相覆盖；步骤信息随父概念的 get_concept 完整返回。
        // game 等通用容器概念（各游戏共有的顶层流程宿主）也不入索引。
        if (!string.IsNullOrEmpty(id) && id != "game" && IsStandaloneFlowNode(node))
        {
            var zhParts = new List<string> { id };

            var nodeType = node.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(nodeType))
                zhParts.Add(nodeType);

            if (node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object && name.TryGetProperty("zh", out var nzh))
                zhParts.Add(nzh.GetString()!);
            if (node.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.Object && desc.TryGetProperty("zh", out var dzh))
                zhParts.Add(dzh.GetString()!);

            result.Add(new ConceptIndexItem
            {
                ConceptId = id,
                Type = "flow",
                Source = source,
                NameZh = node.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.Object && nm.TryGetProperty("zh", out var nz)
                    ? nz.GetString() : id,
                NameEn = node.TryGetProperty("name", out var nm2) && nm2.ValueKind == JsonValueKind.Object && nm2.TryGetProperty("en", out var ne)
                    ? ne.GetString() : null,
                // <> 引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
                SearchText = ConceptRefRegex.Replace(
                    string.Join(" ", zhParts.Where(p => !string.IsNullOrEmpty(p))), ""),
            });
        }

        // 递归无条件下钻（匿名 pipeline 容器也要深入）
        // (2026-08-13: 无 id 直接 return 曾导致嵌套在 pipeline 中的流程节点
        //  从索引缺失——与 rebuild_index 同源修复)
        if (node.TryGetProperty("events", out var events))
        {
            foreach (var evt in events.EnumerateArray())
            {
                WalkFlowNode(evt, result, source);
            }
        }

        if (node.TryGetProperty("options", out var opts))
        {
            foreach (var opt in opts.EnumerateArray())
            {
                WalkFlowNode(opt, result, source);
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
                WalkFlowNode(container, result, source);
        }
    }

    /// <summary>
    /// 为指定游戏全量重建向量索引（删除并重建 collection）。
    /// </summary>
    public async Task BuildEmbeddingIndexAsync(string game)
    {
        if (_vectorSearch == null) return;
        var items = GetIndexItems(game);
        await _vectorSearch.RebuildIndexAsync(game, items);
    }

    /// <summary>
    /// 为指定游戏增量同步向量索引（默认路径；collection 缺失或维度不匹配时自动回退全量）。
    /// </summary>
    public async Task SyncEmbeddingIndexAsync(string game)
    {
        if (_vectorSearch == null) return;
        var items = GetIndexItems(game);
        await _vectorSearch.SyncIndexAsync(game, items);
    }

    // ---- 私有辅助 ----

    private static string BuildSearchText(ConceptSummary summary, JsonElement? detail)
    {
        // 与 scripts/rebuild_index.py 的 build_search_text 完全一致：
        // id + name.zh + name.en + description.zh + description.en（不重复拼接 summary.Name，
        // 否则中文名会出现两次，导致 C# 与 Python 重建路径生成的向量/哈希不一致）。
        var parts = new List<string> { summary.Id };
        if (detail.HasValue)
        {
            var detailEl = detail.Value;
            if (detailEl.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
            {
                if (name.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString()!);
                if (name.TryGetProperty("en", out var en)) parts.Add(en.GetString()!);
            }
            if (detailEl.TryGetProperty("description", out var def) && def.ValueKind == JsonValueKind.Object)
            {
                if (def.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString()!);
                if (def.TryGetProperty("en", out var en)) parts.Add(en.GetString()!);
            }
        }
        // <> 包裹的概念引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
        return ConceptRefRegex.Replace(string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))), "");
    }

    private static string ExtractNameZh(JsonElement element)
    {
        if (element.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.Object &&
            name.TryGetProperty("zh", out var zh))
        {
            return zh.GetString() ?? "";
        }
        return "";
    }

    private static string? ExtractEnName(JsonElement? element)
    {
        if (element.HasValue
            && element.Value.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.Object
            && name.TryGetProperty("en", out var en))
        {
            return en.GetString();
        }
        return null;
    }

}
