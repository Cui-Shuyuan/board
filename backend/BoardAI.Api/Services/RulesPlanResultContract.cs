using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// execute_plan 对外结果契约归一化。只补 resolution/coverage 与拒绝未知 payload，
/// 不改变检索决策，也不构造 relation 专属事实。
/// </summary>
internal static class RulesPlanResultContract
{
    private static readonly HashSet<string> KnownPayloadTypes = new(StringComparer.Ordinal)
    {
        QueryPayloadTypes.Definition,
        QueryPayloadTypes.CatalogPage,
        QueryPayloadTypes.FlowSlice,
        QueryPayloadTypes.RelationFacts,
        QueryPayloadTypes.ComputedFact,
        QueryPayloadTypes.MemberSet
    };

    /// <summary>
    /// 在结果离开发起服务前补齐 resolution/coverage，并拒绝未知 payload 静默成功。
    /// 这是内部契约归一化，不改变已有检索决策。
    /// </summary>
    internal static void Normalize(PlanItemResult item, string? rulesVersion)
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
            // rule_lookup deliberately keeps read-but-unconfirmed rule candidates
            // even when the queried object identity itself is exact.
            if (item.Relation != "rule_lookup")
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

        if (item.Payloads.Count > 0 && item.Resolution == QueryResolutionStatuses.Exact)
        {
            // Payload paths are read locations (dependencies/candidate rules too),
            // not resolved query targets. Each primary result branch sets
            // ResolvedTargets explicitly; never infer identity from payload presence.
            // Non-exact resolutions keep unknown coverage even if some candidates
            // happened to be formally read.
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
}
