using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// execute_plan 的薄编排入口：解析计划、按原顺序执行查询、分配 query identity、
/// 绑定规则版本并汇总结果与 note。实体解析、relation 处理器和结果构造由独立协作类承担。
/// </summary>
public sealed class RulesPlanService
{
    private readonly RulesPlanEntityResolver _entityResolver;
    private readonly RulesPlanQueryExecutor _queryExecutor;

    public RulesPlanService(
        RulesConceptCatalog catalog,
        RulesSearchService searchService,
        RulesNameIndexService nameIndex,
        RulesFlowService flowService,
        RulesReferenceService referenceService,
        RulesFactService factService,
        VectorSearchService? vectorSearch)
    {
        _entityResolver = new RulesPlanEntityResolver(
            catalog, searchService, nameIndex, vectorSearch);
        var resultBuilder = new RulesPlanResultBuilder(
            catalog, referenceService, flowService, factService);

        _queryExecutor = new RulesPlanQueryExecutor(
            new RulesPlanRelationQueryHandler(
                catalog, searchService, _entityResolver, resultBuilder),
            new RulesPlanFlowQueryHandler(flowService),
            new RulesPlanMembersQueryHandler(catalog, _entityResolver),
            new RulesPlanRuleLookupQueryHandler(
                catalog, searchService, flowService, _entityResolver),
            new RulesPlanComputeQueryHandler(factService, _entityResolver));
    }

    /// <summary>清空 relation 类型缓存。</summary>
    public void Clear()
    {
        _entityResolver.Clear();
    }

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
        using var planSpan = RuleRuntimeTelemetry.Measure("assembly", "query_plan_execution");
        cancellationToken.ThrowIfCancellationRequested();

        var result = new PlanExecutionResult
        {
            RequestId = "plan_" + Guid.NewGuid().ToString("N")[..12],
            Game = game,
            RulesVersion = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
                ? snapshot.Version
                : null
        };
        if (!RulesPlanQueryParser.TryParse(plan, question, out var queryItems, out var parseError))
        {
            result.Note = parseError;
            return result;
        }

        for (var queryIndex = 0; queryIndex < queryItems.Count; queryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = queryItems[queryIndex];
            var item = await _queryExecutor.ExecuteAsync(
                game, parsed, question, cancellationToken);
            item.QueryIndex = queryIndex;
            item.QueryId = $"{result.RequestId}:{queryIndex}";
            item.RulesVersion = result.RulesVersion;
            item.Intent = parsed.DisplayIntent;
            RulesPlanResultContract.Normalize(item, result.RulesVersion);
            result.Results.Add(item);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (result.Results.Any(r => r.Status is "unresolved" or "unsupported" or "no_match" or "invalid"))
        {
            var notes = new List<string>();
            if (result.Results.Any(r => r.Status == "unresolved"))
                notes.Add("unresolved 的候选描述只用于选择目标，不是已确认事实；请用候选中的确切 id 或中文名、保持原 relation 和 intent 重新查询");
            if (result.Results.Any(r => r.Status == "no_match"))
                notes.Add("no_match 表示规则库未确认到该查询的信息，请明确说明未查到，不要编造规则");
            if (result.Results.Any(r => r.Status == "unsupported"))
                notes.Add("unsupported 的 relation 请改用 explain/condition/cost/target/ordering/boundary/flow/list/identify/members/rule_lookup/compute");
            if (result.Results.Any(r => r.Status == "invalid"))
                notes.Add("invalid 表示查询参数与 scope 组合不合法；请按 tool schema 修正参数");
            result.Note = "部分查询未完成：" + string.Join("；", notes) + "。";
        }
        return result;
    }

    internal static bool NeedsIntentTargetConfirmation(
        IReadOnlyList<ConceptSummary> intentCandidates,
        IReadOnlyCollection<string> exactKeys)
        => RulesPlanEntityResolver.NeedsIntentTargetConfirmation(intentCandidates, exactKeys);
}
