using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>members relation 的显式集合枚举与 schema 成员补全。</summary>
internal sealed class RulesPlanMembersQueryHandler
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesPlanEntityResolver _entityResolver;

    public RulesPlanMembersQueryHandler(
        RulesConceptCatalog catalog,
        RulesPlanEntityResolver entityResolver)
    {
        _catalog = catalog;
        _entityResolver = entityResolver;
    }

    internal async Task<PlanItemResult> ExecuteMembersQueryAsync(
        string game, string entity, int offset, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(entity))
        {
            return new PlanItemResult
            {
                Relation = "members",
                Entity = entity,
                Status = "invalid",
                Resolution = QueryResolutionStatuses.Invalid,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "members 查询必须提供 entity（集合/类型/父范围）。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.InvalidRequest, FieldPath = "entity" }
                }
            };
        }

        var hits = RulesPlanEntityResolver.PreferGameIdentity(
            entity, _catalog.GetConceptsWithPaths(game, entity).ToList());
        if (hits.Count == 0)
        {
            var deterministic = _entityResolver.ResolvePlanEntity(game, entity, out var candidates, out _);
            if (deterministic.Count > 0)
                hits = deterministic;
            else if (candidates.Count > 0)
                return new PlanItemResult
                {
                    Relation = "members",
                    Entity = entity,
                    Status = "unresolved",
                    Resolution = QueryResolutionStatuses.Candidates,
                    Coverage = QueryCoverageStatuses.Unknown,
                    Candidates = candidates,
                    Message = "集合/类型身份未确定，请选择候选后重试。",
                    Omissions =
                    {
                        new QueryOmission { Reason = QueryOmissionReasons.AmbiguousIdentity, FieldPath = "entity" }
                    }
                };
        }

        if (hits.Count == 0)
        {
            return new PlanItemResult
            {
                Relation = "members",
                Entity = entity,
                Status = "no_match",
                Resolution = QueryResolutionStatuses.NotFound,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "规则库中未找到该集合或类型。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.NoRuleCoverage, FieldPath = "entity" }
                }
            };
        }
        if (hits.Count > 1)
        {
            return new PlanItemResult
            {
                Relation = "members",
                Entity = entity,
                Status = "unresolved",
                Resolution = QueryResolutionStatuses.Candidates,
                Coverage = QueryCoverageStatuses.Unknown,
                Candidates = hits.Select(hit => new ConceptSummary
                {
                    Id = hit.CanonicalPath,
                    Path = hit.CanonicalPath,
                    Name = RulesPlanEntityResolver.ExtractConceptName(hit.Element),
                    Type = _entityResolver.GetConceptTypeMap(game).GetValueOrDefault(RulesTextUtils.GetElementId(hit.Element), "")
                }).ToList(),
                Message = "集合/类型身份存在多个候选，请使用 canonical_path 消歧。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.AmbiguousIdentity, FieldPath = "entity" }
                }
            };
        }

        var target = hits[0];
        var version = RulesSnapshotScope.Current is { } snapshot && snapshot.Game == game
            ? snapshot.Version
            : null;
        var memberEntries = new List<MemberEntry>();
        var membershipKinds = new List<string>();

        foreach (var field in new[] { "members", "items", "options", "slots" })
        {
            if (target.Element.ValueKind != JsonValueKind.Object
                || !target.Element.TryGetProperty(field, out var array)
                || array.ValueKind != JsonValueKind.Array)
                continue;

            membershipKinds.Add("explicit:" + field);
            for (var index = 0; index < array.GetArrayLength(); index++)
            {
                var memberElement = array[index];
                var memberPath = $"{target.CanonicalPath}/{field}[{index}]";
                var memberId = "";
                var memberName = "";
                JsonElement? content = null;
                if (memberElement.ValueKind == JsonValueKind.Object)
                {
                    content = memberElement.Clone();
                    memberId = RulesTextUtils.GetElementId(memberElement);
                    if (memberElement.TryGetProperty("name", out var memberNameProp)
                        && memberNameProp.ValueKind == JsonValueKind.Object
                        && memberNameProp.TryGetProperty("zh", out var zh))
                        memberName = zh.GetString() ?? "";
                    // Inline members may also have an independently resolvable
                    // canonical definition. Keep the declared membership path as
                    // its source, but expose the definition path when available.
                    var resolved = !string.IsNullOrWhiteSpace(memberId)
                        ? _catalog.GetConceptsWithPaths(game, memberId).ToList()
                        : new List<ConceptPathHit>();
                    if (resolved.Count == 1)
                        memberPath = resolved[0].CanonicalPath;
                }
                else if (memberElement.ValueKind == JsonValueKind.String)
                {
                    var reference = memberElement.GetString() ?? "";
                    var local = RulesPlanEntityResolver.NormalizeConceptRef(reference);
                    if (!string.IsNullOrWhiteSpace(local))
                    {
                        var resolved = _catalog.GetConceptsWithPaths(game, local).ToList();
                        if (resolved.Count == 1)
                        {
                            memberPath = resolved[0].CanonicalPath;
                            memberId = RulesTextUtils.GetElementId(resolved[0].Element);
                            memberName = RulesPlanEntityResolver.ExtractConceptName(resolved[0].Element);
                            content = resolved[0].Element.Clone();
                        }
                        else
                        {
                            memberId = local;
                        }
                    }
                }
                memberEntries.Add(new MemberEntry(memberPath, memberId, memberName, field, content));
            }
        }

        // Schema type relations are membership only for explicit
        // specifies/extends/instance_of edges. description text references are not
        // scanned here.
        var typeMembers = FindTypeMembers(game, target);
        if (typeMembers.Count > 0)
        {
            membershipKinds.Add("schema_type_relation");
            memberEntries.AddRange(typeMembers);
        }

        var distinctMembers = memberEntries
            .GroupBy(entry => entry.CanonicalPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var page = distinctMembers.Skip(offset).Take(limit).ToList();
        var hasMore = offset + page.Count < distinctMembers.Count;
        var membershipProven = membershipKinds.Count > 0;
        var coverage = membershipProven
            ? (hasMore ? QueryCoverageStatuses.Partial : QueryCoverageStatuses.Complete)
            : QueryCoverageStatuses.Partial;

        var dataDict = new Dictionary<string, JsonElement>
        {
            ["collection_kind"] = JsonSerializer.SerializeToElement(membershipKinds),
            ["members"] = JsonSerializer.SerializeToElement(page.Select(entry => new
            {
                canonical_path = entry.CanonicalPath,
                id = entry.Id,
                name = entry.Name,
                source = entry.Source,
                content = entry.Content
            }).ToList()),
            ["total_count"] = JsonSerializer.SerializeToElement(distinctMembers.Count),
            ["offset"] = JsonSerializer.SerializeToElement(offset),
            ["limit"] = JsonSerializer.SerializeToElement(limit),
            ["has_more"] = JsonSerializer.SerializeToElement(hasMore),
            ["membership_proven"] = JsonSerializer.SerializeToElement(membershipProven)
        };

        var item = new PlanItemResult
        {
            Relation = "members",
            Entity = entity,
            Status = "ok",
            Resolution = QueryResolutionStatuses.Exact,
            Coverage = coverage,
            Source = "canonical_path",
            ResolvedTargets = new List<string> { target.CanonicalPath },
            Payloads =
            {
                new QueryPayload
                {
                    Type = QueryPayloadTypes.Definition,
                    CanonicalPath = target.CanonicalPath,
                    RulesVersion = version,
                    Completeness = QueryCoverageStatuses.Complete,
                    DefinitionSource = "canonical_path",
                    Content = target.Element.Clone()
                },
                new QueryPayload
                {
                    Type = QueryPayloadTypes.MemberSet,
                    CanonicalPath = target.CanonicalPath,
                    RulesVersion = version,
                    Completeness = coverage,
                    Content = JsonSerializer.SerializeToElement(dataDict),
                    Data = dataDict
                }
            },
            RetrievalQueries = new List<string> { entity }
        };

        if (!membershipProven)
        {
            item.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.NoRuleCoverage,
                CanonicalPath = target.CanonicalPath,
                FieldPath = "members",
                Continuation = new QueryContinuation
                {
                    Operation = "members",
                    Entity = target.CanonicalPath,
                    Scope = "declared_members",
                    Offset = offset,
                    Limit = limit
                }
            });
            item.Message = "未发现可证明完整成员关系的显式集合或 schema 关系；已返回定义依据，不能把空列表称作全集。";
        }
        else if (hasMore)
        {
            item.Omissions.Add(new QueryOmission
            {
                Reason = QueryOmissionReasons.ContinuationRequired,
                CanonicalPath = target.CanonicalPath,
                FieldPath = "members",
                Continuation = new QueryContinuation
                {
                    Operation = "members",
                    Entity = target.CanonicalPath,
                    Scope = "declared_members",
                    Offset = offset + page.Count,
                    Limit = limit
                }
            });
            item.Message = "成员集合跨页；本页不宣称全集，请按 continuation 续读。";
        }

        return item;
    }


    private sealed record MemberEntry(
        string CanonicalPath,
        string Id,
        string Name,
        string Source,
        JsonElement? Content);

    private List<MemberEntry> FindTypeMembers(string game, ConceptPathHit target)
    {
        var results = new List<MemberEntry>();
        var targetId = RulesTextUtils.GetElementId(target.Element);
        var seenCandidatePaths = new HashSet<string>(StringComparer.Ordinal);
        var flowType = new HashSet<string>(StringComparer.Ordinal) { "flow", "top_level_refs", "slots" };

        foreach (var type in _catalog.GetConceptTypes(game))
        {
            if (flowType.Contains(type)) continue;
            foreach (var summary in _catalog.ListConcepts(game, type))
            {
                var candidatePath = string.IsNullOrWhiteSpace(summary.Path) ? summary.Id : summary.Path;
                if (string.IsNullOrWhiteSpace(candidatePath) || !seenCandidatePaths.Add(candidatePath)) continue;
                var candidateHits = RulesPlanEntityResolver.PreferGameIdentity(
                    candidatePath, _catalog.GetConceptsWithPaths(game, candidatePath).ToList());
                foreach (var candidate in candidateHits)
                {
                    if (candidate.CanonicalPath == target.CanonicalPath) continue;
                    if (ConceptRelationLinksTo(
                            game, candidate, target.CanonicalPath, targetId,
                            new HashSet<string>(StringComparer.Ordinal) { target.CanonicalPath },
                            out var source))
                    {
                        results.Add(new MemberEntry(
                            candidate.CanonicalPath,
                            RulesTextUtils.GetElementId(candidate.Element),
                            RulesPlanEntityResolver.ExtractConceptName(candidate.Element),
                            source,
                            candidate.Element.Clone()));
                    }
                }
            }
        }
        return results;
    }

    private bool ConceptRelationLinksTo(
        string game,
        ConceptPathHit candidate,
        string targetPath,
        string targetId,
        HashSet<string> visited,
        out string source)
    {
        source = "";
        if (!visited.Add(candidate.CanonicalPath)) return false;
        foreach (var relation in new[] { "specifies", "extends", "instance_of" })
        {
            if (candidate.Element.ValueKind != JsonValueKind.Object
                || !candidate.Element.TryGetProperty(relation, out var value))
                continue;
            foreach (var reference in ExtractConceptReferences(value))
            {
                if (ReferenceMatchesTarget(reference, targetPath, targetId))
                {
                    source = relation;
                    return true;
                }
                var resolvedRefs = _catalog.GetConceptsWithPaths(game, RulesPlanEntityResolver.NormalizeConceptRef(reference));
                foreach (var resolved in resolvedRefs)
                {
                    if (ConceptRelationLinksTo(
                            game, resolved, targetPath, targetId, visited, out var nestedSource))
                    {
                        source = relation + "->" + nestedSource;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static IEnumerable<string> ExtractConceptReferences(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                yield return id.GetString() ?? "";
            if (value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                yield return path.GetString() ?? "";
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var reference in ExtractConceptReferences(item))
                    yield return reference;
        }
    }

    private static bool ReferenceMatchesTarget(string reference, string targetPath, string targetId)
    {
        var normalized = RulesPlanEntityResolver.NormalizeConceptRef(reference);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (string.Equals(normalized, targetPath, StringComparison.Ordinal)) return true;
        // Ontology targets require a namespace-qualified relation to avoid mixing
        // a game concept with an ontology concept that happens to share an id.
        if (targetPath.StartsWith("ontology::", StringComparison.Ordinal)) return false;
        return string.Equals(normalized, targetId, StringComparison.Ordinal);
    }
}
