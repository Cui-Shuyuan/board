using System.Text.Json;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public class GameRulesService
{
    private readonly string _basePath;
    private readonly VectorSearchService? _vectorSearch;
    private readonly Dictionary<string, JsonDocument> _loadedFiles = new();

    private static readonly string[] ConceptArrayTypes = { "objects", "actions", "triggers", "conditions" };
    private static readonly string[] InstanceArrayTypes = { "effects", "modules", "cards", "continent_tiles", "sites", "chips" };

    public GameRulesService(IOptions<RulesOptions> options, VectorSearchService? vectorSearch = null)
    {
        _basePath = options.Value.BasePath;
        if (string.IsNullOrWhiteSpace(_basePath))
        {
            throw new InvalidOperationException("Rules:BasePath is not configured.");
        }
        _vectorSearch = vectorSearch;
    }

    public IReadOnlyList<string> GetGames()
    {
        var gamesDir = Path.Combine(_basePath, "games");
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

    private readonly Dictionary<string, Dictionary<string, string>> _nameMaps = new();

    /// <summary>
    /// 把文本中的概念引用 <concept_id> / <ontology::concept_id> 注解为 <id>(中文名)，
    /// 让 LLM 无需自行翻译英文 id（如 <idea_marker>(创意标记)）。
    /// 查不到映射的引用（枚举值、未知 id）保持原样。
    /// </summary>
    public string AnnotateReferences(string text, string game)
    {
        var map = GetNameMap(game);
        if (map.Count == 0) return text;
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>",
            m =>
            {
                var raw = m.Groups[1].Value;
                var localId = raw.Contains("::") ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..] : raw;
                if (map.TryGetValue(localId, out var name) && !string.IsNullOrEmpty(name))
                    return $"<{raw}>({name})";
                return m.Value;
            });
    }

    private Dictionary<string, string> GetNameMap(string game)
    {
        if (_nameMaps.TryGetValue(game, out var cached)) return cached;
        var map = new Dictionary<string, string>();

        foreach (var type in GetConceptTypes(game))
        {
            foreach (var summary in ListConcepts(game, type))
            {
                if (!string.IsNullOrEmpty(summary.Id) && !string.IsNullOrEmpty(summary.Name) && summary.Id != summary.Name)
                    map[summary.Id] = summary.Name;
            }
        }

        // flow.json 递归节点（procedures/triggers 内嵌的 id + name.zh）
        var flow = LoadGameFlow(game);
        if (flow != null) WalkFlowForNames(flow.RootElement, map);
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null) WalkFlowForNames(ontologyFlow.RootElement, map);

        _nameMaps[game] = map;
        return map;
    }

    private static void WalkFlowForNames(JsonElement node, Dictionary<string, string> map)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(id)
                    && node.TryGetProperty("name", out var name)
                    && name.TryGetProperty("zh", out var zh))
                {
                    var zhName = zh.GetString() ?? "";
                    if (!string.IsNullOrEmpty(zhName) && !map.ContainsKey(id))
                        map[id] = zhName;
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkFlowForNames(prop.Value, map);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowForNames(item, map);
        }
    }

    public async Task<SearchConceptsResult> SearchConceptsAsync(string game, string query, string searchMode = "full")
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchConceptsResult { Results = new List<ConceptSummary>(), Query = query };

        var useNameOnly = searchMode == "name";

        // 把 query 拆成子查询，加上原句一起并行搜
        var subQueries = SplitQuery(query);
        var allQueries = new HashSet<string>(subQueries) { query };

        // 并行：每个子句同时跑向量搜索 + 关键词搜索
        var tasks = new List<Task<List<(ConceptSummary Summary, float Score)>>>();
        foreach (var q in allQueries)
        {
            if (_vectorSearch != null)
                tasks.Add(VectorSearchAsync(game, q, topK: 5, searchMode: searchMode));
            tasks.Add(KeywordSearchWithScoreAsync(game, q));
        }

        var allBatches = await Task.WhenAll(tasks);

        // 合并去重：同一概念累加各通道分数（AND 语义——匹配子词越多得分越高）
        var merged = new Dictionary<string, (ConceptSummary Summary, float Score)>();
        foreach (var batch in allBatches)
        {
            foreach (var (summary, score) in batch)
            {
                if (!merged.TryGetValue(summary.Id, out var existing))
                    merged[summary.Id] = (summary, 0);
                merged[summary.Id] = (summary, merged[summary.Id].Score + score);
            }
        }

        // 按子词数量归一化：匹配词越多的概念得分越高
        var divisor = Math.Max(subQueries.Length, 1);
        var results = merged.Values
            .Select(x => { x.Summary.Score = x.Score / divisor; return x.Summary; })
            .OrderByDescending(x => x.Score)
            .Take(15)
            .ToList();

        // 向量搜索结果没有 description，从概念数据中补上
        PopulateDescriptions(game, results);

        return new SearchConceptsResult
        {
            Results = results,
            Query = query
        };
    }

    private void PopulateDescriptions(string game, List<ConceptSummary> results)
    {
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r.Description)) continue;
            var detail = GetConcept(game, r.Id);
            if (detail.HasValue)
            {
                r.Description = ExtractDescriptionZh(detail.Value);
            }
        }
    }

    public ListConceptsResult ListAllConceptIds(string game)
    {
        var byType = new Dictionary<string, List<ConceptSummary>>();
        foreach (var type in GetConceptTypes(game))
        {
            var concepts = ListConcepts(game, type);
            if (concepts.Count > 0)
                byType[type] = concepts.Select(c => new ConceptSummary
                {
                    Id = c.Id,
                    Name = c.Name,
                    Type = c.Type
                }).ToList();
        }

        return new ListConceptsResult
        {
            ByType = byType,
            TotalCount = byType.Values.Sum(v => v.Count)
        };
    }

    private async Task<List<(ConceptSummary Summary, float Score)>> VectorSearchAsync(
        string game, string query, int topK, string searchMode = "full")
    {
        try
        {
            var results = await _vectorSearch!.SearchAsync(game, query, topK: topK, searchMode: searchMode);
            return results.Select(r => (
                new ConceptSummary { Id = r.ConceptId, Name = r.NameZh, Type = r.Type },
                r.Score
            )).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Vector search failed for '{query}': {ex.Message}");
            return new();
        }
    }

    private Task<List<(ConceptSummary Summary, float Score)>> KeywordSearchWithScoreAsync(
        string game, string query)
    {
        return Task.Run(() =>
        {
            var results = KeywordSearch(game, query);
            return results.Select(r =>
            {
                // 关键词匹配给高分：精确 ID 命中 > 名字命中 > 内容命中
                var terms = Tokenize(query).ToList();
                float score = 1.0f;
                if (terms.Any(t => r.Id.Equals(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 1.0f;
                else if (terms.Any(t => r.Name.Contains(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.95f;
                else
                    score = 0.90f;
                return (r, score);
            }).ToList();
        });
    }

    /// <summary>
    /// 按标点和空格拆 query，不做 lowercase，保留 LLM 原始意图。
    /// </summary>
    private static string[] SplitQuery(string query)
    {
        return query.Split(
            new[] { ' ', '\t', '\n', '\r', '，', '。', '、', '？', '！', '；', '：', '"', '"' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// 原有关键词搜索逻辑（向量搜索不可用或没结果时降级）。
    /// </summary>
    public IReadOnlyList<ConceptSummary> KeywordSearch(string game, string query)
    {
        var terms = Tokenize(query).ToList();
        if (terms.Count == 0) return Array.Empty<ConceptSummary>();

        var (ns, localQuery) = ParseNamespace(query);
        var localTerms = Tokenize(localQuery).ToList();

        var all = new List<ConceptSummary>();
        if (string.IsNullOrEmpty(ns) || ns == "ontology")
            all.AddRange(ListConcepts(game, "ontology"));

        if (string.IsNullOrEmpty(ns))
        {
            all.AddRange(ListConcepts(game, "objects"));
            all.AddRange(ListConcepts(game, "actions"));
            all.AddRange(ListConcepts(game, "triggers"));
            all.AddRange(ListConcepts(game, "conditions"));
            all.AddRange(ListConcepts(game, "top_level_refs"));
            all.AddRange(ListConcepts(game, "effects"));
            all.AddRange(ListConcepts(game, "modules"));
            all.AddRange(ListConcepts(game, "cards"));
            all.AddRange(ListConcepts(game, "continent_tiles"));
            all.AddRange(ListConcepts(game, "sites"));
            all.AddRange(ListConcepts(game, "chips"));
            all.AddRange(ListConcepts(game, "slots"));
        }

        var activeTerms = localTerms.Count > 0 ? localTerms : terms;
        var results = new List<ConceptSummary>();
        foreach (var summary in all)
        {
            if (activeTerms.Any(t => MatchesSummary(summary, t)))
            {
                results.Add(summary);
                continue;
            }

            var detail = GetConcept(game, summary.Id);
            if (detail.HasValue && activeTerms.Any(t => ContainsTerm(detail.Value, t)))
            {
                results.Add(summary);
            }
        }

        return results;
    }

    /// <summary>
    /// 提取游戏所有概念的索引条目，用于向量化。
    /// </summary>
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
                NameZh = c.Name,
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result);
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
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result);
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
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result);
            }
        }

        // 顶层引用
        foreach (var c in ListConcepts(game, "top_level_refs"))
        {
            var detail = GetConcept(game, c.Id);
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

        // ontology/flow.json
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null)
        {
            ExtractFlowItems(ontologyFlow.RootElement, result);
        }

        // flow.json 流程
        var flow = LoadGameFlow(game);
        if (flow != null)
        {
            ExtractFlowItems(flow.RootElement, result);
        }

        return result;
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
        var id = node.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) return;

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
            SearchText = string.Join(" ", zhParts.Where(p => !string.IsNullOrEmpty(p))),
        });

        // events（嵌入的 event 节点也索引入）
        if (node.TryGetProperty("events", out var events))
        {
            foreach (var evt in events.EnumerateArray())
            {
                WalkFlowNode(evt, result);
            }
        }

        // options（pipeline 选项）
        if (node.TryGetProperty("options", out var opts))
        {
            foreach (var opt in opts.EnumerateArray())
            {
                WalkFlowNode(opt, result);
            }
        }
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
        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
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
        if (!node.TryGetProperty("id", out var idProp)) return;
        var id = idProp.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(id) || !seen.Add(id)) return;

        results.Add(new ConceptSummary
        {
            Id = id,
            Name = ExtractName(node),
            Type = "flow"
        });

        // 递归：events、options、content 容器
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

        // recurse into container fields: content, cost, condition, effect
        foreach (var containerKey in new[] {
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

public class ConceptSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public float Score { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, JsonElement>? Media { get; set; }
}

public class SearchConceptsResult
{
    public List<ConceptSummary> Results { get; set; } = new();
    public int Count => Results.Count;
    public string Query { get; set; } = string.Empty;
    public string Strategy { get; set; } = "hybrid_vector_keyword";
    public string Note { get; set; } = "Top results only — NOT exhaustive. If you need to see ALL concepts (e.g., to browse what exists), use list_concept_ids.";
}

public class ListConceptsResult
{
    public Dictionary<string, List<ConceptSummary>> ByType { get; set; } = new();
    public int TotalCount { get; set; }
    public string Note { get; set; } = "Exhaustive listing of ALL concept IDs and names grouped by type. Use this to confirm a concept doesn't exist or to browse the full catalog.";
}
