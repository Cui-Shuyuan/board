using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// 按 query 收集证据。一次 execute_plan 的所有 query 都会记录；最终 tier
/// 保留历史状态；仅同版本、单一候选被后续精确同关系查询（identify → explain 也允许）确认时恢复。
/// 解析辅助函数与收集器放在同一职责边界内，外部只更新测试类型引用。
/// </summary>
internal sealed class AnswerEvidenceCollector
{
        public List<QueryEvidence> Queries { get; } = new();
        public string? RulesVersion { get; private set; }

        public bool SawData => Queries.Any(q => q.HasData);
        public bool SawCandidates => Queries.Any(q => q.Candidates.Count > 0);

        public void AddPlanResult(string toolResult)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolResult);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                if (TryAddProjectedPlanResult(root))
                    return;

                string? planVersion = null;
                if (root.TryGetProperty("RulesVersion", out var versionProp)
                    && versionProp.ValueKind == JsonValueKind.String)
                {
                    planVersion = versionProp.GetString();
                    RulesVersion ??= planVersion;
                }

                if (!root.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var item in results.EnumerateArray())
                    Queries.Add(ParseQueryEvidence(item, planVersion));
            }
            catch
            {
                // 非 plan 结果或解析失败——不参与 tier 判定
            }
        }

        private bool TryAddProjectedPlanResult(JsonElement root)
        {
            if (!root.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array)
                return false;

            string? planVersion = null;
            if (root.TryGetProperty("rules_version", out var versionProp)
                && versionProp.ValueKind == JsonValueKind.String)
            {
                planVersion = versionProp.GetString();
                RulesVersion ??= planVersion;
            }

            var evidenceById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("evidence_catalog", out var catalog)
                && catalog.ValueKind == JsonValueKind.Array)
            {
                foreach (var evidence in catalog.EnumerateArray())
                {
                    var evidenceId = ReadFlexibleString(evidence, "evidence_id", "EvidenceId");
                    if (!string.IsNullOrWhiteSpace(evidenceId))
                        evidenceById[evidenceId] = evidence;
                }
            }

            foreach (var item in queries.EnumerateArray())
            {
                var evidenceIds = ReadFlexibleStringArray(item, "evidence_ids", "EvidenceIds");
                var query = new QueryEvidence
                {
                    QueryIndex = item.TryGetProperty("query_index", out var qi) && qi.ValueKind == JsonValueKind.Number
                        ? qi.GetInt32()
                        : 0,
                    Relation = ReadFlexibleString(item, "relation", "Relation"),
                    Entity = ReadFlexibleString(item, "entity", "Entity"),
                    Intent = ReadFlexibleString(item, "intent", "Intent"),
                    Status = ReadFlexibleString(item, "status", "Status"),
                    Source = ReadFlexibleString(item, "source", "Source"),
                    Resolution = ReadFlexibleString(item, "resolution", "Resolution"),
                    Coverage = ReadFlexibleString(item, "coverage", "Coverage"),
                    RulesVersion = planVersion,
                    Message = ReadFlexibleNullableString(item, "message", "Message"),
                    ResolvedTargets = ReadFlexibleStringArray(item, "resolved_targets", "ResolvedTargets"),
                    EvidenceIds = evidenceIds
                };

                foreach (var evidenceId in evidenceIds)
                {
                    if (!evidenceById.TryGetValue(evidenceId, out var evidence)) continue;
                    var type = ReadFlexibleString(evidence, "type", "Type");
                    if (!string.IsNullOrWhiteSpace(type)) query.PayloadTypes.Add(type);
                    var canonical = ReadFlexibleString(evidence, "canonical_path", "CanonicalPath");
                    if (!string.IsNullOrWhiteSpace(canonical))
                        query.EvidenceCanonicalPaths.Add(canonical);
                    if (type == QueryPayloadTypes.Definition
                        && evidence.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Object)
                    {
                        var concept = ParseEvidenceConcept(content);
                        if (concept != null && !query.Matched.Any(existing => existing.Id == concept.Id))
                            query.Matched.Add(concept);
                    }
                }

                query.Candidates = ParseProjectedCandidates(item);
                query.HasData = evidenceIds.Count > 0 || query.Matched.Count > 0;
                Queries.Add(query);
            }
            return true;
        }

        public AnswerEvidence Build()
        {
            ResolveCandidateQueries();
            var evidence = new AnswerEvidence
            {
                RulesVersion = RulesVersion,
                QueryCount = Queries.Count,
                HasData = SawData,
                HasCandidates = SawCandidates,
                OkCount = Queries.Count(q => q.Status == "ok"),
                UnresolvedCount = Queries.Count(q => q.Status == "unresolved"),
                NoMatchCount = Queries.Count(q => q.Status == "no_match"),
                UnsupportedCount = Queries.Count(q => q.Status == "unsupported"),
                PendingCount = Queries.Count(q => !q.HasData && q.ResolvedByQueryIndex == null),
                Queries = Queries.ToList()
            };

            evidence.IsComplete = evidence.QueryCount > 0
                && evidence.PendingCount == 0
                && evidence.HasData;

            evidence.Tier = evidence.IsComplete
                ? "tier1"
                : evidence.HasData
                    ? "partial"
                    : evidence.HasCandidates
                        ? "tier2"
                        : "tier3";

            return evidence;
        }

        private void ResolveCandidateQueries()
        {
            foreach (var query in Queries)
            {
                query.ResolvedByQueryIndex = null;
                query.ConfirmedCandidateId = null;
                query.ConfirmedByRelation = null;
            }

            for (var i = 0; i < Queries.Count; i++)
            {
                var query = Queries[i];
                if (query.Status != "unresolved" || string.IsNullOrEmpty(query.RulesVersion)) continue;
                var candidateIds = query.Candidates
                    .Select(c => c.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (candidateIds.Length == 0) continue;

                for (var j = i + 1; j < Queries.Count; j++)
                {
                    var later = Queries[j];
                    if (!CanConfirmRelation(query.Relation, later.Relation)
                        || later.RulesVersion != query.RulesVersion
                        || !later.HasData
                        || later.Status != "ok"
                        || later.Source is not ("exact_id" or "exact_name_zh"))
                        continue;

                    // 只接受唯一对象命中；名称命中多个对象或一次 ok 返回多个 matched 时不能替用户选。
                    if (later.Matched.Count != 1) continue;
                    var selectedId = later.Matched[0].Id;
                    if (string.IsNullOrEmpty(selectedId) || !candidateIds.Contains(selectedId, StringComparer.Ordinal))
                        continue;
                    // 同一候选出现在多个未解决 query 中时，单凭 id 无法证明这次 ok 对齐的是哪一问。
                    if (IsAmbiguousCandidateConfirmation(i, later, selectedId)) continue;

                    query.ResolvedByQueryIndex = j;
                    query.ConfirmedCandidateId = selectedId;
                    query.ConfirmedByRelation = later.Relation;
                    break;
                }
            }
        }

        private bool IsAmbiguousCandidateConfirmation(int priorIndex, QueryEvidence later, string candidateId)
        {
            var competing = new List<int>();
            for (var k = 0; k < Queries.Count; k++)
            {
                var other = Queries[k];
                if (other.Status != "unresolved"
                    || !string.Equals(other.RulesVersion, later.RulesVersion, StringComparison.Ordinal)
                    || !CanConfirmRelation(other.Relation, later.Relation))
                    continue;
                if (other.Candidates.Any(c => string.Equals(c.Id, candidateId, StringComparison.Ordinal)))
                    competing.Add(k);
            }

            if (competing.Count <= 1) return false;

            // 同一候选可能出现在多个宽泛 query 中。后续查询的 intent 与某一项逐字一致时，
            // 这是可审查的关联证据，只解决那一项；其余同候选 query 保持 pending。
            var laterIntent = later.Intent.Trim();
            if (!string.IsNullOrEmpty(laterIntent))
            {
                var sameIntent = competing
                    .Where(index => string.Equals(
                        Queries[index].Intent.Trim(), laterIntent, StringComparison.Ordinal))
                    .ToList();
                if (sameIntent.Count == 1)
                    return sameIntent[0] != priorIndex;
                if (sameIntent.Count > 1)
                    return true;

                // 复述 intent 常有同义词/错字（如“放几张”对“发几张”）。只有最高相似度
                // 明显领先第二项时才允许关联，避免两个相近子问题被一次 ok 一起清空。
                var ranked = competing
                    .Select(index => (Index: index, Score: IntentSimilarity(Queries[index].Intent, laterIntent)))
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => item.Index)
                    .ToList();
                var secondScore = ranked.Count > 1 ? ranked[1].Score : 0f;
                if (ranked[0].Score >= 0.70f && ranked[0].Score - secondScore >= 0.15f)
                    return ranked[0].Index != priorIndex;
            }

            // 没有足够区分证据时保持保守：不能因为 id 相同就把这次 ok 关联到任意一问。
            return true;
        }

        private static float IntentSimilarity(string left, string right)
        {
            var a = (left ?? "").Trim();
            var b = (right ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return 0f;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1f;
            if (a.Length < 2 || b.Length < 2) return 0f;

            static HashSet<string> Bigrams(string value) =>
                Enumerable.Range(0, value.Length - 1)
                    .Select(index => value.Substring(index, 2))
                    .ToHashSet(StringComparer.Ordinal);

            var leftBigrams = Bigrams(a);
            var rightBigrams = Bigrams(b);
            var intersection = leftBigrams.Count(rightBigrams.Contains);
            var union = leftBigrams.Count + rightBigrams.Count - intersection;
            return union == 0 ? 0f : (float)intersection / union;
        }

        private static bool CanConfirmRelation(string originalRelation, string laterRelation) =>
            string.Equals(originalRelation, laterRelation, StringComparison.Ordinal)
            || (originalRelation == "identify" && laterRelation == "explain");

        public string GetTier()
        {
            var tier = Build().Tier;
            return tier switch
            {
                "tier1" => "tier1-数据回答",
                "tier2" => "tier2-候选兜底",
                "partial" => "partial-部分有据",
                _ => "tier3-无数据"
            };
        }

    internal static QueryEvidence ParseQueryEvidence(JsonElement item, string? rulesVersion)
    {
        var status = ReadFlexibleString(item, "Status", "status");
        var query = new QueryEvidence
        {
            QueryIndex = item.TryGetProperty("QueryIndex", out var qi) && qi.ValueKind == JsonValueKind.Number
                ? qi.GetInt32()
                : item.TryGetProperty("queryIndex", out var camelQi) && camelQi.ValueKind == JsonValueKind.Number
                    ? camelQi.GetInt32()
                    : 0,
            Relation = ReadFlexibleString(item, "Relation", "relation"),
            Entity = ReadFlexibleString(item, "Entity", "entity"),
            Intent = ReadFlexibleString(item, "Intent", "intent"),
            Status = status,
            Source = ReadFlexibleString(item, "Source", "source"),
            Resolution = ReadFlexibleString(item, "Resolution", "resolution"),
            Coverage = ReadFlexibleString(item, "Coverage", "coverage"),
            RulesVersion = rulesVersion,
            Message = ReadFlexibleNullableString(item, "Message", "message"),
            Matched = ParseMatchedArray(item, "Matched", "matched"),
            Candidates = ParseCandidateArray(item, "Candidates", "candidates"),
            References = ParseMatchedArray(item, "Related", "related")
        };

        var flow = ReadFlexibleArray(item, "FlowContext", "flowContext");
        if (flow.HasValue)
        {
            query.FlowContext = flow.Value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
        }

        query.ResolvedTargets = ReadFlexibleStringArray(item, "ResolvedTargets", "resolvedTargets");
        var payloads = ReadFlexibleArray(item, "Payloads", "payloads");
        if (payloads.HasValue)
        {
            foreach (var payload in payloads.Value.EnumerateArray())
            {
                if (payload.ValueKind != JsonValueKind.Object) continue;
                var type = ReadFlexibleString(payload, "Type", "type");
                if (!string.IsNullOrWhiteSpace(type)) query.PayloadTypes.Add(type);
            }
        }
        var omissions = ReadFlexibleArray(item, "Omissions", "omissions");
        if (omissions.HasValue)
        {
            foreach (var omission in omissions.Value.EnumerateArray())
            {
                if (omission.ValueKind != JsonValueKind.Object) continue;
                var reason = ReadFlexibleString(omission, "Reason", "reason");
                if (!string.IsNullOrWhiteSpace(reason)) query.OmissionReasons.Add(reason);
            }
        }

        var knownPayloads = new HashSet<string>(StringComparer.Ordinal)
        {
            QueryPayloadTypes.Definition,
            QueryPayloadTypes.CatalogPage,
            QueryPayloadTypes.FlowSlice,
            QueryPayloadTypes.RelationFacts,
            QueryPayloadTypes.ComputedFact,
        QueryPayloadTypes.MemberSet
        };
        var unknownPayload = query.PayloadTypes.Any(type => !knownPayloads.Contains(type));
        var hasCatalogPayload = query.PayloadTypes.Contains(QueryPayloadTypes.CatalogPage);
        var hasCatalog = item.TryGetProperty("Catalog", out var catalog)
            && catalog.ValueKind == JsonValueKind.Object;
        var hasFormalPayload = query.PayloadTypes.Any(knownPayloads.Contains);
        var hasLegacyData = query.Matched.Count > 0 || hasCatalog;

        // 新契约存在时以 payload 为准；list 的 CatalogPage 是合法数据，不能判成空证据成功。
        query.HasData = status == "ok"
            && !unknownPayload
            && (payloads.HasValue ? hasFormalPayload : hasLegacyData)
            && (hasFormalPayload || hasLegacyData || !payloads.HasValue);
        if (unknownPayload)
        {
            query.Status = "unsupported";
            query.Resolution = QueryResolutionStatuses.Unsupported;
            query.Coverage = QueryCoverageStatuses.Unknown;
            query.HasData = false;
        }

        return query;
    }

    private static string ReadFlexibleString(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        return "";
    }

    private static string? ReadFlexibleNullableString(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static JsonElement? ReadFlexibleArray(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.Array)
            return value;
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.Array)
            return value;
        return null;
    }

    private static List<string> ReadFlexibleStringArray(JsonElement item, string pascal, string camel)
    {
        var array = ReadFlexibleArray(item, pascal, camel);
        if (!array.HasValue) return new List<string>();
        return array.Value.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static List<EvidenceConcept> ParseMatchedArray(JsonElement item, string pascalProperty, string? camelProperty = null)
    {
        var result = new List<EvidenceConcept>();
        var array = ReadFlexibleArray(item, pascalProperty, camelProperty ?? char.ToLowerInvariant(pascalProperty[0]) + pascalProperty[1..]);
        if (!array.HasValue)
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.Value.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            result.Add(new EvidenceConcept
            {
                Id = id,
                Name = ExtractNameZh(element)
            });
        }

        return result;
    }

    private static List<EvidenceConcept> ParseCandidateArray(JsonElement item, string pascalProperty, string? camelProperty = null)
    {
        var result = new List<EvidenceConcept>();
        var array = ReadFlexibleArray(item, pascalProperty, camelProperty ?? char.ToLowerInvariant(pascalProperty[0]) + pascalProperty[1..]);
        if (!array.HasValue)
            return result;

        foreach (var element in array.Value.EnumerateArray())
        {
            var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? ""
                : element.TryGetProperty("id", out idProp) ? idProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var candidate = new EvidenceConcept
            {
                Id = id,
                Name = ReadFlexibleString(element, "Name", "name"),
                Type = ReadFlexibleNullableString(element, "Type", "type"),
                Description = ReadFlexibleNullableString(element, "Description", "description"),
                MatchReason = ReadFlexibleNullableString(element, "MatchReason", "matchReason")
            };
            var scoreElement = element.TryGetProperty("Score", out var scoreProp)
                ? scoreProp
                : element.TryGetProperty("score", out scoreProp) ? scoreProp : default;
            if (scoreElement.ValueKind == JsonValueKind.Number)
                candidate.Score = scoreElement.GetSingle();
            result.Add(candidate);
        }

        return result;
    }

    private static EvidenceConcept? ParseEvidenceConcept(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new EvidenceConcept
        {
            Id = id,
            Name = ExtractNameZh(element)
        };
    }

    private static List<EvidenceConcept> ParseProjectedCandidates(JsonElement item)
    {
        var result = new List<EvidenceConcept>();
        if (!item.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var candidate in candidates.EnumerateArray())
        {
            var id = ReadFlexibleString(candidate, "id", "Id");
            var path = ReadFlexibleString(candidate, "canonical_path", "CanonicalPath");
            if (string.IsNullOrWhiteSpace(id)) id = path;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var concept = new EvidenceConcept
            {
                Id = id,
                Name = ReadFlexibleString(candidate, "label", "Label"),
                Type = ReadFlexibleNullableString(candidate, "type", "Type"),
                MatchReason = ReadFlexibleNullableString(candidate, "reason", "Reason"),
                ReadStatus = ReadFlexibleNullableString(candidate, "read_status", "ReadStatus"),
                EvidenceId = ReadFlexibleNullableString(candidate, "evidence_id", "EvidenceId")
            };
            var targetResolved = candidate.TryGetProperty("target_resolved", out var tr)
                ? tr
                : candidate.TryGetProperty("TargetResolved", out tr) ? tr : default;
            if (targetResolved.ValueKind is JsonValueKind.True or JsonValueKind.False)
                concept.TargetResolved = targetResolved.GetBoolean();
            var score = candidate.TryGetProperty("score", out var sp)
                ? sp
                : candidate.TryGetProperty("Score", out sp) ? sp : default;
            if (score.ValueKind == JsonValueKind.Number)
                concept.Score = score.GetSingle();
            result.Add(concept);
        }
        return result;
    }

    private static string ExtractNameZh(JsonElement element)
    {
        if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            if (name.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.String)
                return zh.GetString() ?? "";
            if (name.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                return en.GetString() ?? "";
        }

        return "";
    }
}
