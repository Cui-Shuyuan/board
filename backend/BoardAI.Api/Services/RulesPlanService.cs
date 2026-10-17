using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 查询计划协作类：承载 execute_plan 的实体解析、relation 导航与结果构造。
/// 自身只缓存 relation 类型映射；规则文档变化时由调用方 Clear。
/// </summary>
public sealed class RulesPlanService
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesSearchService _searchService;
    private readonly RulesNameIndexService _nameIndex;
    private readonly RulesFlowService _flowService;
    private readonly RulesReferenceService _referenceService;
    private readonly RulesFactService _factService;
    private readonly VectorSearchService? _vectorSearch;

    public RulesPlanService(
        RulesConceptCatalog catalog,
        RulesSearchService searchService,
        RulesNameIndexService nameIndex,
        RulesFlowService flowService,
        RulesReferenceService referenceService,
        RulesFactService factService,
        VectorSearchService? vectorSearch)
    {
        _catalog = catalog;
        _searchService = searchService;
        _nameIndex = nameIndex;
        _flowService = flowService;
        _referenceService = referenceService;
        _factService = factService;
        _vectorSearch = vectorSearch;
    }


    /// <summary>游戏概念 id → type，用于 execute_plan 的 relation 级类型约束。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _conceptTypeMaps = new();


    /// <summary>特定 relation 下，实体最可能属于的 concept 类型。</summary>
    private static readonly Dictionary<string, string[]> PlanRelationExpectedTypes = new()
    {
        ["condition"] = new[] { "actions", "triggers", "conditions", "flow" },
        ["ordering"] = new[] { "flow", "triggers", "actions" },
        ["boundary"] = new[] { "objects", "ontology", "concepts", "slot", "top_level_refs", "triggers", "conditions", "flow" },
    };


    private Dictionary<string, string> GetConceptTypeMap(string game)
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


    private bool IsExpectedPlanType(string game, string relation, string conceptId)
    {
        if (string.IsNullOrEmpty(conceptId)) return false;
        if (!PlanRelationExpectedTypes.TryGetValue(relation, out var expected)) return true;
        var map = GetConceptTypeMap(game);
        return map.TryGetValue(conceptId, out var type) && expected.Contains(type);
    }


    /// <summary>清空 relation 类型缓存。</summary>
    public void Clear()
    {
        _conceptTypeMaps.Clear();
    }


    // ---- 查询计划（execute_plan）----

    /// <summary>
    /// 执行 LLM 提交的查询计划。各 relation 均为确定性数据导航：
    /// explain/condition/ordering/boundary 按实体解析（精确 id → 精确名/别名/基名 →
    /// 问题原文直呼 → 名称包含候选）；identify 按外观/位置描述搜索候选；
    /// flow/list 不需要实体。question 为客人问题原文——实体解析失败时程序直接扫原文
    /// 找概念名（「广播」第二站），不依赖 LLM 的转述质量。
    /// </summary>
    public async Task<PlanExecutionResult> ExecutePlanAsync(
        string game,
        JsonElement plan,
        string question = "",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new PlanExecutionResult
        {
            RequestId = "plan_" + Guid.NewGuid().ToString("N")[..12],
            Game = game,
            RulesVersion = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
                ? snapshot.Version
                : null
        };
        if (!plan.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Array)
        {
            result.Note = "plan 缺少 queries 数组。";
            return result;
        }

        var queryItems = queries.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .ToList();

        for (var queryIndex = 0; queryIndex < queryItems.Count; queryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var q = queryItems[queryIndex];
            var relation = q.TryGetProperty("relation", out var rp) ? rp.GetString() ?? "" : "";
            var entity = q.TryGetProperty("entity", out var ep) ? ep.GetString() ?? "" : "";
            var explicitIntent = q.TryGetProperty("intent", out var ip) ? ip.GetString() ?? "" : "";
            explicitIntent = explicitIntent.Trim();

            // 展示/日志用 intent：单一子问题缺失时回填客人原问句，方便观察；
            // 但检索只使用模型显式提供的 intent。缺失项保守回到原有 entity 路径，
            // 多子问题更不能把整段问题不加区分地套到每个实体上。
            var displayIntent = explicitIntent;
            if (string.IsNullOrWhiteSpace(displayIntent)
                && queryItems.Count == 1
                && !string.IsNullOrWhiteSpace(question))
                displayIntent = question.Trim();

            var item = await ExecutePlanQueryAsync(
                game, relation, entity, explicitIntent, question, cancellationToken);
            item.QueryIndex = queryIndex;
            item.QueryId = $"{result.RequestId}:{queryIndex}";
            item.RulesVersion = result.RulesVersion;
            item.Intent = displayIntent;
            NormalizeResultContract(item, result.RulesVersion);
            result.Results.Add(item);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (result.Results.Any(r => r.Status is "unresolved" or "unsupported" or "no_match"))
        {
            var notes = new List<string>();
            if (result.Results.Any(r => r.Status == "unresolved"))
                notes.Add("unresolved 的候选描述只用于选择目标，不是已确认事实；请用候选中的确切 id 或中文名、保持原 relation 和 intent 重新查询");
            if (result.Results.Any(r => r.Status == "no_match"))
                notes.Add("no_match 表示规则库未确认到该查询的信息，请明确说明未查到，不要编造规则");
            if (result.Results.Any(r => r.Status == "unsupported"))
                notes.Add("unsupported 的 relation 请改用 explain/condition/ordering/boundary/flow/list/identify");
            result.Note = "部分查询未完成：" + string.Join("；", notes) + "。";
        }
        return result;
    }


    private static readonly HashSet<string> PlanRelations = new(StringComparer.Ordinal)
    {
        "explain", "condition", "ordering", "boundary", "flow", "list", "identify"
    };


    /// <summary>候选显示门槛。混合分只作排序，不作为自动解析的置信概率。</summary>
    private const float SemanticCandidateThreshold = 0.50f;


    private async Task<PlanItemResult> ExecutePlanQueryAsync(
        string game,
        string relation,
        string entity,
        string intent,
        string question,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!PlanRelations.Contains(relation))
        {
            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "unsupported",
                Resolution = QueryResolutionStatuses.Unsupported,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "该 relation 不在支持列表（explain/condition/ordering/boundary/flow/list/identify）。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.UnsupportedRelation, FieldPath = "relation" }
                }
            };
        }

        // 外观识别保留完整描述。问题里出现通用名，不代表已识别具体组件。
        if (relation == "identify")
        {
            var exact = ResolvePlanEntity(game, entity, out _, out var exactSource);
            if (exact.Count > 0 && exactSource == "exact_id")
            {
                return BuildOkResult(
                    game, relation, entity, exact, exactSource, question, cancellationToken,
                    retrievalQueries: new[] { entity });
            }

            var search = await _searchService.SearchConceptsAsync(
                game, entity, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (search.Results.Count == 0)
            {
                // 名称线索只能作为候选；不能替代颜色/位置等限定词。
                var hints = ResolveFromQuestion(game, question, out _);
                foreach (var hint in hints)
                    search.Results.Add(new ConceptSummary
                    {
                        Id = RulesTextUtils.GetElementId(hint.Element),
                        Path = hint.CanonicalPath,
                        Name = hint.Element.TryGetProperty("name", out var hintName) && hintName.TryGetProperty("zh", out var hintZh)
                            ? hintZh.GetString() ?? "" : RulesTextUtils.GetElementId(hint.Element),
                        Description = RulesTextUtils.ExtractDescriptionZh(hint.Element)
                    });
            }
            return new PlanItemResult
            {
                Relation = relation, Entity = entity,
                Status = search.Results.Count > 0 ? "unresolved" : "no_match",
                Resolution = search.Results.Count > 0
                    ? QueryResolutionStatuses.Candidates
                    : QueryResolutionStatuses.NotFound,
                Coverage = QueryCoverageStatuses.Unknown,
                RetrievalQueries = new List<string> { entity },
                Candidates = search.Results,
                Message = "按完整描述检索的候选概念（含定义与匹配分数）。请核对颜色、形状和位置，再用确切 id 发起 explain；通用概念命中不能证明已识别客人描述的组件。",
                Omissions =
                {
                    new QueryOmission
                    {
                        Reason = search.Results.Count > 0
                            ? QueryOmissionReasons.AmbiguousIdentity
                            : QueryOmissionReasons.NoRuleCoverage,
                        FieldPath = "entity"
                    }
                }
            };
        }

        // flow/list 不需要实体解析
        if (relation == "flow")
        {
            var gameConcept = _catalog.GetConceptsWithPaths(game, "game").ToList();
            if (gameConcept.Count == 0)
            {
                return new PlanItemResult
                {
                    Relation = relation,
                    Entity = entity,
                    Status = "no_match",
                    Resolution = QueryResolutionStatuses.NotFound,
                    Coverage = QueryCoverageStatuses.Unknown,
                    Message = "规则库中没有该游戏的流程定义，无法返回任何流程数据。请向客人说明这部分规则未被规则库覆盖，不要自行补写规则。",
                    Omissions =
                    {
                        new QueryOmission { Reason = QueryOmissionReasons.NoRuleCoverage, FieldPath = "flow" }
                    }
                };
            }

            var version = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
                ? snapshot.Version
                : null;
            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "ok",
                Resolution = QueryResolutionStatuses.Exact,
                Coverage = QueryCoverageStatuses.Complete,
                Source = "exact_id",
                Matched = gameConcept.Select(hit => hit.Element).ToList(),
                ResolvedTargets = gameConcept.Select(hit => hit.CanonicalPath).ToList(),
                Payloads = gameConcept.Select(hit => new QueryPayload
                {
                    Type = QueryPayloadTypes.Definition,
                    CanonicalPath = hit.CanonicalPath,
                    RulesVersion = version,
                    Completeness = QueryCoverageStatuses.Complete,
                    Content = hit.Element.Clone()
                }).ToList(),
                RetrievalQueries = new List<string>()
            };
        }
        if (relation == "list")
        {
            var catalog = _searchService.ListAllConceptIds(game);
            var version = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
                ? snapshot.Version
                : null;
            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "ok",
                Resolution = QueryResolutionStatuses.Exact,
                Coverage = QueryCoverageStatuses.Complete,
                Source = "exact_id",
                Catalog = catalog,
                Payloads =
                {
                    new QueryPayload
                    {
                        Type = QueryPayloadTypes.CatalogPage,
                        CanonicalPath = "catalog",
                        RulesVersion = version,
                        Completeness = QueryCoverageStatuses.Complete,
                        Content = JsonSerializer.SerializeToElement(catalog)
                    }
                },
                RetrievalQueries = new List<string>()
            };
        }

        // 实体解析：精确 id → 精确名/别名/基名（直呼工具）→ 名称包含候选
        cancellationToken.ThrowIfCancellationRequested();
        var matched = ResolvePlanEntity(game, entity, out var deterministicCandidates, out var source);
        cancellationToken.ThrowIfCancellationRequested();

        // 用户显式指定准确 id 时，保留其身份，不因 intent 检索把它替换成另一个对象。
        // 后续若需要该位置的设置/流程事实，由确认后的同一身份查询自然返回。
        if (matched.Count > 0 && source == "exact_id")
        {
            return BuildOkResult(
                game, relation, entity, matched, source, question, cancellationToken,
                retrievalQueries: new[] { entity });
        }

        var hasIntent = !string.IsNullOrWhiteSpace(intent);
        var exactIsProcessNode = matched.Any(item =>
        {
            var id = RulesTextUtils.GetElementId(item.Element);
            var type = GetConceptTypeMap(game).GetValueOrDefault(id, "");
            return type is "actions" or "triggers" or "conditions" or "flow";
        });
        // 对象名精确命中时仍要补动作/流程；流程/动作/触发器名精确命中时，
        // 它本身就是用户要的规则节点，不应被宽泛 intent 再次打散。
        var shouldSupplementExactIdentity = hasIntent && !exactIsProcessNode;
        var exactKeys = matched
            .Select(item => RulesTextUtils.GetElementId(item.Element))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var exactCandidates = matched
            .Select(item => ToConceptSummary(game, item.Element, item.CanonicalPath))
            .Where(item => !string.IsNullOrEmpty(item.Id))
            .ToList();
        var retrievalQueries = new List<string>();
        if (!string.IsNullOrWhiteSpace(entity)) retrievalQueries.Add(entity);
        if (hasIntent && !string.Equals(entity.Trim(), intent.Trim(), StringComparison.Ordinal))
            retrievalQueries.Add(intent);

        List<ConceptSummary> entityCandidates = new();
        if (matched.Count == 0)
        {
            entityCandidates = await RetrieveEntityCandidatesAsync(
                game, relation, entity, deterministicCandidates, cancellationToken);
        }

        List<ConceptSummary> intentCandidates = new();
        if (hasIntent)
        {
            intentCandidates = await RetrieveIntentCandidatesAsync(
                game, relation, intent, cancellationToken);
        }

        List<ConceptSummary> merged;
        if (hasIntent)
        {
            // intent 是“用户要知道什么”的完整场景证据；entity 仅用于身份召回与打平。
            merged = RulesSearchRanking.MergeEntityAndIntentCandidates(
                entityCandidates,
                intentCandidates,
                exactCandidates,
                exactKeys);
        }
        else if (matched.Count == 0)
        {
            // 无 intent 时保持既有实体检索路径，避免改变已有 gold 行为。
            merged = entityCandidates;
        }
        else
        {
            merged = new List<ConceptSummary>();
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 问题中的名称只能补充上下文候选，不能证明查询实体已被识别。
        // 例如未知能力的问句提到“模组”，不能把未知能力确认成某个模组。
        if (matched.Count == 0 && (merged.Count == 0 || merged[0].Score < 0.55f))
        {
            var qhits = ResolveFromQuestion(game, question, out _)
                .Where(hit => IsExpectedPlanType(game, relation, RulesTextUtils.GetElementId(hit.Element)))
                .ToList();
            var mergedKeys = merged
                .Select(RulesSearchRanking.CandidateKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var hint in qhits)
            {
                var candidate = ToConceptSummary(game, hint.Element, hint.CanonicalPath);
                if (string.IsNullOrEmpty(candidate.Id) || !mergedKeys.Add(RulesSearchRanking.CandidateKey(candidate)))
                    continue;
                candidate.MatchReason = "question_hint";
                merged.Add(candidate);
            }
            merged = merged.OrderByDescending(c => c.Score).Take(15).ToList();
        }

        if (matched.Count > 0)
        {
            // 依据完整意图的检索结果判断是否仍有目标歧义；不从句长推断语义，
            // 也不因名字精确命中而抹去该对象的语义相关分数。
            var hasScenarioCandidate = shouldSupplementExactIdentity
                && NeedsIntentTargetConfirmation(intentCandidates, exactKeys);
            if (hasScenarioCandidate)
            {
                // 名字精确命中对象，不等于该对象定义足以回答用户的设置/动作/获得问题。
                // 返回候选并让模型用准确 id 再确认一次，候选描述不能当正式事实使用。
                return new PlanItemResult
                {
                    Relation = relation,
                    Entity = entity,
                    Status = "unresolved",
                    Resolution = QueryResolutionStatuses.Candidates,
                    Coverage = QueryCoverageStatuses.Unknown,
                    RetrievalQueries = retrievalQueries,
                    Candidates = merged,
                    Message = "实体名称已有命中，但完整 intent 保留了对象之外的动作/场景疑点，且检索指向了其他规则节点。候选描述只用于选择目标：若其中某项直接回答 intent，请用它的准确 id、保持原 relation 和 intent 再查询一次以获取正式事实。",
                    Omissions =
                    {
                        new QueryOmission { Reason = QueryOmissionReasons.AmbiguousIdentity, FieldPath = "target" }
                    }
                };
            }

            return BuildOkResult(
                game, relation, entity, matched, source, question, cancellationToken,
                retrievalQueries);
        }

        if (merged.Count > 0)
        {
            // 自动解析必须同时有整句语义和名称/ID 文字证据，阈值不因 intent 放宽。
            var autoTop = merged[0];
            if (RulesSearchRanking.CanAutoResolve(merged))
            {
                var autoMatched = PreferGameIdentity(
                    autoTop.Id, _catalog.GetConceptsWithPaths(game, autoTop.Id).ToList());
                if (autoMatched.Count > 0)
                {
                    return BuildOkResult(
                        game, relation, entity, autoMatched, "auto_semantic", question,
                        cancellationToken, retrievalQueries);
                }
            }

            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "unresolved",
                Resolution = QueryResolutionStatuses.Candidates,
                Coverage = QueryCoverageStatuses.Unknown,
                RetrievalQueries = retrievalQueries,
                Candidates = merged,
                Message = "实体未精确命中——程序结合名称定位与完整 intent 检索出以下候选（含分数与来源）。候选描述只用于选择目标；请从中挑选真正回答 intent 的概念，用它的 id 或中文名、保持原 relation 和 intent 重新发起查询。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.AmbiguousIdentity, FieldPath = "target" }
                }
            };
        }

        return new PlanItemResult
        {
            Relation = relation,
            Entity = entity,
            Status = "no_match",
            Resolution = QueryResolutionStatuses.NotFound,
            Coverage = QueryCoverageStatuses.Unknown,
            RetrievalQueries = retrievalQueries,
            Message = $"程序未能在规则库中找到与「{entity}」足够相近的概念，本查询无法获得任何规则数据。请先尝试：一、用 list 浏览概念目录确认是否真的没有相近概念（也许名字不同）；二、用 identify 按外观或位置描述再找一次。若确认规则库中没有此规则事实，请向客人说明未查到，不要基于记忆补写规则。",
            Omissions =
            {
                new QueryOmission { Reason = QueryOmissionReasons.NoRuleCoverage, FieldPath = "entity" }
            }
        };
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
    private async Task<List<ConceptSummary>> RetrieveEntityCandidatesAsync(
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
    private async Task<List<ConceptSummary>> RetrieveIntentCandidatesAsync(
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


    private List<ConceptSummary> ApplyRelationTypePreference(
        string game,
        string relation,
        List<ConceptSummary> candidates)
    {
        if (candidates.Count == 0) return candidates;
        var expected = candidates.Where(c => IsExpectedPlanType(game, relation, c.Id)).ToList();
        // 没有符合类型的候选时保留原候选，避免把可用召回误杀。
        return expected.Count > 0 ? expected : candidates;
    }


    private ConceptSummary ToConceptSummary(string game, JsonElement element, string? canonicalPath = null)
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


    /// <summary>
    /// 把已解析的概念构造成 ok 结果：引用扩展数据 + 流程位置 + 关系专属字段。
    /// 精确解析、问题级直呼与「高置信语义候选自动解析」共用此路径（2026-08-16 提取）。
    /// 多命中（基名/别名/问题直呼）时 Matched 返回全部命中概念，Related 为各概念
    /// 直接引用的并集——多概念共享的事实（如播种与谷物）一次性给全。
    /// </summary>
    private PlanItemResult BuildOkResult(
        string game,
        string relation,
        string entity,
        List<ConceptPathHit> matched,
        string source,
        string question,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? retrievalQueries = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matchedElements = matched.Select(hit => hit.Element).ToList();
        var resolvedId = RulesTextUtils.GetElementId(matchedElements[0]);
        var rulesVersion = RulesSnapshotScope.Current is { } scope && scope.Game == game
            ? scope.Version
            : null;

        var item = new PlanItemResult
        {
            Relation = relation,
            Entity = entity,
            Status = "ok",
            Resolution = QueryResolutionStatuses.Exact,
            Source = source,
            ResolvedTargets = matched.Select(hit => hit.CanonicalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Coverage = QueryCoverageStatuses.Complete,
            Matched = matchedElements,
            Related = _referenceService.ExpandRelated(game, matchedElements, light: source == "question_hit"),
            RetrievalQueries = retrievalQueries?.ToList() ?? new List<string>()
        };

        // 每个规范路径都交付一个完整定义负载；正文不再从 matched 再复制一次给模型。
        foreach (var hit in matched)
        {
            item.Payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.Definition,
                CanonicalPath = hit.CanonicalPath,
                RulesVersion = rulesVersion,
                Completeness = QueryCoverageStatuses.Complete,
                DefinitionSource = source,
                Content = hit.Element.Clone()
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var localId = resolvedId.Contains("::") ? resolvedId[(resolvedId.IndexOf("::", StringComparison.Ordinal) + 2)..] : resolvedId;
        _flowService.GetFlowPositions(game).TryGetValue(localId, out var pos);
        cancellationToken.ThrowIfCancellationRequested();

        // 流程位置链（flow 节点才有）：回答「在哪个阶段/回合」类语境
        if (pos != null)
            item.FlowContext = pos.Ancestors;

        // condition 关系：额外提取「能不能」答案所需的三要素——条件谓词、费用、目标约束
        if (relation == "condition" && matched.Count > 0)
        {
            var el = matched[0].Element;
            item.Condition = ExtractTopField(el, "<ontology::condition>");
            item.Cost = ExtractTopField(el, "<ontology::cost>");
            item.Target = ExtractTopField(el, "target");
            var fields = new Dictionary<string, JsonElement>();
            if (item.Condition.HasValue) fields["condition"] = item.Condition.Value;
            if (item.Cost.HasValue) fields["cost"] = item.Cost.Value;
            if (item.Target.HasValue) fields["target"] = item.Target.Value;
            item.Payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.RelationFacts,
                CanonicalPath = matched[0].CanonicalPath,
                RulesVersion = rulesVersion,
                Completeness = fields.Count > 0 ? QueryCoverageStatuses.Complete : QueryCoverageStatuses.Partial,
                Data = fields.Count > 0 ? fields : null
            });
            if (fields.Count == 0)
            {
                item.Coverage = QueryCoverageStatuses.Partial;
                item.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.FieldMissing,
                    CanonicalPath = matched[0].CanonicalPath,
                    FieldPath = "condition|cost|target"
                });
            }
        }

        // ordering 关系：同级选项顺序 + 位置 + 循环结构（回答「之后是什么/何时结束」）
        if (relation == "ordering")
        {
            if (pos != null)
            {
                item.Siblings = pos.Siblings;
                item.PositionIndex = pos.Index;
                item.Loop = pos.Loop;
                var fields = new Dictionary<string, JsonElement>();
                if (pos.Siblings.Count > 0)
                    fields["siblings"] = JsonSerializer.SerializeToElement(pos.Siblings);
                if (pos.Index >= 0)
                    fields["position_index"] = JsonSerializer.SerializeToElement(pos.Index);
                if (pos.Loop.HasValue)
                    fields["loop"] = pos.Loop.Value.Clone();
                item.Payloads.Add(new QueryPayload
                {
                    Type = QueryPayloadTypes.RelationFacts,
                    CanonicalPath = matched[0].CanonicalPath,
                    RulesVersion = rulesVersion,
                    Completeness = QueryCoverageStatuses.Complete,
                    Data = fields
                });
            }
            else
            {
                item.Coverage = QueryCoverageStatuses.Partial;
                item.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.FieldMissing,
                    CanonicalPath = matched[0].CanonicalPath,
                    FieldPath = "flow_position"
                });
            }
        }

        // boundary 关系：溢出/下溢/圈事件等边界字段 + 缺省语义说明
        if (relation == "boundary" && matched.Count > 0)
        {
            var el = matched[0].Element;
            var boundary = new Dictionary<string, JsonElement>();
            foreach (var key in new[]
                     {
                         "<ontology::overflow_compensation>", "<ontology::underflow_compensation>",
                         "<ontology::lap_event>", "capacity", "loop"
                     })
            {
                var v = ExtractTopField(el, key);
                if (v.HasValue) boundary[key] = v.Value;
            }
            item.Boundary = boundary.Count > 0 ? boundary : null;
            item.Payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.RelationFacts,
                CanonicalPath = matched[0].CanonicalPath,
                RulesVersion = rulesVersion,
                Completeness = QueryCoverageStatuses.Complete,
                Data = boundary
            });
            item.Message = "未声明溢出/下溢补偿的轨道：缺省语义=该方向无事发生或该方向不会发生（见 ontology <ontology::overflow_compensation> 定义）。";
        }

        // 事实卡（广播第三站）：数量/计分题的程序化计算——容量公式求值、计分表区间查表、
        // quantity.numeric 分支选择与求和。程序算得出的数不交给 LLM 从散文里读（1+1 交给计算器）。
        // 问题不含数量词/分数词、或命中概念无结构化数据时返回 null，序列化时省略。
        if (!string.IsNullOrWhiteSpace(question))
            item.Facts = _factService.ExtractFacts(game, matchedElements, question);
        if (item.Facts is { Count: > 0 })
        {
            foreach (var fact in item.Facts)
            {
                item.Payloads.Add(new QueryPayload
                {
                    Type = QueryPayloadTypes.ComputedFact,
                    CanonicalPath = matched[0].CanonicalPath,
                    RulesVersion = rulesVersion,
                    Completeness = QueryCoverageStatuses.Complete,
                    Content = fact.Clone()
                });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return item;
    }


    private List<ConceptPathHit> ResolvePlanEntity(
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
    private static List<ConceptPathHit> PreferGameIdentity(string requested, List<ConceptPathHit> hits)
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


    private List<ConceptPathHit> ResolveFromQuestion(string game, string question, out string source)
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

    private static readonly HashSet<string> KnownPayloadTypes = new(StringComparer.Ordinal)
    {
        QueryPayloadTypes.Definition,
        QueryPayloadTypes.CatalogPage,
        QueryPayloadTypes.FlowSlice,
        QueryPayloadTypes.RelationFacts,
        QueryPayloadTypes.ComputedFact
    };

    /// <summary>
    /// 在结果离开发起服务前补齐 resolution/coverage，并拒绝未知 payload 静默成功。
    /// 这是内部契约归一化，不改变已有检索决策。
    /// </summary>
    private static void NormalizeResultContract(PlanItemResult item, string? rulesVersion)
    {
        item.RulesVersion ??= rulesVersion;
        if (string.IsNullOrWhiteSpace(item.QueryId))
            item.QueryId = "query_" + Guid.NewGuid().ToString("N")[..12];

        foreach (var payload in item.Payloads)
            payload.RulesVersion ??= item.RulesVersion;

        var unknownPayload = item.Payloads.FirstOrDefault(payload => !KnownPayloadTypes.Contains(payload.Type));
        if (unknownPayload != null)
        {
            item.Status = "unsupported";
            item.Resolution = QueryResolutionStatuses.Unsupported;
            item.Coverage = QueryCoverageStatuses.Unknown;
            item.Payloads.Clear();
            item.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.UnsupportedRelation,
                CanonicalPath = unknownPayload.CanonicalPath,
                FieldPath = "payload.type"
            });
            item.Message = "程序返回了消费者不认识的 payload 类型，已按 unsupported 处理。";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Resolution))
        {
            item.Resolution = item.Status switch
            {
                "ok" => QueryResolutionStatuses.Exact,
                "unresolved" => QueryResolutionStatuses.Candidates,
                "unsupported" => QueryResolutionStatuses.Unsupported,
                "no_match" => QueryResolutionStatuses.NotFound,
                _ => QueryResolutionStatuses.Invalid
            };
        }

        if (item.Resolution == QueryResolutionStatuses.Exact)
        {
            item.Candidates = null;
            if (item.Payloads.Count == 0 && item.Status == "ok")
            {
                item.Resolution = QueryResolutionStatuses.Invalid;
                item.Status = "unsupported";
                item.Coverage = QueryCoverageStatuses.Unknown;
                item.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.ContinuationRequired,
                    FieldPath = "payloads"
                });
            }
        }

        if (item.Payloads.Count > 0)
        {
            var payloadPaths = item.Payloads
                .Select(payload => payload.CanonicalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var path in payloadPaths)
                if (!item.ResolvedTargets.Contains(path, StringComparer.Ordinal))
                    item.ResolvedTargets.Add(path);

            if (item.Coverage != QueryCoverageStatuses.Partial)
            {
                item.Coverage = item.Payloads.All(payload =>
                        payload.Completeness == QueryCoverageStatuses.Complete)
                    ? QueryCoverageStatuses.Complete
                    : QueryCoverageStatuses.Partial;
            }
        }
        else if (string.IsNullOrWhiteSpace(item.Coverage))
        {
            item.Coverage = item.Resolution == QueryResolutionStatuses.Exact
                ? QueryCoverageStatuses.Partial
                : QueryCoverageStatuses.Unknown;
        }
    }

    /// <summary>提取概念顶层的指定字段（无则 null）。</summary>
    private static JsonElement? ExtractTopField(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v)
            ? v
            : null;
    }

}
