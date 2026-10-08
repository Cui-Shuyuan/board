namespace BoardAI.Api.Services;

/// <summary>整句匹配主导排序；子查询只补充召回，同来源重复不能增加相关性。</summary>
internal static class RulesSearchRanking
{
    internal sealed record Batch(
        string Query, bool IsVector, List<(ConceptSummary Summary, float Score)> Items);

    private sealed class Entry
    {
        public required ConceptSummary Summary;
        public readonly Dictionary<string, float> Vector = new(StringComparer.Ordinal);
        public readonly Dictionary<string, float> Keyword = new(StringComparer.Ordinal);
    }

    internal static List<ConceptSummary> Rank(string query, string[] terms, IEnumerable<Batch> batches)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var batch in batches)
        foreach (var (summary, score) in batch.Items)
        {
            var key = string.IsNullOrEmpty(summary.Path) ? summary.Id : summary.Path;
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Summary = summary };
                entries.Add(key, entry);
            }
            var channel = batch.IsVector ? entry.Vector : entry.Keyword;
            channel[batch.Query] = Math.Max(channel.GetValueOrDefault(batch.Query), score);
        }

        var split = !terms.Contains(query, StringComparer.Ordinal);
        return entries.Values.Select(entry =>
        {
            var phrase = new ChannelScores
            {
                Vector = entry.Vector.GetValueOrDefault(query),
                Keyword = entry.Keyword.GetValueOrDefault(query)
            };
            // 只命中泛词的候选，不能凭多通道累加超过整句匹配。
            var boost = split && terms.Length > 0
                ? 0.02f * terms.Average(t => entry.Vector.GetValueOrDefault(t) + entry.Keyword.GetValueOrDefault(t))
                : 0;
            var s = entry.Summary;
            var result = new ConceptSummary
            {
                Id = s.Id, Path = s.Path, Name = s.Name, Type = s.Type,
                Description = s.Description, Media = s.Media,
                PhraseScore = phrase,
                SubqueryBoost = MathF.Round(boost, 4),
                TermScores = terms.ToDictionary(t => t, t => new ChannelScores
                {
                    Vector = MathF.Round(entry.Vector.GetValueOrDefault(t), 4),
                    Keyword = MathF.Round(entry.Keyword.GetValueOrDefault(t), 4)
                }),
                FullQueryScore = split ? phrase : null
            };
            return (Summary: result, Score: phrase.Vector + phrase.Keyword + boost);
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Summary.Id, StringComparer.Ordinal)
        .Take(15)
        .Select(x => { x.Summary.Score = MathF.Round(x.Score, 4); return x.Summary; })
        .ToList();
    }


    /// <summary>名称索引补漏；不同索引的相似度不能直接相加，也不能盖过全文语义候选。</summary>
    internal static List<ConceptSummary> SupplementNames(
        IReadOnlyList<ConceptSummary> full, IReadOnlyList<ConceptSummary> names)
    {
        var results = full.ToList();
        var seen = full.Where(x => x.Score >= 0.55f)
            .Select(x => string.IsNullOrEmpty(x.Path) ? x.Id : x.Path)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var name in names.Where(x => x.Score >= 0.50f))
        {
            var key = string.IsNullOrEmpty(name.Path) ? name.Id : name.Path;
            if (!seen.Add(key)) continue;
            // 全文只有弱关键词命中时，不能挡住名称索引的补漏。
            results.RemoveAll(x => (string.IsNullOrEmpty(x.Path) ? x.Id : x.Path) == key);
            results.Add(new ConceptSummary
            {
                Id = name.Id, Path = name.Path, Name = name.Name, Type = name.Type,
                Description = name.Description, Media = name.Media,
                // 0.55 是全文向量通道的召回底线，名称补漏不压过过线的整句匹配。
                Score = Math.Min(name.Score, 0.55f),
                NameMatchScore = name.Score,
                TermScores = name.TermScores, FullQueryScore = name.FullQueryScore
            });
        }
        return results.OrderByDescending(x => x.Score).Take(30).ToList();
    }

    internal static bool CanAutoResolve(IReadOnlyList<ConceptSummary> candidates)
    {
        if (candidates.Count == 0) return false;
        var first = candidates[0];
        var phrase = first.PhraseScore;
        var gap = candidates.Count > 1 ? first.Score - candidates[1].Score : float.PositiveInfinity;
        // 混合分不是概率。需要整句向量 + 名称/ID 文字证据；单一低分候选也不能拍板。
        return phrase is { Vector: >= 0.72f, Keyword: >= 0.3f }
            && first.Score >= 0.80f && gap >= 0.10f;
    }
}
