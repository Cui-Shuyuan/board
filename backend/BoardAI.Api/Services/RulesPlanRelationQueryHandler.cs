using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// explain/condition/cost/target/ordering/boundary/identify/list 等共享实体解析路径。
/// 复杂 relation 的分页/读取/流程查询由专门处理器承担。
/// </summary>
internal sealed class RulesPlanRelationQueryHandler
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesSearchService _searchService;
    private readonly RulesPlanEntityResolver _entityResolver;
    private readonly RulesPlanResultBuilder _resultBuilder;

    public RulesPlanRelationQueryHandler(
        RulesConceptCatalog catalog,
        RulesSearchService searchService,
        RulesPlanEntityResolver entityResolver,
        RulesPlanResultBuilder resultBuilder)
    {
        _catalog = catalog;
        _searchService = searchService;
        _entityResolver = entityResolver;
        _resultBuilder = resultBuilder;
    }

    internal async Task<PlanItemResult> ExecuteAsync(
        string game,
        string relation,
        string entity,
        string intent,
        string question,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 外观识别保留完整描述。问题里出现通用名，不代表已识别具体组件。
        if (relation == "identify")
        {
            var exact = _entityResolver.ResolvePlanEntity(game, entity, out _, out var exactSource);
            if (exact.Count > 0 && exactSource == "exact_id")
            {
                return _resultBuilder.BuildOkResult(
                    game, relation, entity, exact, exactSource, question, cancellationToken,
                    retrievalQueries: new[] { entity });
            }

            var search = await _searchService.SearchConceptsAsync(
                game, entity, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (search.Results.Count == 0)
            {
                // 名称线索只能作为候选；不能替代颜色/位置等限定词。
                var hints = _entityResolver.ResolveFromQuestion(game, question, out _);
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

        if (relation == "list")
        {
            var catalog = _searchService.ListAllConceptIds(game, offset, limit);
            var version = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
                ? snapshot.Version
                : null;
            var data = new Dictionary<string, JsonElement>
            {
                ["total_count"] = JsonSerializer.SerializeToElement(catalog.TotalCount),
                ["offset"] = JsonSerializer.SerializeToElement(catalog.Offset),
                ["limit"] = JsonSerializer.SerializeToElement(catalog.Limit),
                ["has_more"] = JsonSerializer.SerializeToElement(catalog.HasMore)
            };
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
                        Content = JsonSerializer.SerializeToElement(catalog),
                        Data = data
                    }
                },
                RetrievalQueries = new List<string>()
            };
        }

        // 实体解析：精确 id → 精确名/别名/基名（直呼工具）→ 名称包含候选
        cancellationToken.ThrowIfCancellationRequested();
        var matched = _entityResolver.ResolvePlanEntity(game, entity, out var deterministicCandidates, out var source);
        cancellationToken.ThrowIfCancellationRequested();

        // 用户显式指定准确 id 时，保留其身份，不因 intent 检索把它替换成另一个对象。
        // 后续若需要该位置的设置/流程事实，由确认后的同一身份查询自然返回。
        if (matched.Count > 0 && source == "exact_id")
        {
            return _resultBuilder.BuildOkResult(
                game, relation, entity, matched, source, question, cancellationToken,
                retrievalQueries: new[] { entity });
        }

        var hasIntent = !string.IsNullOrWhiteSpace(intent);
        var exactIsProcessNode = matched.Any(item =>
        {
            var id = RulesTextUtils.GetElementId(item.Element);
            var type = _entityResolver.GetConceptTypeMap(game).GetValueOrDefault(id, "");
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
            .Select(item => _entityResolver.ToConceptSummary(game, item.Element, item.CanonicalPath))
            .Where(item => !string.IsNullOrEmpty(item.Id))
            .ToList();
        var retrievalQueries = new List<string>();
        if (!string.IsNullOrWhiteSpace(entity)) retrievalQueries.Add(entity);
        if (hasIntent && !string.Equals(entity.Trim(), intent.Trim(), StringComparison.Ordinal))
            retrievalQueries.Add(intent);

        List<ConceptSummary> entityCandidates = new();
        if (matched.Count == 0)
        {
            entityCandidates = await _entityResolver.RetrieveEntityCandidatesAsync(
                game, relation, entity, deterministicCandidates, cancellationToken);
        }

        List<ConceptSummary> intentCandidates = new();
        if (hasIntent)
        {
            intentCandidates = await _entityResolver.RetrieveIntentCandidatesAsync(
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
            var qhits = _entityResolver.ResolveFromQuestion(game, question, out _)
                .Where(hit => _entityResolver.IsExpectedPlanType(game, relation, RulesTextUtils.GetElementId(hit.Element)))
                .ToList();
            var mergedKeys = merged
                .Select(RulesSearchRanking.CandidateKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var hint in qhits)
            {
                var candidate = _entityResolver.ToConceptSummary(game, hint.Element, hint.CanonicalPath);
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
                && RulesPlanEntityResolver.NeedsIntentTargetConfirmation(intentCandidates, exactKeys);
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

            return _resultBuilder.BuildOkResult(
                game, relation, entity, matched, source, question, cancellationToken,
                retrievalQueries);
        }

        if (merged.Count > 0)
        {
            // 自动解析必须同时有整句语义和名称/ID 文字证据，阈值不因 intent 放宽。
            var autoTop = merged[0];
            if (RulesSearchRanking.CanAutoResolve(merged))
            {
                var autoMatched = RulesPlanEntityResolver.PreferGameIdentity(
                    autoTop.Id, _catalog.GetConceptsWithPaths(game, autoTop.Id).ToList());
                if (autoMatched.Count > 0)
                {
                    return _resultBuilder.BuildOkResult(
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
}
