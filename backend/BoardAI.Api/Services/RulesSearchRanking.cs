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
        IReadOnlyList<ConceptSummary> full, IReadOnlyList<ConceptSummary> names,
        float fullRecallThreshold = 0.55f)
    {
        var results = full.ToList();
        var seen = full.Where(x => x.Score >= fullRecallThreshold)
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
                // 名称补漏不能压过当前编码配置下过线的整句匹配。
                Score = Math.Min(name.Score, fullRecallThreshold),
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

    /// <summary>
    /// 合并“实体定位”和“完整 intent 场景召回”的候选。intent 是用户真正要解决的问题，
    /// 因此有 intent 证据时占主导；entity 分数只做召回补充与边界打平，不能靠多个泛词累加翻盘。
    /// 同一稳定身份只保留一项，并在 MatchReason 中保留分数来源。
    /// </summary>
    internal static List<ConceptSummary> MergeEntityAndIntentCandidates(
        IReadOnlyList<ConceptSummary> entityCandidates,
        IReadOnlyList<ConceptSummary> intentCandidates,
        IReadOnlyList<ConceptSummary> exactCandidates,
        IReadOnlyCollection<string> exactIdentityKeys,
        int limit = 15)
    {
        var entries = new Dictionary<string, CandidateEntry>(StringComparer.Ordinal);

        CandidateEntry GetEntry(ConceptSummary candidate)
        {
            var key = CandidateKey(candidate);
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new CandidateEntry { Key = key, Summary = Clone(candidate) };
                entries.Add(key, entry);
            }
            return entry;
        }

        foreach (var candidate in exactCandidates)
        {
            var entry = GetEntry(candidate);
            entry.Summary = Clone(candidate);
            entry.ExactIdentity = true;
        }

        foreach (var candidate in entityCandidates)
        {
            var entry = GetEntry(candidate);
            entry.EntityScore = Math.Max(entry.EntityScore, candidate.Score);
            if (entry.IntentSummary == null)
                entry.Summary = Clone(candidate);
        }

        foreach (var candidate in intentCandidates)
        {
            var entry = GetEntry(candidate);
            if (!entry.HasIntentScore || candidate.Score > entry.IntentScore)
            {
                entry.IntentScore = candidate.Score;
                entry.HasIntentScore = true;
            }
            // 完整 intent 的 PhraseScore 是 auto-resolve 唯一可审查的整句证据。
            entry.IntentSummary = candidate;
            entry.Summary = Clone(candidate);
        }

        foreach (var exactKey in exactIdentityKeys)
            if (entries.TryGetValue(exactKey, out var exactEntry))
                exactEntry.ExactIdentity = true;

        var results = new List<ConceptSummary>(entries.Count);
        foreach (var entry in entries.Values)
        {
            var summary = entry.Summary;
            float score;
            string reason;
            if (entry.HasIntentScore && entry.EntityScore > 0)
            {
                score = 0.8f * entry.IntentScore + 0.2f * entry.EntityScore;
                reason = "intent+entity";
            }
            else if (entry.HasIntentScore)
            {
                score = entry.IntentScore;
                reason = "intent";
            }
            else if (entry.EntityScore > 0)
            {
                // 没有 intent 证据时保留旧路径；只有 entity 的候选不能压过 intent 候选。
                score = 0.5f * entry.EntityScore;
                reason = "entity";
            }
            else
            {
                score = 0f;
                reason = "exact_identity";
            }

            summary.Score = MathF.Round(score, 4);
            summary.MatchReason = entry.ExactIdentity && reason != "intent+entity"
                ? "exact_identity"
                : reason;
            results.Add(summary);
        }

        return results
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => CandidateKey(candidate), StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>候选的稳定概念身份：局部槽位用 Path，普通概念用 Id。</summary>
    internal static string CandidateKey(ConceptSummary candidate) =>
        string.IsNullOrEmpty(candidate.Path) ? candidate.Id : candidate.Path;

    private sealed class CandidateEntry
    {
        public required string Key { get; init; }
        public required ConceptSummary Summary { get; set; }
        public float EntityScore { get; set; }
        public float IntentScore { get; set; }
        public bool HasIntentScore { get; set; }
        public ConceptSummary? IntentSummary { get; set; }
        public bool ExactIdentity { get; set; }
    }

    private static ConceptSummary Clone(ConceptSummary source) => new()
    {
        Id = source.Id,
        Path = source.Path,
        Name = source.Name,
        Type = source.Type,
        Score = source.Score,
        Description = source.Description,
        Media = source.Media,
        TermScores = source.TermScores,
        FullQueryScore = source.FullQueryScore,
        PhraseScore = source.PhraseScore,
        SubqueryBoost = source.SubqueryBoost,
        NameMatchScore = source.NameMatchScore,
        MatchReason = source.MatchReason
    };

}
