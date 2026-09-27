using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则向量索引协作类：从概念目录、游戏实例、顶层引用与流程 JSON 提取索引条目，
/// 并负责触发向量索引重建。自身不缓存；规则文档变化时由调用方重新提取。
/// </summary>
public sealed class RulesIndexService
{
    private readonly IRulesConceptCatalog _catalog;
    private readonly RulesContentStore _content;
    private readonly VectorSearchService? _vectorSearch;

    public RulesIndexService(
        IRulesConceptCatalog catalog,
        RulesContentStore content,
        VectorSearchService? vectorSearch)
    {
        _catalog = catalog;
        _content = content;
        _vectorSearch = vectorSearch;
    }

    /// <summary>
    /// 提取游戏所有概念的索引条目，用于向量化。
    /// </summary>
    public IReadOnlyList<ConceptIndexItem> GetIndexItems(string game)
    {
        var result = new List<ConceptIndexItem>();

        // ontology 概念（不限定 game）
        foreach (var c in _catalog.ListConcepts(game, "ontology"))
        {
            var detail = _catalog.GetConcept(game, c.Id);
            result.Add(new ConceptIndexItem
            {
                ConceptId = c.Id,
                Type = "ontology",
                NameZh = c.Name,
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result);
        }

        foreach (var type in RulesConceptTypes.ConceptArrayTypes)
        {
            foreach (var c in _catalog.ListConcepts(game, type))
            {
                var detail = _catalog.GetConcept(game, c.Id);
                result.Add(new ConceptIndexItem
                {
                    ConceptId = c.Id,
                    Type = type,
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result);
            }
        }

        // instances.json 实例
        foreach (var type in RulesConceptTypes.InstanceArrayTypes)
        {
            foreach (var c in _catalog.ListConcepts(game, type))
            {
                var detail = _catalog.GetConcept(game, c.Id);
                result.Add(new ConceptIndexItem
                {
                    ConceptId = c.Id,
                    Type = type,
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result);
            }
        }

        // 顶层引用
        foreach (var c in _catalog.ListConcepts(game, "top_level_refs"))
        {
            var detail = _catalog.GetConcept(game, c.Id);
            result.Add(new ConceptIndexItem
            {
                ConceptId = c.Id,
                Type = "top_level_ref",
                NameZh = c.Name,
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result);
        }

        // content/ontology/flow.json
        var ontologyFlow = _content.LoadOntologyFlow();
        if (ontologyFlow != null)
        {
            ExtractFlowItems(ontologyFlow.RootElement, result);
        }

        // flow.json 流程
        var flow = _content.LoadGameFlow(game);
        if (flow != null)
        {
            ExtractFlowItems(flow.RootElement, result);
        }

        return result;
    }

    /// <summary>
    /// 为指定游戏重建向量索引。
    /// </summary>
    public async Task BuildEmbeddingIndexAsync(string game)
    {
        if (_vectorSearch == null) return;
        var items = GetIndexItems(game);
        await _vectorSearch.RebuildIndexAsync(game, items);
    }

    /// <summary>递归提取 slots 元素 (裸键槽名如 population/expansion 作为概念, 与 Python rebuild_index 一致)</summary>
    private static void ExtractSlots(JsonElement node, List<ConceptIndexItem> result)
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
                            NameZh = "",
                            NameEn = "",
                            SearchText = string.Join(" ", parts),
                        });
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
                ExtractSlots(prop.Value, result);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                ExtractSlots(item, result);
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

    private static void ExtractFlowItems(JsonElement root, List<ConceptIndexItem> result)
    {
        // 游戏 flow.json：procedures 树 + triggers 组
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!root.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                WalkFlowNode(item, result);
            }
        }

        // ontology flow.json：pipeline.options 数组
        if (root.TryGetProperty("pipeline", out var pipeline) &&
            pipeline.TryGetProperty("options", out var options))
        {
            foreach (var opt in options.EnumerateArray())
            {
                WalkFlowNode(opt, result);
            }
        }
    }

    private static void WalkFlowNode(JsonElement node, List<ConceptIndexItem> result)
    {
        // 游戏 flow 的 options 中可能存在 <concept_id> 字符串引用；
        // 跳过非对象节点，保持与 rebuild_index.py 相同的下钻语义。
        if (node.ValueKind != JsonValueKind.Object)
            return;

        var id = node.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        // 只索引独立概念节点（有 id 且有 specifies/extends/instance_of）：
        // 局部步骤（仅 id+name，do_after 引用用）不入索引——短名短描述是向量噪音，
        // 且同 id 跨位置重复互相覆盖；步骤信息随父概念的 get_concept 完整返回。
        // game 等通用容器概念（各游戏共有的顶层流程宿主）也不入索引。
        if (!string.IsNullOrEmpty(id) && id != "game" && RulesConceptTypes.IsStandaloneFlowNode(node))
        {
            var zhParts = new List<string> { id };

            var nodeType = node.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(nodeType))
                zhParts.Add(nodeType);

            if (node.TryGetProperty("name", out var name) && name.TryGetProperty("zh", out var nzh))
                zhParts.Add(nzh.GetString()!);
            if (node.TryGetProperty("description", out var desc) && desc.TryGetProperty("zh", out var dzh))
                zhParts.Add(dzh.GetString()!);

            result.Add(new ConceptIndexItem
            {
                ConceptId = id,
                Type = "flow",
                NameZh = node.TryGetProperty("name", out var nm) && nm.TryGetProperty("zh", out var nz)
                    ? nz.GetString() : id,
                NameEn = null,
                // <> 引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
                SearchText = RulesJsonUtils.ConceptRefRegex.Replace(
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
                WalkFlowNode(evt, result);
            }
        }

        if (node.TryGetProperty("options", out var opts))
        {
            foreach (var opt in opts.EnumerateArray())
            {
                WalkFlowNode(opt, result);
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
                WalkFlowNode(container, result);
        }
    }

    // ---- 私有辅助 ----

    private static string BuildSearchText(ConceptSummary summary, JsonElement? detail)
    {
        var parts = new List<string> { summary.Id, summary.Name };
        if (detail.HasValue)
        {
            var detailEl = detail.Value;
            if (detailEl.TryGetProperty("name", out var name))
            {
                if (name.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString()!);
                if (name.TryGetProperty("en", out var en)) parts.Add(en.GetString()!);
            }
            if (detailEl.TryGetProperty("description", out var def))
            {
                if (def.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString()!);
                if (def.TryGetProperty("en", out var en)) parts.Add(en.GetString()!);
            }
        }
        // <> 包裹的概念引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
        return RulesJsonUtils.ConceptRefRegex.Replace(string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))), "");
    }

    private static string? ExtractEnName(JsonElement? element)
    {
        if (element.HasValue
            && element.Value.TryGetProperty("name", out var name)
            && name.TryGetProperty("en", out var en))
        {
            return en.GetString();
        }
        return null;
    }
}
