using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则向量索引协作类：从概念目录、游戏实例、顶层引用与流程 JSON 提取索引条目，
/// 并负责触发向量索引重建。自身不缓存；规则文档变化时由调用方重新提取。
/// </summary>
public sealed class RulesIndexService
{
    private readonly RulesContentStore _content;
    private readonly VectorSearchService? _vectorSearch;

    public RulesIndexService(
        IRulesConceptCatalog catalog,
        RulesContentStore content,
        VectorSearchService? vectorSearch)
    {
        // Kept for constructor compatibility with existing call sites; index
        // extraction intentionally reads raw JSON so the CLI/API contract is
        // not altered by catalog-side id resolution.
        _ = catalog;
        _content = content;
        _vectorSearch = vectorSearch;
    }

    /// <summary>
    /// 提取游戏所有概念的索引条目，用于向量化。
    /// </summary>
    public IReadOnlyList<ConceptIndexItem> GetIndexItems(string game)
    {
        var result = new List<ConceptIndexItem>();

        // Raw JSON extraction, matching tools/indexing/rebuild_index.py.
        ExtractConceptItems(_content.LoadOntology(), "ontology", result);
        ExtractConceptItems(_content.LoadGameConcepts(game), "game", result);
        ExtractInstanceItems(_content.LoadGameInstances(game), result);

        // Top-level references are indexed by their JSON key; Python does the
        // same for both ontology and game files.
        ExtractTopLevelRefs(_content.LoadGameConcepts(game), "game", result);
        ExtractTopLevelRefs(_content.LoadOntology(), "ontology", result);

        // content/ontology/flow.json
        var ontologyFlow = _content.LoadOntologyFlow();
        if (ontologyFlow != null)
        {
            ExtractFlowItems(ontologyFlow.RootElement, result, "ontology_flow");
        }

        // flow.json 流程
        var flow = _content.LoadGameFlow(game);
        if (flow != null)
        {
            ExtractFlowItems(flow.RootElement, result, "game_flow");
        }

        // 通用流程容器概念不参与语义检索（与 rebuild_index.py 一致）。
        return result.Where(item => item.ConceptId != "game").ToList();
    }

    /// <summary>
    /// 为指定游戏重建向量索引。
    /// </summary>
    public async Task<IndexBuildResult?> BuildEmbeddingIndexAsync(string game)
    {
        if (_vectorSearch == null) return null;
        var items = GetIndexItems(game);
        return await _vectorSearch.RebuildIndexAsync(game, items);
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
                            NameText = "",
                            // <> 引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
                            SearchText = RulesJsonUtils.ConceptRefRegex.Replace(
                                string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part))), ""),
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

    private static void WalkFlowNode(JsonElement node, List<ConceptIndexItem> result, string source)
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

            var flowNameZh = node.TryGetProperty("name", out var nm) && nm.TryGetProperty("zh", out var nz)
                ? nz.GetString() : id;
            var flowNameEn = node.TryGetProperty("name", out var nmEn) && nmEn.TryGetProperty("en", out var nen)
                ? nen.GetString() : null;
            result.Add(new ConceptIndexItem
            {
                ConceptId = id,
                Type = "flow",
                Source = source,
                NameZh = flowNameZh,
                NameEn = flowNameEn,
                NameText = !string.IsNullOrWhiteSpace(flowNameZh) ? flowNameZh : (flowNameEn ?? ""),
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

    // ---- 私有辅助 ----

    private static void ExtractConceptItems(
        JsonDocument? document,
        string source,
        List<ConceptIndexItem> result)
    {
        if (document == null) return;

        var root = document.RootElement;
        if (root.TryGetProperty("concepts", out var concepts) && concepts.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in concepts.EnumerateArray())
                AddRawConceptItem(element, "ontology", source, includeSlots: true, result);
        }

        foreach (var type in RulesConceptTypes.ConceptArrayTypes)
        {
            if (!root.TryGetProperty(type, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var element in array.EnumerateArray())
                AddRawConceptItem(element, type, source, includeSlots: true, result);
        }
    }

    private static void ExtractInstanceItems(
        JsonDocument? document,
        List<ConceptIndexItem> result)
    {
        if (document == null) return;

        var root = document.RootElement;
        foreach (var type in RulesConceptTypes.InstanceArrayTypes)
        {
            if (!root.TryGetProperty(type, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var element in array.EnumerateArray())
                AddRawConceptItem(element, type, "instances", includeSlots: false, result);
        }
    }

    private static void ExtractTopLevelRefs(
        JsonDocument? document,
        string source,
        List<ConceptIndexItem> result)
    {
        if (document == null) return;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "concepts" || RulesConceptTypes.ConceptArrayTypes.Contains(property.Name))
                continue;
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            AddRawTopLevelRefItem(property.Value, property.Name, source, result);
        }
    }

    private static void AddRawTopLevelRefItem(
        JsonElement element,
        string key,
        string source,
        List<ConceptIndexItem> result)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        var nameZh = "";
        var nameEn = "";
        if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            if (name.TryGetProperty("zh", out var zh)) nameZh = zh.GetString() ?? "";
            if (name.TryGetProperty("en", out var en)) nameEn = en.GetString() ?? "";
        }

        var parts = new List<string>();
        // Python 的 build_search_text 使用 value["id"]，而不是 JSON key。
        if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            parts.Add(id.GetString() ?? "");
        if (!string.IsNullOrEmpty(nameZh)) parts.Add(nameZh);
        if (!string.IsNullOrEmpty(nameEn)) parts.Add(nameEn);
        if (element.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.Object)
        {
            if (description.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString() ?? "");
            if (description.TryGetProperty("en", out var en)) parts.Add(en.GetString() ?? "");
        }

        result.Add(new ConceptIndexItem
        {
            ConceptId = key,
            Type = "top_level_ref",
            Source = source,
            NameZh = nameZh,
            NameEn = nameEn,
            NameText = !string.IsNullOrWhiteSpace(nameZh) ? nameZh : nameEn,
            SearchText = RulesJsonUtils.ConceptRefRegex.Replace(
                string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part))), ""),
        });

        ExtractSlots(element, result, source);
    }

    private static void AddRawConceptItem(
        JsonElement element,
        string type,
        string source,
        bool includeSlots,
        List<ConceptIndexItem> result)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        var conceptId = element.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? ""
            : "";

        AddRawConceptItem(element, conceptId, type, source, includeSlots, result);
    }

    private static void AddRawConceptItem(
        JsonElement element,
        string conceptId,
        string type,
        string source,
        bool includeSlots,
        List<ConceptIndexItem> result)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        var nameZh = "";
        var nameEn = "";
        if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            if (name.TryGetProperty("zh", out var zh)) nameZh = zh.GetString() ?? "";
            if (name.TryGetProperty("en", out var en)) nameEn = en.GetString() ?? "";
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(conceptId)) parts.Add(conceptId);
        if (!string.IsNullOrEmpty(nameZh)) parts.Add(nameZh);
        if (!string.IsNullOrEmpty(nameEn)) parts.Add(nameEn);
        if (element.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.Object)
        {
            if (description.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString() ?? "");
            if (description.TryGetProperty("en", out var en)) parts.Add(en.GetString() ?? "");
        }

        var nameText = !string.IsNullOrWhiteSpace(nameZh) ? nameZh : nameEn;
        result.Add(new ConceptIndexItem
        {
            ConceptId = conceptId,
            Type = type,
            Source = source,
            NameZh = nameZh,
            NameEn = nameEn,
            NameText = nameText,
            // <> 引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
            SearchText = RulesJsonUtils.ConceptRefRegex.Replace(
                string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part))), ""),
        });

        if (includeSlots)
            ExtractSlots(element, result, source);
    }

}
