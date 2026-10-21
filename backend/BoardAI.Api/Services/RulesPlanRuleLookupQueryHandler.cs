using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// rule_lookup relation 的身份保持、intent 候选排序、有界正式读取与续读描述。
/// 请求内的规则版本和 continuation 由调用链传入/回传，处理器自身不持有请求状态。
/// </summary>
internal sealed class RulesPlanRuleLookupQueryHandler
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesSearchService _searchService;
    private readonly RulesFlowService _flowService;
    private readonly RulesPlanEntityResolver _entityResolver;

    public RulesPlanRuleLookupQueryHandler(
        RulesConceptCatalog catalog,
        RulesSearchService searchService,
        RulesFlowService flowService,
        RulesPlanEntityResolver entityResolver)
    {
        _catalog = catalog;
        _searchService = searchService;
        _flowService = flowService;
        _entityResolver = entityResolver;
    }

    internal async Task<PlanItemResult> ExecuteRuleLookupAsync(
        string game,
        string entity,
        string intent,
        string question,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveIntent = string.IsNullOrWhiteSpace(intent) ? question.Trim() : intent.Trim();
        var pageOffset = Math.Max(0, offset);
        var pageSize = Math.Clamp(limit, 1, 12);
        if (string.IsNullOrWhiteSpace(entity) && string.IsNullOrWhiteSpace(effectiveIntent))
        {
            return new PlanItemResult
            {
                Relation = "rule_lookup",
                Entity = entity,
                Status = "invalid",
                Resolution = QueryResolutionStatuses.Invalid,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "rule_lookup 需要 entity 或完整 intent 至少一项。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.InvalidRequest, FieldPath = "entity|intent" }
                }
            };
        }

        var identityHits = string.IsNullOrWhiteSpace(entity)
            ? new List<ConceptPathHit>()
            : _entityResolver.ResolvePlanEntity(game, entity, out var identityCandidates, out var identitySource);
        var identitySourceValue = string.IsNullOrWhiteSpace(entity)
            ? ""
            : (identityHits.Count == 1 ? "exact" : identityHits.Count > 1 ? "candidates" : "not_found");

        if (identityHits.Count > 1)
        {
            // Keep identity ambiguity explicit before any rule candidate is treated
            // as the object the user meant.
            return new PlanItemResult
            {
                Relation = "rule_lookup",
                Entity = entity,
                Status = "unresolved",
                Resolution = QueryResolutionStatuses.Candidates,
                Coverage = QueryCoverageStatuses.Unknown,
                Candidates = identityHits.Select(hit => new ConceptSummary
                {
                    Id = RulesTextUtils.GetElementId(hit.Element),
                    Path = hit.CanonicalPath,
                    Name = RulesPlanEntityResolver.ExtractConceptName(hit.Element),
                    Type = _entityResolver.GetConceptTypeMap(game).GetValueOrDefault(RulesTextUtils.GetElementId(hit.Element), ""),
                    ReadStatus = "not_read",
                    TargetResolved = false,
                    MatchReason = "identity"
                }).ToList(),
                Message = "对象身份有多个候选；规则查找不能替用户选择对象。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.AmbiguousIdentity, FieldPath = "entity" }
                }
            };
        }

        var identityHit = identityHits.SingleOrDefault();
        var identityId = identityHit == null ? "" : RulesTextUtils.GetElementId(identityHit.Element);
        var identityPath = identityHit?.CanonicalPath ?? "";

        using var selectionSpan = RuleRuntimeTelemetry.Measure("assembly", "rule_candidate_selection");
        // Candidate selection is intent-first. Reference edges are only a
        // tie-breaker/recall source; directory order must not decide which rules
        // get the limited formal-read budget.
        var intentCandidates = string.IsNullOrWhiteSpace(effectiveIntent)
            ? new List<ConceptSummary>()
            : await RetrieveRuleLookupIntentCandidatesAsync(
                game, effectiveIntent, cancellationToken);
        var candidateByKey = new Dictionary<string, ConceptSummary>(StringComparer.Ordinal);
        var candidateOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextOrder = 0;

        void RememberCandidate(ConceptSummary candidate)
        {
            var key = RulesSearchRanking.CandidateKey(candidate);
            if (!candidateByKey.ContainsKey(key))
            {
                candidateByKey[key] = candidate;
                candidateOrder[key] = nextOrder++;
            }
        }

        if (!string.IsNullOrWhiteSpace(identityId) || !string.IsNullOrWhiteSpace(identityPath))
        {
            foreach (var candidate in FindRuleReferenceCandidates(game, identityId, identityPath))
            {
                candidate.Score = 0f;
                candidate.MatchReason = "reference";
                RememberCandidate(candidate);
            }
        }

        foreach (var candidate in intentCandidates)
        {
            var key = RulesSearchRanking.CandidateKey(candidate);
            if (candidateByKey.TryGetValue(key, out var existing))
            {
                existing.Score = candidate.Score;
                existing.MatchReason = existing.MatchReason == "reference"
                    ? "intent+reference"
                    : "intent";
                if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = candidate.Name;
                if (string.IsNullOrWhiteSpace(existing.Type)) existing.Type = candidate.Type;
            }
            else
            {
                candidate.MatchReason = "intent";
                RememberCandidate(candidate);
            }
        }

        // The exact object itself is an identity target, not a candidate rule.
        var ruleCandidates = candidateByKey
            .Where(pair => string.IsNullOrWhiteSpace(identityPath)
                || (!string.Equals(pair.Value.Path, identityPath, StringComparison.Ordinal)
                    && !string.Equals(pair.Value.Id, identityId, StringComparison.Ordinal)))
            .OrderByDescending(pair => pair.Value.Score)
            .ThenByDescending(pair => pair.Value.MatchReason?.Contains("reference", StringComparison.Ordinal) == true)
            .ThenBy(pair => candidateOrder[pair.Key])
            .Select(pair => pair.Value)
            .Take(12)
            .ToList();

        var pageCandidates = ruleCandidates
            .Skip(pageOffset)
            .Take(pageSize)
            .ToList();
        var pageEnd = Math.Min(ruleCandidates.Count, pageOffset + pageSize);
        var readLimited = ruleCandidates.Count > pageEnd;
        var payloads = new List<QueryPayload>();
        var version = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
            ? snapshot.Version
            : null;
        if (identityHit != null)
        {
            payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.Definition,
                CanonicalPath = identityHit.CanonicalPath,
                RulesVersion = version,
                Completeness = QueryCoverageStatuses.Complete,
                DefinitionSource = "exact_identity",
                Content = identityHit.Element.Clone(),
                Data = new Dictionary<string, JsonElement>
                {
                    ["role"] = JsonSerializer.SerializeToElement("identity"),
                    ["target_resolved"] = JsonSerializer.SerializeToElement(true)
                }
            });
        }

        var readPaths = new List<string>();
        var failedReads = new List<string>();
        var edges = new List<object>();
        foreach (var candidate in pageCandidates)
        {
            var candidatePath = string.IsNullOrWhiteSpace(candidate.Path) ? candidate.Id : candidate.Path;
            var hits = RulesPlanEntityResolver.PreferGameIdentity(
                candidatePath, _catalog.GetConceptsWithPaths(game, candidatePath).ToList());
            if (hits.Count == 0 && !string.IsNullOrWhiteSpace(candidate.Id))
                hits = RulesPlanEntityResolver.PreferGameIdentity(
                    candidate.Id, _catalog.GetConceptsWithPaths(game, candidate.Id).ToList());
            if (hits.Count == 0)
            {
                candidate.ReadStatus = "read_failed";
                candidate.TargetResolved = false;
                failedReads.Add(candidatePath);
                continue;
            }

            foreach (var hit in hits)
            {
                var referenceEdges = CollectReferenceEdges(hit.Element);
                var data = new Dictionary<string, JsonElement>
                {
                    ["role"] = JsonSerializer.SerializeToElement("rule_candidate"),
                    ["target_resolved"] = JsonSerializer.SerializeToElement(false),
                    ["read_status"] = JsonSerializer.SerializeToElement("auto_read"),
                    ["candidate_index"] = JsonSerializer.SerializeToElement(pageOffset + readPaths.Count),
                    ["reference_edges"] = JsonSerializer.SerializeToElement(referenceEdges)
                };
                payloads.Add(new QueryPayload
                {
                    Type = QueryPayloadTypes.Definition,
                    CanonicalPath = hit.CanonicalPath,
                    RulesVersion = version,
                    Completeness = QueryCoverageStatuses.Complete,
                    DefinitionSource = "auto_candidate_read",
                    Content = hit.Element.Clone(),
                    Data = data
                });
                if (!readPaths.Contains(hit.CanonicalPath, StringComparer.Ordinal))
                    readPaths.Add(hit.CanonicalPath);
                edges.AddRange(referenceEdges);
            }
            candidate.ReadStatus = "auto_read";
            candidate.TargetResolved = false;
        }

        var coverage = readLimited || failedReads.Count > 0
            ? QueryCoverageStatuses.Partial
            : QueryCoverageStatuses.Complete;
        var result = new PlanItemResult
        {
            Relation = "rule_lookup",
            Entity = entity,
            Status = identityHit != null ? "ok" : ruleCandidates.Count > 0 ? "unresolved" : "no_match",
            Resolution = identityHit != null
                ? QueryResolutionStatuses.Exact
                : ruleCandidates.Count > 0
                    ? QueryResolutionStatuses.Candidates
                    : QueryResolutionStatuses.NotFound,
            Coverage = identityHit != null ? coverage : QueryCoverageStatuses.Unknown,
            Source = identityHit != null ? identitySourceValue : "",
            ResolvedTargets = identityHit == null ? new List<string>() : new List<string> { identityHit.CanonicalPath },
            Candidates = ruleCandidates.Count == 0 ? null : ruleCandidates,
            RetrievalQueries = new[] { entity, effectiveIntent }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Message = identityHit == null
                ? "规则候选已由程序正式读取，但对象身份仍未确定；候选正文只用于模型判断相关性，不能当作已确认目标。"
                : ruleCandidates.Count == 0
                    ? "已读取对象定义，但未找到与完整 intent 相关的规则候选。"
                    : "已保留对象身份并自动正式读取候选规则；候选规则的内容已可验证，但哪条回答 intent 仍需模型相关性判断。"
        };
        result.Payloads.AddRange(payloads);

        var summaryData = new Dictionary<string, JsonElement>
        {
            ["identity_resolved"] = JsonSerializer.SerializeToElement(identityHit != null),
            ["candidate_rule_paths"] = JsonSerializer.SerializeToElement(readPaths),
            ["candidate_read_count"] = JsonSerializer.SerializeToElement(readPaths.Count),
            ["read_budget"] = JsonSerializer.SerializeToElement(pageSize),
            ["page_offset"] = JsonSerializer.SerializeToElement(pageOffset),
            ["page_size"] = JsonSerializer.SerializeToElement(pageSize),
            ["read_truncated"] = JsonSerializer.SerializeToElement(readLimited),
            ["reference_edges"] = JsonSerializer.SerializeToElement(edges)
        };
        result.Payloads.Add(new QueryPayload
        {
            Type = QueryPayloadTypes.RelationFacts,
            CanonicalPath = identityPath,
            RulesVersion = version,
            Completeness = coverage,
            Data = summaryData,
            Content = JsonSerializer.SerializeToElement(summaryData)
        });

        if (readLimited)
        {
            result.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.BudgetTruncated,
                CanonicalPath = identityPath,
                FieldPath = "rule_candidates",
                Continuation = new QueryContinuation
                {
                    Operation = "rule_lookup",
                    Entity = string.IsNullOrWhiteSpace(identityPath) ? entity : identityPath,
                    Intent = effectiveIntent,
                    Offset = pageOffset + pageSize,
                    Limit = pageSize
                }
            });
        }
        foreach (var failed in failedReads)
        {
            result.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.NoRuleCoverage,
                CanonicalPath = failed,
                FieldPath = "candidate_definition"
            });
        }
        if (identityHit == null && ruleCandidates.Count > 0)
        {
            result.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.AmbiguousIdentity,
                FieldPath = "entity"
            });
        }
        if (identityHit != null && ruleCandidates.Count == 0)
        {
            result.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.NoRuleCoverage,
                CanonicalPath = identityPath,
                FieldPath = "rule_lookup"
            });
        }

        return result;
    }

    private async Task<List<ConceptSummary>> RetrieveRuleLookupIntentCandidatesAsync(
        string game, string intent, CancellationToken cancellationToken)
    {
        var semantic = await _searchService.SearchConceptsAsync(
            game, intent, searchMode: "full", cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return semantic.Results
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Take(15)
            .ToList();
    }

    private List<ConceptSummary> FindRuleReferenceCandidates(string game, string identityId, string identityPath)
    {
        var results = new List<ConceptSummary>();
        if (string.IsNullOrWhiteSpace(identityId) && string.IsNullOrWhiteSpace(identityPath))
            return results;

        foreach (var type in new[] { "actions", "triggers", "conditions" })
        {
            foreach (var summary in _catalog.ListConcepts(game, type))
            {
                var lookup = string.IsNullOrWhiteSpace(summary.Path) ? summary.Id : summary.Path;
                var hits = RulesPlanEntityResolver.PreferGameIdentity(
                    lookup, _catalog.GetConceptsWithPaths(game, lookup).ToList());
                foreach (var hit in hits)
                {
                    if (hit.CanonicalPath == identityPath) continue;
                    if (!ElementReferencesIdentity(hit.Element, identityId, identityPath)) continue;
                    AddReferenceCandidate(
                        results, RulesTextUtils.GetElementId(hit.Element), hit.CanonicalPath,
                        RulesPlanEntityResolver.ExtractConceptName(hit.Element), type);
                }
            }
        }

        // Process rules can live in flow.json (e.g. Wingspan action triggers).
        // The flow index supplies canonical paths, so local trigger ids remain
        // disambiguated by parent structure.
        var flowIndex = _flowService.GetFlowIndex(game);
        foreach (var node in flowIndex.AllNodes)
        {
            if (node.CanonicalPath == identityPath) continue;
            // Structural container procedures/phases are not rules themselves;
            // their descendants carry the actionable text. Restrict to declared
            // action/trigger nodes to avoid every ancestor matching nested refs.
            if (node.Kind is not ("action" or "trigger")) continue;
            if (!ElementReferencesIdentity(node.Element, identityId, identityPath)) continue;
            AddReferenceCandidate(results, node.LocalId, node.CanonicalPath, node.Name, node.Kind);
        }
        return results;
    }

    private static void AddReferenceCandidate(
        List<ConceptSummary> results, string id, string canonicalPath, string name, string type)
    {
        if (results.Any(existing => existing.Path == canonicalPath)) return;
        results.Add(new ConceptSummary
        {
            Id = id,
            Path = canonicalPath,
            Name = name,
            Type = type,
            Score = 1f,
            MatchReason = "reference",
            ReadStatus = "not_read",
            TargetResolved = false
        });
    }

    private static bool ElementReferencesIdentity(
        JsonElement element, string identityId, string identityPath)
    {
        var raw = JsonSerializer.Serialize(element, RulesJsonUtils.RelaxedJsonOptions);
        foreach (System.Text.RegularExpressions.Match match in RulesJsonUtils.ConceptRefRegex.Matches(raw))
        {
            var reference = RulesPlanEntityResolver.NormalizeConceptRef(match.Groups[1].Value);
            if (string.Equals(reference, identityPath, StringComparison.Ordinal)) return true;
            if (!identityPath.StartsWith("ontology::", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(identityId)
                && string.Equals(reference, identityId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static List<object> CollectReferenceEdges(JsonElement element)
    {
        var edges = new List<object>();
        if (element.ValueKind != JsonValueKind.Object) return edges;
        foreach (var property in element.EnumerateObject())
        {
            var refs = RulesJsonUtils.ConceptRefRegex.Matches(
                    JsonSerializer.Serialize(property.Value, RulesJsonUtils.RelaxedJsonOptions))
                .Select(match => RulesPlanEntityResolver.NormalizeConceptRef(match.Groups[1].Value))
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (refs.Count == 0) continue;
            edges.Add(new
            {
                field = property.Name,
                edge_type = ClassifyReferenceEdge(property.Name),
                references = refs
            });
        }
        return edges;
    }

    private static string ClassifyReferenceEdge(string field)
    {
        if (field.Contains("condition", StringComparison.OrdinalIgnoreCase)) return "condition";
        if (field.Contains("cost", StringComparison.OrdinalIgnoreCase)) return "cost";
        if (field.Equals("target", StringComparison.OrdinalIgnoreCase)) return "target";
        if (field.Contains("content", StringComparison.OrdinalIgnoreCase)
            || field.Contains("effect", StringComparison.OrdinalIgnoreCase)) return "effect";
        if (field.Equals("description", StringComparison.OrdinalIgnoreCase)) return "mention";
        return "other_reference";
    }

}
