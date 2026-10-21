using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// relation 分派器。只负责校验 relation 并把已解析查询交给对应处理器，
/// 不持有任何请求状态，也不反向依赖 RulesPlanService。
/// </summary>
internal sealed class RulesPlanQueryExecutor
{
    private static readonly HashSet<string> PlanRelations = new(StringComparer.Ordinal)
    {
        "explain", "condition", "cost", "target", "ordering", "boundary",
        "flow", "list", "identify", "members", "rule_lookup", "compute"
    };

    private readonly RulesPlanRelationQueryHandler _relationHandler;
    private readonly RulesPlanFlowQueryHandler _flowHandler;
    private readonly RulesPlanMembersQueryHandler _membersHandler;
    private readonly RulesPlanRuleLookupQueryHandler _ruleLookupHandler;
    private readonly RulesPlanComputeQueryHandler _computeHandler;

    public RulesPlanQueryExecutor(
        RulesPlanRelationQueryHandler relationHandler,
        RulesPlanFlowQueryHandler flowHandler,
        RulesPlanMembersQueryHandler membersHandler,
        RulesPlanRuleLookupQueryHandler ruleLookupHandler,
        RulesPlanComputeQueryHandler computeHandler)
    {
        _relationHandler = relationHandler;
        _flowHandler = flowHandler;
        _membersHandler = membersHandler;
        _ruleLookupHandler = ruleLookupHandler;
        _computeHandler = computeHandler;
    }

    internal async Task<PlanItemResult> ExecuteAsync(
        string game,
        ParsedPlanQuery query,
        string question,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!PlanRelations.Contains(query.Relation))
        {
            return new PlanItemResult
            {
                Relation = query.Relation,
                Entity = query.Entity,
                Status = "unsupported",
                Resolution = QueryResolutionStatuses.Unsupported,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "该 relation 不在支持列表（explain/condition/cost/target/ordering/boundary/flow/list/identify/members/rule_lookup/compute）。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.UnsupportedRelation, FieldPath = "relation" }
                }
            };
        }

        return query.Relation switch
        {
            "members" => await _membersHandler.ExecuteMembersQueryAsync(
                game, query.Entity, query.Offset, query.Limit, cancellationToken),
            "rule_lookup" => await _ruleLookupHandler.ExecuteRuleLookupAsync(
                game, query.Entity, query.ExplicitIntent, question, query.Offset,
                query.HasExplicitLimit ? query.Limit : 3, cancellationToken),
            "compute" => await _computeHandler.ExecuteComputeAsync(
                game, query.Entity, query.ExplicitIntent, question, cancellationToken),
            "flow" => _flowHandler.ExecuteFlowQueryAsync(
                game, query.Entity, query.Scope, query.Include),
            _ => await _relationHandler.ExecuteAsync(
                game, query.Relation, query.Entity, query.ExplicitIntent, question,
                query.Offset, query.Limit, cancellationToken)
        };
    }
}
