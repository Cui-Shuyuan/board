using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardAI.Api.Services;

/// <summary>
/// 将内部 PlanItemResult 投影成发给模型的薄工具结果：正式证据只在顶层
/// evidence_catalog 出现一次，query 只引用 evidence_id。绝不按字符截断正文；
/// 无法装入预算的原子证据会变成显式 omission + continuation。
/// </summary>
internal sealed class RuleToolResultProjector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly int _maxPackChars;

    public RuleToolResultProjector(int maxPackChars)
    {
        _maxPackChars = Math.Max(1000, maxPackChars);
    }

    public ProjectedRuleQueryResult Project(string game, PlanExecutionResult plan)
    {
        var result = new ProjectedRuleQueryResult
        {
            RequestId = plan.RequestId,
            Game = game,
            RulesVersion = plan.RulesVersion,
            MaxPackChars = _maxPackChars
        };
        var queryStates = plan.Results
            .Select(item => new QueryProjectionState
            {
                Source = item,
                Projected = new ProjectedQuery
                {
                    QueryId = item.QueryId,
                    QueryIndex = item.QueryIndex,
                    Relation = item.Relation,
                    Entity = item.Entity,
                    Intent = item.Intent,
                    Status = item.Status,
                    Source = item.Source,
                    Resolution = item.Resolution,
                    Coverage = item.Coverage,
                    ResolvedTargets = item.ResolvedTargets.ToList(),
                    Message = string.IsNullOrWhiteSpace(item.Message) ? null : item.Message,
                    Omissions = item.Omissions.Select(ToOmission).ToList()
                }
            })
            .ToList();

        var catalog = result.EvidenceCatalog;
        var byContent = new Dictionary<string, ProjectedEvidence>(StringComparer.Ordinal);
        var candidateEvidenceByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var omittedCount = 0;

        foreach (var state in queryStates)
        {
            var query = state.Projected;
            foreach (var payload in state.Source.Payloads)
            {
                var evidence = BuildEvidence(payload, state.Source.RulesVersion);
                var key = evidence.CanonicalPath + "\u0000" + evidence.Type + "\u0000" + evidence.ContentHash;
                if (byContent.TryGetValue(key, out var existing))
                {
                    AddEvidenceId(query, existing.EvidenceId);
                    RememberCandidateEvidence(candidateEvidenceByPath, payload, existing.EvidenceId);
                    continue;
                }

                var evidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
                catalog.Add(evidence);
                var candidateResult = BuildResult(result, queryStates);
                var candidateLength = JsonSerializer.Serialize(candidateResult, JsonOptions).Length;
                if (candidateLength > _maxPackChars)
                {
                    catalog.Remove(evidence);
                    omittedCount++;
                    var reason = evidenceJson.Length > _maxPackChars
                        ? QueryOmissionReasons.Oversized
                        : QueryOmissionReasons.BudgetTruncated;
                    query.Omissions.Add(new ProjectedOmission
                    {
                        Reason = reason,
                        CanonicalPath = payload.CanonicalPath,
                        FieldPath = payload.Type,
                        Continuation = new ProjectedContinuation
                        {
                            Operation = "read_evidence",
                            Entity = payload.CanonicalPath,
                            Scope = payload.Type,
                            Include = new List<string> { payload.Type }
                        }
                    });
                    if (query.Coverage == QueryCoverageStatuses.Complete)
                        query.Coverage = QueryCoverageStatuses.Partial;
                    continue;
                }

                byContent[key] = evidence;
                AddEvidenceId(query, evidence.EvidenceId);
                RememberCandidateEvidence(candidateEvidenceByPath, payload, evidence.EvidenceId);
            }
        }

        // Candidate metadata is intentionally thin; formal candidate content lives
        // in evidence_catalog and is linked by evidence_id when it was delivered.
        foreach (var state in queryStates)
        {
            state.Projected.Candidates = (state.Source.Candidates ?? new List<ConceptSummary>())
                .Select(candidate =>
                {
                    var path = string.IsNullOrWhiteSpace(candidate.Path) ? candidate.Id : candidate.Path;
                    candidateEvidenceByPath.TryGetValue(path, out var evidenceId);
                    return new ProjectedCandidate
                    {
                        CanonicalPath = path,
                        Id = candidate.Id,
                        Label = candidate.Name,
                        Type = candidate.Type,
                        Score = candidate.Score,
                        Reason = candidate.MatchReason,
                        ReadStatus = candidate.ReadStatus,
                        TargetResolved = candidate.TargetResolved,
                        EvidenceId = evidenceId
                    };
                })
                .ToList();
        }

        result.OmittedEvidenceCount = omittedCount;
        result.DeliveredEvidenceCount = catalog.Count;
        var final = BuildResult(result, queryStates);
        final.OmittedEvidenceCount += omittedCount;
        final.DeliveredEvidenceCount = catalog.Count;
        final.SerializedChars = JsonSerializer.Serialize(final, JsonOptions).Length;
        return final;
    }

    private static ProjectedRuleQueryResult BuildResult(
        ProjectedRuleQueryResult template,
        List<QueryProjectionState> states)
    {
        return new ProjectedRuleQueryResult
        {
            RequestId = template.RequestId,
            Game = template.Game,
            RulesVersion = template.RulesVersion,
            MaxPackChars = template.MaxPackChars,
            DeliveredEvidenceCount = template.EvidenceCatalog.Count,
            OmittedEvidenceCount = states.Sum(state => state.Projected.Omissions.Count(
                omission => omission.Reason is QueryOmissionReasons.Oversized or QueryOmissionReasons.BudgetTruncated)),
            Queries = states.Select(state => state.Projected).ToList(),
            EvidenceCatalog = template.EvidenceCatalog.ToList(),
            Note = "证据正文只在 evidence_catalog 出现一次；query.evidence_ids 是引用。partial/unknown 不能当作全集或答案充分证明。"
        };
    }

    private static ProjectedEvidence BuildEvidence(QueryPayload payload, string? fallbackVersion)
    {
        var includeContent = payload.Type is QueryPayloadTypes.Definition
            or QueryPayloadTypes.FlowSlice
            or QueryPayloadTypes.CatalogPage
            or QueryPayloadTypes.ComputedFact;
        // RelationFacts and MemberSet Data already contain the complete structured
        // body; avoid sending a second equivalent Content copy.
        var content = includeContent ? payload.Content : null;
        var data = payload.Data;
        var hashInput = new StringBuilder()
            .Append(payload.CanonicalPath).Append('\u0000')
            .Append(payload.Type).Append('\u0000')
            .Append(payload.Content?.GetRawText()).Append('\u0000')
            .Append(payload.Data == null ? "" : JsonSerializer.Serialize(payload.Data, JsonOptions));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())))
            .ToLowerInvariant()[..16];
        return new ProjectedEvidence
        {
            EvidenceId = "ev_" + hash,
            Type = payload.Type,
            CanonicalPath = payload.CanonicalPath,
            RulesVersion = payload.RulesVersion ?? fallbackVersion,
            Completeness = payload.Completeness,
            Source = payload.DefinitionSource,
            ContentHash = hash,
            Content = content,
            Data = data
        };
    }

    private static void AddEvidenceId(ProjectedQuery query, string evidenceId)
    {
        if (!query.EvidenceIds.Contains(evidenceId, StringComparer.Ordinal))
            query.EvidenceIds.Add(evidenceId);
    }

    private static void RememberCandidateEvidence(
        Dictionary<string, string> map, QueryPayload payload, string evidenceId)
    {
        if (payload.Data == null
            || !payload.Data.TryGetValue("role", out var role)
            || role.ValueKind != JsonValueKind.String
            || role.GetString() != "rule_candidate")
            return;
        if (!string.IsNullOrWhiteSpace(payload.CanonicalPath))
            map[payload.CanonicalPath] = evidenceId;
    }

    private static ProjectedOmission ToOmission(QueryOmission omission) => new()
    {
        Reason = omission.Reason,
        CanonicalPath = omission.CanonicalPath,
        FieldPath = omission.FieldPath,
        Continuation = omission.Continuation == null
            ? null
            : new ProjectedContinuation
            {
                Operation = omission.Continuation.Operation,
                Entity = omission.Continuation.Entity,
                Scope = omission.Continuation.Scope,
                Offset = omission.Continuation.Offset,
                Limit = omission.Continuation.Limit,
                Include = omission.Continuation.Include.ToList()
            }
    };

    private sealed class QueryProjectionState
    {
        public required PlanItemResult Source { get; init; }
        public required ProjectedQuery Projected { get; init; }
    }
}

internal sealed class ProjectedRuleQueryResult
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = "";
    [JsonPropertyName("game")]
    public string Game { get; set; } = "";
    [JsonPropertyName("rules_version")]
    public string? RulesVersion { get; set; }
    [JsonPropertyName("queries")]
    public List<ProjectedQuery> Queries { get; set; } = new();
    [JsonPropertyName("evidence_catalog")]
    public List<ProjectedEvidence> EvidenceCatalog { get; set; } = new();
    [JsonPropertyName("delivered_evidence_count")]
    public int DeliveredEvidenceCount { get; set; }
    [JsonPropertyName("omitted_evidence_count")]
    public int OmittedEvidenceCount { get; set; }
    [JsonPropertyName("max_pack_chars")]
    public int MaxPackChars { get; set; }
    [JsonPropertyName("serialized_chars")]
    public int SerializedChars { get; set; }
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

internal sealed class ProjectedQuery
{
    [JsonPropertyName("query_id")]
    public string QueryId { get; set; } = "";
    [JsonPropertyName("query_index")]
    public int QueryIndex { get; set; }
    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";
    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "";
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "";
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";
    [JsonPropertyName("coverage")]
    public string Coverage { get; set; } = "";
    [JsonPropertyName("resolved_targets")]
    public List<string> ResolvedTargets { get; set; } = new();
    [JsonPropertyName("evidence_ids")]
    public List<string> EvidenceIds { get; set; } = new();
    [JsonPropertyName("candidates")]
    public List<ProjectedCandidate> Candidates { get; set; } = new();
    [JsonPropertyName("omissions")]
    public List<ProjectedOmission> Omissions { get; set; } = new();
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class ProjectedEvidence
{
    [JsonPropertyName("evidence_id")]
    public string EvidenceId { get; set; } = "";
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [JsonPropertyName("canonical_path")]
    public string CanonicalPath { get; set; } = "";
    [JsonPropertyName("rules_version")]
    public string? RulesVersion { get; set; }
    [JsonPropertyName("completeness")]
    public string Completeness { get; set; } = "";
    [JsonPropertyName("source")]
    public string? Source { get; set; }
    [JsonPropertyName("content_hash")]
    public string ContentHash { get; set; } = "";
    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement>? Data { get; set; }
}

internal sealed class ProjectedCandidate
{
    [JsonPropertyName("canonical_path")]
    public string CanonicalPath { get; set; } = "";
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    [JsonPropertyName("score")]
    public float Score { get; set; }
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
    [JsonPropertyName("read_status")]
    public string? ReadStatus { get; set; }
    [JsonPropertyName("target_resolved")]
    public bool? TargetResolved { get; set; }
    [JsonPropertyName("evidence_id")]
    public string? EvidenceId { get; set; }
}

internal sealed class ProjectedOmission
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
    [JsonPropertyName("canonical_path")]
    public string? CanonicalPath { get; set; }
    [JsonPropertyName("field_path")]
    public string? FieldPath { get; set; }
    [JsonPropertyName("continuation")]
    public ProjectedContinuation? Continuation { get; set; }
}

internal sealed class ProjectedContinuation
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";
    [JsonPropertyName("entity")]
    public string? Entity { get; set; }
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
    [JsonPropertyName("offset")]
    public int? Offset { get; set; }
    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
    [JsonPropertyName("include")]
    public List<string> Include { get; set; } = new();
}
