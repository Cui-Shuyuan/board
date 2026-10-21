using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>compute relation 的确定性公式/查表入口。</summary>
internal sealed class RulesPlanComputeQueryHandler
{
    private readonly RulesFactService _factService;
    private readonly RulesPlanEntityResolver _entityResolver;

    public RulesPlanComputeQueryHandler(
        RulesFactService factService,
        RulesPlanEntityResolver entityResolver)
    {
        _factService = factService;
        _entityResolver = entityResolver;
    }

    internal async Task<PlanItemResult> ExecuteComputeAsync(
        string game,
        string entity,
        string intent,
        string question,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entity))
        {
            return new PlanItemResult
            {
                Relation = "compute",
                Entity = entity,
                Status = "invalid",
                Resolution = QueryResolutionStatuses.Invalid,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "compute 需要已确定的对象或公式载体。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.InvalidRequest, FieldPath = "entity" }
                }
            };
        }

        var matched = _entityResolver.ResolvePlanEntity(game, entity, out var candidates, out var source);
        if (matched.Count == 0)
        {
            return new PlanItemResult
            {
                Relation = "compute",
                Entity = entity,
                Status = candidates.Count > 0 ? "unresolved" : "no_match",
                Resolution = candidates.Count > 0
                    ? QueryResolutionStatuses.Candidates
                    : QueryResolutionStatuses.NotFound,
                Coverage = QueryCoverageStatuses.Unknown,
                Candidates = candidates.Count > 0 ? candidates : null,
                Message = candidates.Count > 0
                    ? "计算对象未确定，请先消歧。"
                    : "规则库中没有可计算的对象。",
                Omissions =
                {
                    new QueryOmission
                    {
                        Reason = candidates.Count > 0
                            ? QueryOmissionReasons.AmbiguousIdentity
                            : QueryOmissionReasons.NoRuleCoverage,
                        FieldPath = "entity"
                    }
                }
            };
        }

        var version = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
            ? snapshot.Version
            : null;
        var computationQuestion = string.Join(" ", new[] { intent, question }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal));
        var facts = _factService.ExtractFacts(
            game, matched.Select(hit => hit.Element).ToList(), computationQuestion);
        var result = new PlanItemResult
        {
            Relation = "compute",
            Entity = entity,
            Status = facts is { Count: > 0 } ? "ok" : "unsupported",
            Resolution = QueryResolutionStatuses.Exact,
            Coverage = facts is { Count: > 0 } ? QueryCoverageStatuses.Complete : QueryCoverageStatuses.Partial,
            Source = source,
            ResolvedTargets = matched.Select(hit => hit.CanonicalPath).Distinct(StringComparer.Ordinal).ToList(),
            Matched = matched.Select(hit => hit.Element).ToList(),
            RetrievalQueries = new List<string> { entity },
            Message = facts is { Count: > 0 }
                ? "使用现有确定性事实服务计算；输出仅覆盖受支持公式和已知参数。"
                : "该对象没有可用的受支持公式，或问题缺少计算所需参数；未猜默认人数/回合/数量。"
        };
        result.Payloads.AddRange(matched.Select(hit => new QueryPayload
        {
            Type = QueryPayloadTypes.Definition,
            CanonicalPath = hit.CanonicalPath,
            RulesVersion = version,
            Completeness = QueryCoverageStatuses.Complete,
            DefinitionSource = source,
            Content = hit.Element.Clone()
        }));

        if (facts is { Count: > 0 })
        {
            var hasComputed = facts.Any(fact =>
                (fact.TryGetProperty("computed", out _) || fact.TryGetProperty("total", out _)));
            if (!hasComputed)
            {
                result.Coverage = QueryCoverageStatuses.Partial;
                result.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.FieldMissing,
                    CanonicalPath = matched[0].CanonicalPath,
                    FieldPath = "compute.parameters"
                });
            }
            foreach (var fact in facts)
            {
                result.Payloads.Add(new QueryPayload
                {
                    Type = QueryPayloadTypes.ComputedFact,
                    CanonicalPath = matched[0].CanonicalPath,
                    RulesVersion = version,
                    Completeness = hasComputed ? QueryCoverageStatuses.Complete : QueryCoverageStatuses.Partial,
                    Content = fact.Clone()
                });
            }
        }
        else
        {
            result.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.UnsupportedRelation,
                CanonicalPath = matched[0].CanonicalPath,
                FieldPath = "compute.supported_formula"
            });
        }
        return result;
    }
}
