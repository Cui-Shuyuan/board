using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// execute_plan 的实体身份解析与候选召回协作类。只依赖目录、名称索引、检索与
/// 关系类型地图，不执行 relation 处理器，也不反向调用 RulesPlanService。
/// </summary>
internal sealed class RulesPlanEntityResolver
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesSearchService _searchService;
    private readonly RulesNameIndexService _nameIndex;
    private readonly VectorSearchService? _vectorSearch;

    public RulesPlanEntityResolver(
        RulesConceptCatalog catalog,
        RulesSearchService searchService,
        RulesNameIndexService nameIndex,
        VectorSearchService? vectorSearch)
    {
        _catalog = catalog;
        _searchService = searchService;
        _nameIndex = nameIndex;
        _vectorSearch = vectorSearch;
    }

    /// <summary>候选显示门槛。混合分只作排序，不作为自动解析的置信概率。</summary>
    private const float SemanticCandidateThreshold = 0.50f;

    /// <summary>游戏概念 id → type，用于 execute_plan 的 relation 级类型约束。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _conceptTypeMaps = new();


    /// <summary>特定 relation 下，实体最可能属于的 concept 类型。</summary>
    private static readonly Dictionary<string, string[]> PlanRelationExpectedTypes = new()
    {
        ["condition"] = new[] { "actions", "triggers", "conditions", "flow" },
        ["cost"] = new[] { "actions", "triggers", "flow", "objects" },
        ["target"] = new[] { "actions", "triggers", "flow" },
        ["rule_lookup"] = new[] { "actions", "triggers", "conditions", "flow", "objects" },
        ["compute"] = new[] { "actions", "triggers", "objects", "flow", "concepts" },
        ["ordering"] = new[] { "flow", "triggers", "actions" },
        ["boundary"] = new[] { "objects", "ontology", "concepts", "slot", "top_level_refs", "triggers", "conditions", "flow" },
    };


    internal Dictionary<string, string> GetConceptTypeMap(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game)
            return snapshot.GetOrAdd("concept-type-map", () => BuildConceptTypeMap(game));

        if (_conceptTypeMaps.TryGetValue(game, out var cached)) return cached;
        var map = BuildConceptTypeMap(game);
        _conceptTypeMaps[game] = map;
        return map;
    }

    private Dictionary<string, string> BuildConceptTypeMap(string game)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in _catalog.GetConceptTypes(game))
            foreach (var summary in _catalog.ListConcepts(game, type))
                if (!string.IsNullOrEmpty(summary.Id))
                    map[summary.Id] = type;
        return map;
    }


    internal bool IsExpectedPlanType(string game, string relation, string conceptId)
    {
        if (string.IsNullOrEmpty(conceptId)) return false;
        if (!PlanRelationExpectedTypes.TryGetValue(relation, out var expected)) return true;
        var map = GetConceptTypeMap(game);
        return map.TryGetValue(conceptId, out var type) && expected.Contains(type);
    }


    internal static bool NeedsIntentTargetConfirmation(
        IReadOnlyList<ConceptSummary> intentCandidates, IReadOnlyCollection<string> exactKeys)
    {
        var bestExactIntentScore = intentCandidates
            .Where(candidate => exactKeys.Contains(RulesSearchRanking.CandidateKey(candidate), StringComparer.Ordinal))
            .Select(candidate => candidate.Score)
            .DefaultIfEmpty(0f)
            .Max();
        return intentCandidates.Any(candidate =>
            !exactKeys.Contains(RulesSearchRanking.CandidateKey(candidate), StringComparer.Ordinal)
            && candidate.Score >= SemanticCandidateThreshold
            && candidate.Score >= bestExactIntentScore);
    }


    /// <summary>
    /// 实体名字/身份定位通道。保留原有 explain 全文+名称补漏和其他 relation 名称检索的差异。
    /// </summary>
    internal async Task<List<ConceptSummary>> RetrieveEntityCandidatesAsync(
        string game,
        string relation,
        string entity,
        List<ConceptSummary> deterministicCandidates,
        CancellationToken cancellationToken)
    {
        var merged = new List<ConceptSummary>(deterministicCandidates);
        if (_vectorSearch == null) return merged;

        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < merged.Count; i++) indexById[RulesSearchRanking.CandidateKey(merged[i])] = i;

        SearchConceptsResult semantic;
        if (relation == "explain")
        {
            var fullTask = _searchService.SearchConceptsAsync(game, entity, cancellationToken: cancellationToken);
            var nameTask = _searchService.SearchConceptsAsync(game, entity, searchMode: "name", cancellationToken: cancellationToken);
            await Task.WhenAll(fullTask, nameTask);
            semantic = await fullTask;
            semantic.Results = RulesSearchRanking.SupplementNames(
                semantic.Results, (await nameTask).Results, _vectorSearch.RecallThreshold);
        }
        else
        {
            semantic = await _searchService.SearchConceptsAsync(
                game, entity, searchMode: "name", cancellationToken: cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var semItems = semantic.Results
            .Where(r => r.Score >= SemanticCandidateThreshold)
            .OrderByDescending(r => r.Score)
            .ToList();
        semItems = ApplyRelationTypePreference(game, relation, semItems);

        foreach (var c in semItems)
        {
            var key = RulesSearchRanking.CandidateKey(c);
            if (indexById.TryGetValue(key, out var i))
            {
                if (c.Score > merged[i].Score) merged[i] = c;
            }
            else
            {
                indexById[key] = merged.Count;
                merged.Add(c);
                if (merged.Count >= 15) break;
            }
        }
        return merged.OrderByDescending(c => c.Score).ToList();
    }


    /// <summary>
    /// 完整 intent 场景/动作召回。始终走全文/整句检索，不使用短名称补漏通道，
    /// 避免设置、获得、外观等不同问题都被对象名压成同一条路径。
    /// </summary>
    internal async Task<List<ConceptSummary>> RetrieveIntentCandidatesAsync(
        string game,
        string relation,
        string intent,
        CancellationToken cancellationToken)
    {
        var semantic = await _searchService.SearchConceptsAsync(
            game, intent, searchMode: "full", cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = semantic.Results
            .Where(r => r.Score >= SemanticCandidateThreshold)
            .OrderByDescending(r => r.Score)
            .ToList();
        candidates = ApplyRelationTypePreference(game, relation, candidates);
        foreach (var candidate in candidates)
            candidate.MatchReason = "intent";
        return candidates.Take(15).ToList();
    }


    internal List<ConceptSummary> ApplyRelationTypePreference(
        string game,
        string relation,
        List<ConceptSummary> candidates)
    {
        if (candidates.Count == 0) return candidates;
        var expected = candidates.Where(c => IsExpectedPlanType(game, relation, c.Id)).ToList();
        // 没有符合类型的候选时保留原候选，避免把可用召回误杀。
        return expected.Count > 0 ? expected : candidates;
    }


    internal ConceptSummary ToConceptSummary(string game, JsonElement element, string? canonicalPath = null)
    {
        var id = RulesTextUtils.GetElementId(element);
        var name = "";
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("name", out var nameProp)
            && nameProp.ValueKind == JsonValueKind.Object
            && nameProp.TryGetProperty("zh", out var zh))
            name = zh.GetString() ?? "";
        var type = GetConceptTypeMap(game).TryGetValue(id, out var foundType) ? foundType : "";
        return new ConceptSummary
        {
            Id = id,
            Path = string.IsNullOrEmpty(canonicalPath) ? id : canonicalPath,
            Name = string.IsNullOrEmpty(name) ? id : name,
            Type = type,
            Description = RulesTextUtils.ExtractDescriptionZh(element)
        };
    }


    internal static string NormalizeConceptRef(string reference)
        => (reference ?? "").Trim().TrimStart('<').TrimEnd('>').Trim();

    internal static string ExtractConceptName(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.Object
            && name.TryGetProperty("zh", out var zh)
            && zh.ValueKind == JsonValueKind.String)
            return zh.GetString() ?? "";
        return RulesTextUtils.GetElementId(element);
    }

    internal List<ConceptPathHit> ResolvePlanEntity(
        string game, string entity, out List<ConceptSummary> candidates, out string source)
    {
        candidates = new List<ConceptSummary>();
        source = "";
        entity = entity.Trim();
        if (string.IsNullOrEmpty(entity)) return new List<ConceptPathHit>();

        var byId = PreferGameIdentity(
            entity, _catalog.GetConceptsWithPaths(game, entity).ToList());
        if (byId.Count > 0)
        {
            source = "exact_id";
            return byId;
        }

        // 精确匹配：中文名 / 英文名 / 别名 / 基名（大小写不敏感；覆盖 flow 节点与实例）。
        // 「名词直呼」工具：客人按名字提到概念时，程序直接确定检索目标，不走向量。
        // 一个键可映射多个概念（基名「家庭成长」→ 需/无需房间两个行动），多命中全部返回。
        var lookup = _nameIndex.GetExactLookup(game);
        if (lookup.TryGetValue(entity, out var hits))
        {
            var exact = new List<ConceptPathHit>();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in hits)
                foreach (var pathHit in PreferGameIdentity(h.Id, _catalog.GetConceptsWithPaths(game, h.Id).ToList()))
                    if (seenPaths.Add(pathHit.CanonicalPath))
                        exact.Add(pathHit);
            if (exact.Count > 0)
            {
                source = hits[0].Kind;
                return exact;
            }
        }

        // 名称包含 → 唯一则直接解析，多个则返回候选供消歧
        var map = _nameIndex.GetNameMap(game);
        var containing = map.Where(kv => kv.Value.Contains(entity, StringComparison.Ordinal)).Take(6).ToList();
        if (containing.Count == 1)
        {
            source = "contain_unique";
            return PreferGameIdentity(
                containing[0].Key, _catalog.GetConceptsWithPaths(game, containing[0].Key).ToList());
        }

        candidates = containing
            .Select(kv => new ConceptSummary { Id = kv.Key, Path = kv.Key, Name = kv.Value })
            .ToList();
        return new List<ConceptPathHit>();
    }

    /// <summary>
    /// 无命名空间时游戏层身份优先；只有游戏层没有该 id 时才回退到本体。
    /// 显式 ontology:: 查询仍只返回本体，保证两个同名 id 不会互相命中。
    /// </summary>
    internal static List<ConceptPathHit> PreferGameIdentity(string requested, List<ConceptPathHit> hits)
    {
        if (hits.Count == 0 || requested.StartsWith("ontology::", StringComparison.Ordinal))
            return hits;
        var gameHits = hits
            .Where(hit => !hit.CanonicalPath.StartsWith("ontology::", StringComparison.Ordinal))
            .ToList();
        return gameHits.Count > 0 ? gameHits : hits;
    }


    /// <summary>
    /// 「问题级直呼」（广播第二站）：实体解析失败时，直接扫客人问题原文——命中的
    /// 概念名/别名/基名即拍板，避免 LLM 转述失败造成漏检（2026-08-16 QA：C 类
    /// 10 题的查询几乎全是转述失败，如「谷物播种」「开局食物」「农场空格」）。
    /// 最长名字优先 + 区间不重叠；命中概念去重后最多返回 MaxQuestionHits 个。
    /// </summary>
    private const int MaxQuestionHits = 4;


    internal List<ConceptPathHit> ResolveFromQuestion(string game, string question, out string source)
    {
        source = "";
        if (string.IsNullOrWhiteSpace(question)) return new List<ConceptPathHit>();

        var lookup = _nameIndex.GetExactLookup(game);

        // 收集所有命中区间（key, 起点, 长度）
        var spans = new List<(string Key, int Start, int Len)>();
        foreach (var (key, _) in lookup)
        {
            var idx = question.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                spans.Add((key, idx, key.Length));
                idx = question.IndexOf(key, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (spans.Count == 0) return new List<ConceptPathHit>();

        spans.Sort((a, b) => a.Len != b.Len ? b.Len.CompareTo(a.Len) : a.Start.CompareTo(b.Start));

        var takenUntil = -1;
        var result = new List<ConceptPathHit>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, start, len) in spans)
        {
            if (start < takenUntil) continue; // 被更长的名字覆盖（如「家庭成员」盖住「成员」）
            takenUntil = start + len;
            foreach (var h in lookup[key])
            {
                foreach (var pathHit in PreferGameIdentity(h.Id, _catalog.GetConceptsWithPaths(game, h.Id).ToList()))
                {
                    if (!seenPaths.Add(pathHit.CanonicalPath)) continue;
                    result.Add(pathHit);
                    if (result.Count >= MaxQuestionHits)
                    {
                        source = "question_hit";
                        return result;
                    }
                }
            }
        }

        if (result.Count > 0) source = "question_hit";
        return result;
    }


    /// <summary>清空 relation 类型缓存。</summary>
    public void Clear()
    {
        _conceptTypeMaps.Clear();
    }
}
