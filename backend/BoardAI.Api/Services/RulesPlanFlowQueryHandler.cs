using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>flow relation 的局部/整局结构查询。只依赖流程索引，不反向依赖编排服务。</summary>
internal sealed class RulesPlanFlowQueryHandler
{
    private readonly RulesFlowService _flowService;

    public RulesPlanFlowQueryHandler(RulesFlowService flowService)
    {
        _flowService = flowService;
    }

    internal PlanItemResult ExecuteFlowQueryAsync(
        string game, string entity, string scope, IReadOnlyList<string> include)
    {
        var normalizedScope = (scope ?? "").Trim().ToLowerInvariant();
        if (normalizedScope is not ("local" or "game"))
        {
            return new PlanItemResult
            {
                Relation = "flow",
                Entity = entity,
                Status = "invalid",
                Resolution = QueryResolutionStatuses.Invalid,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "flow 查询必须显式提供 scope=local 或 scope=game；scope=local 需要 entity。",
                Omissions =
                {
                    new QueryOmission
                    {
                        Reason = QueryOmissionReasons.InvalidRequest,
                        FieldPath = "scope",
                        Continuation = new QueryContinuation
                        {
                            Operation = "flow",
                            Entity = string.IsNullOrWhiteSpace(entity) ? null : entity,
                            Scope = "local|game"
                        }
                    }
                }
            };
        }

        var index = _flowService.GetFlowIndex(game);
        var version = index.RulesVersion;
        if (index.Root.ValueKind == JsonValueKind.Undefined)
        {
            return new PlanItemResult
            {
                Relation = "flow",
                Entity = entity,
                Status = "no_match",
                Resolution = QueryResolutionStatuses.NotFound,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "规则库中没有该游戏的流程定义。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.NoRuleCoverage, FieldPath = "flow" }
                }
            };
        }

        if (normalizedScope == "game")
        {
            if (!string.IsNullOrWhiteSpace(entity))
            {
                return new PlanItemResult
                {
                    Relation = "flow",
                    Entity = entity,
                    Status = "invalid",
                    Resolution = QueryResolutionStatuses.Invalid,
                    Coverage = QueryCoverageStatuses.Unknown,
                    Message = "scope=game 返回整局流程，不接受 entity；若要定位局部节点请使用 scope=local。",
                    Omissions =
                    {
                        new QueryOmission { Reason = QueryOmissionReasons.InvalidRequest, FieldPath = "entity" }
                    }
                };
            }

            var nodeSummary = index.AllNodes.Select(node => ToFlowSkeleton(node)).ToList();
            var data = new Dictionary<string, JsonElement>
            {
                ["scope"] = JsonSerializer.SerializeToElement("game"),
                ["node_count"] = JsonSerializer.SerializeToElement(index.AllNodes.Count),
                ["nodes"] = JsonSerializer.SerializeToElement(nodeSummary)
            };
            return new PlanItemResult
            {
                Relation = "flow",
                Entity = entity,
                Status = "ok",
                Resolution = QueryResolutionStatuses.Exact,
                Coverage = QueryCoverageStatuses.Complete,
                Source = "scope_game",
                ResolvedTargets = new List<string> { "game" },
                Payloads =
                {
                    new QueryPayload
                    {
                        Type = QueryPayloadTypes.FlowSlice,
                        CanonicalPath = "game",
                        RulesVersion = version,
                        Completeness = QueryCoverageStatuses.Complete,
                        Content = index.Root.Clone(),
                        Data = data
                    }
                },
                RetrievalQueries = new List<string>()
            };
        }

        if (string.IsNullOrWhiteSpace(entity))
        {
            return new PlanItemResult
            {
                Relation = "flow",
                Entity = entity,
                Status = "invalid",
                Resolution = QueryResolutionStatuses.Invalid,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "scope=local 必须提供 entity，程序不会把无目标局部查询静默退回整局流程。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.InvalidRequest, FieldPath = "entity" }
                }
            };
        }

        var resolved = index.Resolve(entity);
        if (resolved.Exact.Count > 1)
        {
            return FlowCandidatesResult(entity, resolved.Exact, "局部 id 命中多个规范流程路径，请选择 canonical_path。");
        }
        if (resolved.Exact.Count == 0)
        {
            if (resolved.Candidates.Count > 0)
                return FlowCandidatesResult(entity, resolved.Candidates);
            return new PlanItemResult
            {
                Relation = "flow",
                Entity = entity,
                Status = "no_match",
                Resolution = QueryResolutionStatuses.NotFound,
                Coverage = QueryCoverageStatuses.Unknown,
                Message = "未在流程结构中找到该目标。",
                Omissions =
                {
                    new QueryOmission { Reason = QueryOmissionReasons.NoRuleCoverage, FieldPath = "entity" }
                }
            };
        }

        var node = resolved.Exact[0];
        var childNodes = node.ChildPaths
            .Select(path => index.ByPath.GetValueOrDefault(path))
            .Where(child => child != null)
            .Cast<FlowNodeInfo>()
            .ToList();
        var parent = node.ParentPath != null ? index.ByPath.GetValueOrDefault(node.ParentPath) : null;
        var siblings = parent?.ChildPaths
            .Select(path => index.ByPath.GetValueOrDefault(path))
            .Where(sibling => sibling != null)
            .Cast<FlowNodeInfo>()
            .ToList() ?? new List<FlowNodeInfo>();
        var dataLocal = new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement("local"),
            ["target"] = JsonSerializer.SerializeToElement(ToFlowSkeleton(node, includeContent: false)),
            ["ancestors"] = JsonSerializer.SerializeToElement(node.Ancestors.Select(ToFlowSkeleton).ToList()),
            ["parent"] = parent == null
                ? JsonSerializer.SerializeToElement<object?>(null)
                : JsonSerializer.SerializeToElement(ToFlowSkeleton(parent, includeContent: false)),
            ["siblings"] = JsonSerializer.SerializeToElement(siblings.Select(item => ToFlowSkeleton(item, includeContent: false)).ToList()),
            ["children"] = JsonSerializer.SerializeToElement(childNodes.Select(item => ToFlowSkeleton(item, includeContent: true)).ToList()),
            ["child_count"] = JsonSerializer.SerializeToElement(childNodes.Count),
            ["can_expand_children"] = JsonSerializer.SerializeToElement(childNodes.Count > 0)
        };
        return new PlanItemResult
        {
            Relation = "flow",
            Entity = entity,
            Status = "ok",
            Resolution = QueryResolutionStatuses.Exact,
            Coverage = QueryCoverageStatuses.Complete,
            Source = "canonical_path",
            ResolvedTargets = new List<string> { node.CanonicalPath },
            Payloads =
            {
                new QueryPayload
                {
                    Type = QueryPayloadTypes.FlowSlice,
                    CanonicalPath = node.CanonicalPath,
                    RulesVersion = version,
                    Completeness = QueryCoverageStatuses.Complete,
                    Content = node.Element.Clone(),
                    Data = dataLocal
                }
            },
            RetrievalQueries = new List<string> { entity }
        };
    }

    private PlanItemResult FlowCandidatesResult(
        string entity, IEnumerable<FlowNodeInfo> candidates, string? message = null)
    {
        var summaries = candidates
            .Select(node => new ConceptSummary
            {
                Id = node.CanonicalPath,
                Path = node.CanonicalPath,
                Name = node.Name,
                Type = node.Kind,
                Description = $"局部 id={node.LocalId}"
            })
            .ToList();
        return new PlanItemResult
        {
            Relation = "flow",
            Entity = entity,
            Status = "unresolved",
            Resolution = QueryResolutionStatuses.Candidates,
            Coverage = QueryCoverageStatuses.Unknown,
            Candidates = summaries,
            Message = message ?? "多个局部流程节点可能匹配；规范路径才是身份，请用 canonical_path 重新查询。",
            Omissions =
            {
                new QueryOmission { Reason = QueryOmissionReasons.AmbiguousIdentity, FieldPath = "entity" }
            }
        };
    }

    private PlanItemResult FlowCandidatesResult(
        string entity, IEnumerable<FlowTargetCandidate> candidates, string? message = null)
        => FlowCandidatesResult(entity, candidates.Select(candidate => new FlowNodeInfo
        {
            CanonicalPath = candidate.CanonicalPath,
            LocalId = candidate.Id,
            Name = candidate.Name,
            Kind = candidate.Kind,
            Element = default
        }), message);

    private static Dictionary<string, object?> ToFlowSkeleton(FlowNodeInfo node, bool includeContent = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["canonical_path"] = node.CanonicalPath,
            ["id"] = node.LocalId,
            ["name"] = node.Name,
            ["kind"] = node.Kind,
            ["index"] = node.Index,
            ["optional"] = node.Optional,
            ["do_after"] = node.DoAfter,
            ["loop"] = node.Loop,
            ["condition"] = node.Condition,
            ["child_paths"] = node.ChildPaths
        };
        if (includeContent)
            result["content"] = node.Element.ValueKind == JsonValueKind.Undefined ? null : node.Element.Clone();
        return result;
    }

    private static Dictionary<string, object?> ToFlowSkeleton(FlowAncestorInfo ancestor) => new()
    {
        ["canonical_path"] = ancestor.CanonicalPath,
        ["id"] = ancestor.LocalId,
        ["name"] = ancestor.Name,
        ["kind"] = ancestor.Kind,
        ["loop"] = ancestor.Loop,
        ["condition"] = ancestor.Condition
    };
}
