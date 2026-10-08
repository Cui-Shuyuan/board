using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

public sealed class RulesSearchService
{
    private readonly IRulesConceptCatalog _catalog;
    private readonly VectorSearchService? _vectorSearch;

    public RulesSearchService(
        IRulesConceptCatalog catalog,
        VectorSearchService? vectorSearch)
    {
        _catalog = catalog;
        _vectorSearch = vectorSearch;
    }

    public async Task<SearchConceptsResult> SearchConceptsAsync(
        string game,
        string query,
        string searchMode = "full",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
            return new SearchConceptsResult { Results = new List<ConceptSummary>(), Query = query };

        // 把 query 拆成子查询，加上原句一起并行搜
        var subQueries = SplitQuery(query);
        var allQueries = new HashSet<string>(subQueries) { query };

        // 并行：每个子句同时跑向量搜索 + 关键词搜索（批次带子句标记，供逐词分数记账）
        async Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)> VectorChannelAsync(string q, string mode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // topK 与合并结果的 top-15 对齐——5 会把原始相似度第 6 名开外的概念
            // 的向量分截成 0（激活骰被 favor_test 等挤出 top-5 的教训，2026-08-13）
            var items = await VectorSearchAsync(game, q, topK: 15, searchMode: mode, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // 同一 concept_id 可能来自多个来源（ontology 通用概念 + 游戏层具体实现，
            // 如 public_board），批次内去重取最高分——否则合并求和会重复计分
            var deduped = items
                .GroupBy(x => string.IsNullOrEmpty(x.Summary.Path) ? x.Summary.Id : x.Summary.Path)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .ToList();
            return (q, true, deduped);
        }

        async Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)> KeywordChannelAsync(string q)
        {
            var items = await KeywordSearchWithScoreAsync(game, q, cancellationToken);
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
        cancellationToken.ThrowIfCancellationRequested();

        var results = RulesSearchRanking.Rank(query, subQueries,
            allBatches.Select(batch => new RulesSearchRanking.Batch(
                batch.SubQuery, batch.IsVector, batch.Items)));

        // 向量搜索结果没有 description，从概念数据中补上
        PopulateDescriptions(game, results, cancellationToken);

        return new SearchConceptsResult
        {
            Results = results,
            Query = query,
            SplitTerms = subQueries.ToList()
        };
    }


    private void PopulateDescriptions(string game, List<ConceptSummary> results, CancellationToken cancellationToken)
    {
        foreach (var r in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(r.Description)) continue;
            var lookupId = string.IsNullOrEmpty(r.Path) ? r.Id : r.Path;
            var detail = _catalog.GetConcept(game, lookupId);
            if (detail.HasValue)
            {
                r.Description = RulesTextUtils.ExtractDescriptionZh(detail.Value);
            }
        }
    }


    public ListConceptsResult ListAllConceptIds(string game)
    {
        var byType = new Dictionary<string, List<ConceptSummary>>();
        foreach (var type in _catalog.GetConceptTypes(game))
        {
            var concepts = _catalog.ListConcepts(game, type);
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
        string game, string query, int topK, string searchMode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var results = await _vectorSearch!.SearchAsync(
                game, query, topK: topK, searchMode: searchMode, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return results.Select(r => (
                new ConceptSummary
                {
                    Id = string.IsNullOrEmpty(r.Path) ? r.ConceptId : r.Path,
                    Path = string.IsNullOrEmpty(r.Path) ? r.ConceptId : r.Path,
                    Name = r.NameZh,
                    Type = r.Type
                },
                r.Score
            )).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Vector search failed for '{query}': {ex.Message}");
            return new();
        }
    }


    private Task<List<(ConceptSummary Summary, float Score)>> KeywordSearchWithScoreAsync(
        string game, string query, CancellationToken cancellationToken)
    {
        // 关键词通道是同步内存扫描；Task.Run 能响应调度前的取消，扫描中途无法强制中断，
        // 但每个子查询通常很小。若以后关键词集合继续增长，应把 Token 深入扫描循环。
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = KeywordSearch(game, query);
            cancellationToken.ThrowIfCancellationRequested();
            return results.Select(r =>
            {
                // 关键词匹配只作小幅加成，不主导排序（2026-08-16 实测：内容命中 0.90 的
                // 固定分把「描述里提到该词」的无关概念顶上榜首，盖过向量语义分）。
                // 向量语义分是排序主体；关键词加成只用于打破同分与弱向量时的微调。
                var terms = Tokenize(query).ToList();
                var detail = _catalog.GetConcept(game, string.IsNullOrEmpty(r.Path) ? r.Id : r.Path);
                // 完整查询要求每个词在同一概念中出现；子词的部分命中另行留账。
                if (!terms.All(t => MatchesSummary(r, t)
                    || (detail.HasValue && ContainsTerm(detail.Value, t))))
                    return (r, 0f);
                float score;
                if (terms.Any(t => r.Id.Equals(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.5f;
                else if (terms.Any(t => r.Name.Contains(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.3f;
                else
                    score = 0.05f;
                return (r, score);
            }).Where(x => x.Item2 > 0).ToList();
        }, cancellationToken);
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

        var (ns, localQuery) = RulesTextUtils.ParseNamespace(query);
        var localTerms = Tokenize(localQuery).ToList();

        var all = new List<ConceptSummary>();
        if (string.IsNullOrEmpty(ns) || ns == "ontology")
            all.AddRange(_catalog.ListConcepts(game, "ontology"));

        if (string.IsNullOrEmpty(ns))
        {
            all.AddRange(_catalog.ListConcepts(game, "objects"));
            all.AddRange(_catalog.ListConcepts(game, "actions"));
            all.AddRange(_catalog.ListConcepts(game, "triggers"));
            all.AddRange(_catalog.ListConcepts(game, "conditions"));
            all.AddRange(_catalog.ListConcepts(game, "top_level_refs"));
            all.AddRange(_catalog.ListConcepts(game, "effects"));
            all.AddRange(_catalog.ListConcepts(game, "modules"));
            all.AddRange(_catalog.ListConcepts(game, "cards"));
            all.AddRange(_catalog.ListConcepts(game, "continent_tiles"));
            all.AddRange(_catalog.ListConcepts(game, "sites"));
            all.AddRange(_catalog.ListConcepts(game, "chips"));
            all.AddRange(_catalog.ListConcepts(game, "slots"));
            all.AddRange(_catalog.ListConcepts(game, "flow"));
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

            var detail = _catalog.GetConcept(game, summary.Id);
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
