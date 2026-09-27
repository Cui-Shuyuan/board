using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService
{

    private readonly Dictionary<string, Dictionary<string, string>> _nameMaps = new();


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


    private readonly Dictionary<string, Dictionary<string, List<(string Id, string Kind)>>> _exactLookups = new();


    /// <summary>
    /// 「名词直呼」精确查找表：zh 名 / en 名（大小写不敏感）/ aliases / 基名（括号注解剥除）
    /// → 概念 id 列表 + 匹配类别。一个键可以映射多个概念（如基名「家庭成长」→ 需空房间与
    /// 无需房间两个行动；别名「随时转换效果」→ 烹饪与生吃），多命中全部返回由 LLM 读数据取舍。
    /// 与 GetNameMap 同源（概念 + 实例 + flow + 本体）。首次访问时构建并缓存；
    /// JSON 修改后需重启 API 才生效（与 LoadGameConcepts 一致）。
    /// </summary>
    private Dictionary<string, List<(string Id, string Kind)>> GetExactLookup(string game)
    {
        if (_exactLookups.TryGetValue(game, out var cached)) return cached;

        var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string id, string kind)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<(string, string)>();
                map[key] = list;
            }
            if (!list.Any(e => e.Item1 == id)) list.Add((id, kind));
        }

        // 1) 既有来源的 zh 名（与 GetNameMap 同源，保持原行为）
        foreach (var type in GetConceptTypes(game))
            foreach (var summary in ListConcepts(game, type))
                Add(summary.Name, summary.Id, "exact_name_zh");

        // 2) 游戏概念 + 实例的 zh/en 名与 aliases（只有原始 JSON 才有这些字段）
        AddRawNames(LoadGameConcepts(game), ConceptArrayTypes, Add);
        AddRawNames(LoadGameInstances(game), InstanceArrayTypes, Add);

        // 3) 本体概念 en 名（zh 名太通用——行动/转移/对象——不进直呼表，避免噪声）
        AddRawNames(LoadOntology(), new[] { "concepts" }, Add);

        // 4) flow 节点 zh/en 名
        var flow = LoadGameFlow(game);
        if (flow != null) WalkFlowNames(flow.RootElement, Add);
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null) WalkFlowNames(ontologyFlow.RootElement, Add);

        // 5) 基名：把 zh 名里的括号注解剥掉（「家庭成长（需空房间）」→「家庭成长」），
        //    让客人/LLM 只说名字主体也能直呼命中——2026-08-16 QA 显示 C 类题几乎全是
        //    转述与全名不一致导致的实体解析失败
        var bases = new List<(string Key, string Id)>();
        foreach (var (key, list) in map)
            foreach (var (id, kind) in list)
                if (kind == "exact_name_zh")
                {
                    var b = StripAnnotations(key);
                    if (b != null && !string.Equals(b, key, StringComparison.Ordinal))
                        bases.Add((b, id));
                }
        foreach (var (key, id) in bases)
            Add(key, id, "exact_base");

        _exactLookups[game] = map;
        return map;
    }


    /// <summary>剥除中文名里的括号注解（全角/半角均可），剥后不足 2 字返回 null。</summary>
    private static string? StripAnnotations(string name)
    {
        var s = ParentheticalRegex.Replace(name, "");
        s = s.Trim();
        return s.Length >= 2 ? s : null;
    }


    private static readonly Regex ParentheticalRegex = new(@"[（(][^（）()]*[）)]");


    private static void AddRawNames(JsonDocument? doc, string[] arrays,
        Action<string, string, string> add)
    {
        if (doc == null) return;
        foreach (var type in arrays)
        {
            if (!doc.RootElement.TryGetProperty(type, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
            {
                var id = GetElementId(el);
                if (string.IsNullOrEmpty(id)) continue;
                if (el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
                {
                    if (name.TryGetProperty("zh", out var zh))
                    {
                        var zhName = zh.GetString() ?? "";
                        if (!string.IsNullOrEmpty(zhName)) add(zhName, id, "exact_name_zh");
                    }
                    if (name.TryGetProperty("en", out var en))
                    {
                        var enName = en.GetString() ?? "";
                        if (!string.IsNullOrEmpty(enName)) add(enName, id, "exact_name_en");
                    }
                }
                if (el.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
                {
                    foreach (var lang in new[] { "zh", "en" })
                    {
                        if (al.TryGetProperty(lang, out var list) && list.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in list.EnumerateArray())
                            {
                                var s = a.GetString() ?? "";
                                if (!string.IsNullOrEmpty(s)) add(s, id, "exact_alias");
                            }
                        }
                    }
                }
            }
        }
    }


    private static void WalkFlowNames(JsonElement node, Action<string, string, string> add)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(id))
                {
                    if (node.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.Object)
                    {
                        if (name.TryGetProperty("zh", out var zh))
                        {
                            var zhName = zh.GetString() ?? "";
                            if (!string.IsNullOrEmpty(zhName)) add(zhName, id, "exact_name_zh");
                        }
                        if (name.TryGetProperty("en", out var en))
                        {
                            var enName = en.GetString() ?? "";
                            if (!string.IsNullOrEmpty(enName)) add(enName, id, "exact_name_en");
                        }
                    }
                    // flow 节点别名（与概念同形）——如 give_starting_food 别名「开局食物」
                    if (node.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var lang in new[] { "zh", "en" })
                        {
                            if (al.TryGetProperty(lang, out var list) && list.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var a in list.EnumerateArray())
                                {
                                    var s = a.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(s)) add(s, id, "exact_alias");
                                }
                            }
                        }
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkFlowNames(prop.Value, add);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowNames(item, add);
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

        // 并行：每个子句同时跑向量搜索 + 关键词搜索（批次带子句标记，供逐词分数记账）
        async Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)> VectorChannelAsync(string q, string mode)
        {
            // topK 与合并结果的 top-15 对齐——5 会把原始相似度第 6 名开外的概念
            // 的向量分截成 0（激活骰被 favor_test 等挤出 top-5 的教训，2026-08-13）
            var items = await VectorSearchAsync(game, q, topK: 15, searchMode: mode);
            // 同一 concept_id 可能来自多个来源（ontology 通用概念 + 游戏层具体实现，
            // 如 public_board），批次内去重取最高分——否则合并求和会重复计分
            var deduped = items
                .GroupBy(x => x.Summary.Id)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .ToList();
            return (q, true, deduped);
        }

        async Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)> KeywordChannelAsync(string q)
        {
            var items = await KeywordSearchWithScoreAsync(game, q);
            return (q, false, items);
        }

        var tasks = new List<Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)>>();
        foreach (var q in allQueries)
        {
            // 路由（2026-08-13 实验）：短词（≤2 字）是词级对局——查名字集合
            // （短文本对短文本，实测排序有意义）；长词/整句是句级对局——查全文集合。
            // BGE 句模型配「短词 vs 全文」落在窄锥噪声带（无关文本余弦基线 0.74-0.82，
            // 实测「白色」top-16 无一个相关概念），排序近似随机
            if (_vectorSearch != null)
            {
                var effectiveMode = searchMode == "name" || q.Length <= 2 ? "name" : "full";
                tasks.Add(VectorChannelAsync(q, effectiveMode));
            }
            tasks.Add(KeywordChannelAsync(q));
        }

        var allBatches = await Task.WhenAll(tasks);

        // 合并去重：同一概念累加各通道分数（AND 语义——匹配子词越多得分越高）；
        // 同时按子词分别记账 vector/keyword 双通道分数，供 LLM 判断每个词匹配强弱
        var merged = new Dictionary<string, AccumulatedSearch>();
        foreach (var (subQuery, isVector, items) in allBatches)
        {
            foreach (var (summary, score) in items)
            {
                if (!merged.TryGetValue(summary.Id, out var acc))
                {
                    acc = new AccumulatedSearch { Summary = summary };
                    merged[summary.Id] = acc;
                }
                acc.Total += score;
                var byTerm = isVector ? acc.VectorByTerm : acc.KeywordByTerm;
                byTerm[subQuery] = byTerm.GetValueOrDefault(subQuery) + score;
            }
        }

        // 按子词数量归一化：匹配词越多的概念得分越高（排名算法不变）
        var divisor = Math.Max(subQueries.Length, 1);
        var hasFullQuery = !subQueries.Contains(query);
        var results = merged.Values
            .Select(acc => new { acc, RawScore = acc.Total / divisor })
            .OrderByDescending(x => x.RawScore)
            .Take(15)
            .Select(x =>
            {
                var summary = x.acc.Summary;
                summary.Score = MathF.Round(x.RawScore, 2);
                summary.TermScores = subQueries.ToDictionary(
                    t => t,
                    t => new ChannelScores
                    {
                        Vector = MathF.Round(x.acc.VectorByTerm.GetValueOrDefault(t), 2),
                        Keyword = MathF.Round(x.acc.KeywordByTerm.GetValueOrDefault(t), 2)
                    });
                if (hasFullQuery)
                {
                    summary.FullQueryScore = new ChannelScores
                    {
                        Vector = MathF.Round(x.acc.VectorByTerm.GetValueOrDefault(query), 2),
                        Keyword = MathF.Round(x.acc.KeywordByTerm.GetValueOrDefault(query), 2)
                    };
                }
                return summary;
            })
            .ToList();

        // 向量搜索结果没有 description，从概念数据中补上
        PopulateDescriptions(game, results);

        return new SearchConceptsResult
        {
            Results = results,
            Query = query,
            SplitTerms = subQueries.ToList()
        };
    }


    /// <summary>搜索合并过程中的单概念累计分数（按子词分通道记账）。</summary>
    private sealed class AccumulatedSearch
    {
        public ConceptSummary Summary = null!;
        public float Total;
        public readonly Dictionary<string, float> VectorByTerm = new();
        public readonly Dictionary<string, float> KeywordByTerm = new();
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
                // 关键词匹配只作小幅加成，不主导排序（2026-08-16 实测：内容命中 0.90 的
                // 固定分把「描述里提到该词」的无关概念顶上榜首，盖过向量语义分）。
                // 向量语义分是排序主体；关键词加成只用于打破同分与弱向量时的微调。
                var terms = Tokenize(query).ToList();
                float score;
                if (terms.Any(t => r.Id.Equals(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.5f;
                else if (terms.Any(t => r.Name.Contains(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.3f;
                else
                    score = 0.05f;
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
            all.AddRange(ListConcepts(game, "flow"));
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
