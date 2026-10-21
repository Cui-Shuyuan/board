using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 已解析查询结果的正规构造器：负责定义负载、声明依赖、流程位置、关系字段、
/// 事实卡与结果契约归一化所需的基础 payload。不解析实体或调用查询处理器。
/// </summary>
internal sealed class RulesPlanResultBuilder
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesReferenceService _referenceService;
    private readonly RulesFlowService _flowService;
    private readonly RulesFactService _factService;

    public RulesPlanResultBuilder(
        RulesConceptCatalog catalog,
        RulesReferenceService referenceService,
        RulesFlowService flowService,
        RulesFactService factService)
    {
        _catalog = catalog;
        _referenceService = referenceService;
        _flowService = flowService;
        _factService = factService;
    }

    /// <summary>
    /// 把已解析的概念构造成 ok 结果：引用扩展数据 + 流程位置 + 关系专属字段。
    /// 精确解析、问题级直呼与「高置信语义候选自动解析」共用此路径（2026-08-16 提取）。
    /// 多命中（基名/别名/问题直呼）时 Matched 返回全部命中概念，Related 为各概念
    /// 直接引用的并集——多概念共享的事实（如播种与谷物）一次性给全。
    /// </summary>
    internal PlanItemResult BuildOkResult(
        string game,
        string relation,
        string entity,
        List<ConceptPathHit> matched,
        string source,
        string question,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? retrievalQueries = null)
    {
        using var buildSpan = RuleRuntimeTelemetry.Measure("assembly", "build_ok_result");
        cancellationToken.ThrowIfCancellationRequested();
        var matchedElements = matched.Select(hit => hit.Element).ToList();
        var resolvedId = RulesTextUtils.GetElementId(matchedElements[0]);
        var rulesVersion = RulesSnapshotScope.Current is { } scope && scope.Game == game
            ? scope.Version
            : null;

        var item = new PlanItemResult
        {
            Relation = relation,
            Entity = entity,
            Status = "ok",
            Resolution = QueryResolutionStatuses.Exact,
            Source = source,
            ResolvedTargets = matched.Select(hit => hit.CanonicalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Coverage = QueryCoverageStatuses.Complete,
            Matched = matchedElements,
            Related = _referenceService.ExpandRelated(game, matchedElements, light: source == "question_hit"),
            RetrievalQueries = retrievalQueries?.ToList() ?? new List<string>()
        };

        // 每个规范路径都交付一个完整定义负载；正文不再从 matched 再复制一次给模型。
        foreach (var hit in matched)
        {
            item.Payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.Definition,
                CanonicalPath = hit.CanonicalPath,
                RulesVersion = rulesVersion,
                Completeness = QueryCoverageStatuses.Complete,
                DefinitionSource = source,
                Content = hit.Element.Clone()
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var localId = resolvedId.Contains("::") ? resolvedId[(resolvedId.IndexOf("::", StringComparison.Ordinal) + 2)..] : resolvedId;
        _flowService.GetFlowPositions(game).TryGetValue(localId, out var pos);
        cancellationToken.ThrowIfCancellationRequested();

        // 流程位置链（flow 节点才有）：回答「在哪个阶段/回合」类语境
        if (pos != null)
            item.FlowContext = pos.Ancestors;

        // condition/cost/target 关系：读取所属节点的对应字段，并只按显式
        // 概念引用展开一层正式定义；文本提到不自动升级为效果/收益关系。
        if (relation is "condition" or "cost" or "target" && matched.Count > 0)
        {
            var el = matched[0].Element;
            var includeCondition = relation == "condition";
            var includeCost = relation is "condition" or "cost";
            var includeTarget = relation is "condition" or "target";
            item.Condition = includeCondition ? ExtractTopField(el, "<ontology::condition>") : null;
            item.Cost = includeCost ? ExtractTopField(el, "<ontology::cost>") : null;
            item.Target = includeTarget ? ExtractTopField(el, "target") : null;

            var fields = new Dictionary<string, JsonElement>();
            if (item.Condition.HasValue) fields["condition"] = item.Condition.Value;
            if (item.Cost.HasValue) fields["cost"] = item.Cost.Value;
            if (item.Target.HasValue) fields["target"] = item.Target.Value;

            var edges = new List<object>();
            var dependencyPaths = new HashSet<string>(StringComparer.Ordinal);
            var unresolvedReferences = new List<string>();
            var dependencyBudgetExceeded = false;
            const int maxDependencyReads = 8;
            foreach (var (field, value) in fields)
            {
                var raw = JsonSerializer.Serialize(value, RulesJsonUtils.RelaxedJsonOptions);
                foreach (System.Text.RegularExpressions.Match match in RulesJsonUtils.ConceptRefRegex.Matches(raw))
                {
                    var reference = RulesPlanEntityResolver.NormalizeConceptRef(match.Groups[1].Value);
                    if (string.IsNullOrWhiteSpace(reference)) continue;
                    var hits = RulesPlanEntityResolver.PreferGameIdentity(
                        reference, _catalog.GetConceptsWithPaths(game, reference).ToList());
                    if (hits.Count == 0)
                    {
                        if (!unresolvedReferences.Contains(reference, StringComparer.Ordinal))
                            unresolvedReferences.Add(reference);
                        edges.Add(new
                        {
                            field,
                            edge_type = field,
                            reference,
                            canonical_path = (string?)null,
                            target_resolved = false,
                            binding = (string?)null
                        });
                        continue;
                    }

                    foreach (var hit in hits)
                    {
                        edges.Add(new
                        {
                            field,
                            edge_type = field,
                            reference,
                            canonical_path = hit.CanonicalPath,
                            target_resolved = true,
                            binding = matched[0].CanonicalPath + ":" + field
                        });
                        if (dependencyPaths.Count >= maxDependencyReads)
                        {
                            dependencyBudgetExceeded = true;
                            break;
                        }
                        if (!dependencyPaths.Add(hit.CanonicalPath)) continue;
                        item.Payloads.Add(new QueryPayload
                        {
                            Type = QueryPayloadTypes.Definition,
                            CanonicalPath = hit.CanonicalPath,
                            RulesVersion = rulesVersion,
                            Completeness = QueryCoverageStatuses.Complete,
                            DefinitionSource = "declared_dependency",
                            Content = hit.Element.Clone(),
                            Data = new Dictionary<string, JsonElement>
                            {
                                ["role"] = JsonSerializer.SerializeToElement("dependency"),
                                ["edge_type"] = JsonSerializer.SerializeToElement(field),
                                ["parent_path"] = JsonSerializer.SerializeToElement(matched[0].CanonicalPath),
                                ["target_resolved"] = JsonSerializer.SerializeToElement(true),
                                ["binding"] = JsonSerializer.SerializeToElement(matched[0].CanonicalPath + ":" + field)
                            }
                        });
                    }
                    if (dependencyBudgetExceeded) break;
                }
                if (dependencyBudgetExceeded) break;
            }

            if (edges.Count > 0)
                fields["reference_edges"] = JsonSerializer.SerializeToElement(edges);
            var relationCoverage = fields.Count == 0
                || unresolvedReferences.Count > 0
                || dependencyBudgetExceeded
                    ? QueryCoverageStatuses.Partial
                    : QueryCoverageStatuses.Complete;
            item.Payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.RelationFacts,
                CanonicalPath = matched[0].CanonicalPath,
                RulesVersion = rulesVersion,
                Completeness = relationCoverage,
                Data = fields.Count > 0 ? fields : null
            });
            if (fields.Count == 0)
            {
                item.Coverage = QueryCoverageStatuses.Partial;
                item.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.FieldMissing,
                    CanonicalPath = matched[0].CanonicalPath,
                    FieldPath = relation
                });
            }
            if (unresolvedReferences.Count > 0)
            {
                item.Coverage = QueryCoverageStatuses.Partial;
                foreach (var reference in unresolvedReferences)
                {
                    item.Omissions.Add(new QueryOmission
                    {
                        Reason = QueryOmissionReasons.NoRuleCoverage,
                        CanonicalPath = matched[0].CanonicalPath,
                        FieldPath = relation + ":" + reference
                    });
                }
            }
            if (dependencyBudgetExceeded)
            {
                item.Coverage = QueryCoverageStatuses.Partial;
                item.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.BudgetTruncated,
                    CanonicalPath = matched[0].CanonicalPath,
                    FieldPath = relation + ".dependencies",
                    Continuation = new QueryContinuation
                    {
                        Operation = "explain",
                        Entity = matched[0].CanonicalPath,
                        Offset = maxDependencyReads,
                        Limit = maxDependencyReads
                    }
                });
            }
        }

        // ordering 关系：同级选项顺序 + 位置 + 循环结构（回答「之后是什么/何时结束」）
        if (relation == "ordering")
        {
            if (pos != null)
            {
                item.Siblings = pos.Siblings;
                item.PositionIndex = pos.Index;
                item.Loop = pos.Loop;
                var fields = new Dictionary<string, JsonElement>();
                if (pos.Siblings.Count > 0)
                    fields["siblings"] = JsonSerializer.SerializeToElement(pos.Siblings);
                if (pos.Index >= 0)
                    fields["position_index"] = JsonSerializer.SerializeToElement(pos.Index);
                if (pos.Loop.HasValue)
                    fields["loop"] = pos.Loop.Value.Clone();
                item.Payloads.Add(new QueryPayload
                {
                    Type = QueryPayloadTypes.RelationFacts,
                    CanonicalPath = matched[0].CanonicalPath,
                    RulesVersion = rulesVersion,
                    Completeness = QueryCoverageStatuses.Complete,
                    Data = fields
                });
            }
            else
            {
                item.Coverage = QueryCoverageStatuses.Partial;
                item.Omissions.Add(new QueryOmission
                {
                    Reason = QueryOmissionReasons.FieldMissing,
                    CanonicalPath = matched[0].CanonicalPath,
                    FieldPath = "flow_position"
                });
            }
        }

        // boundary 关系：溢出/下溢/圈事件等边界字段 + 缺省语义说明
        if (relation == "boundary" && matched.Count > 0)
        {
            var el = matched[0].Element;
            var boundary = new Dictionary<string, JsonElement>();
            foreach (var key in new[]
                     {
                         "<ontology::overflow_compensation>", "<ontology::underflow_compensation>",
                         "<ontology::lap_event>", "capacity", "loop"
                     })
            {
                var v = ExtractTopField(el, key);
                if (v.HasValue) boundary[key] = v.Value;
            }
            item.Boundary = boundary.Count > 0 ? boundary : null;
            item.Payloads.Add(new QueryPayload
            {
                Type = QueryPayloadTypes.RelationFacts,
                CanonicalPath = matched[0].CanonicalPath,
                RulesVersion = rulesVersion,
                Completeness = QueryCoverageStatuses.Complete,
                Data = boundary
            });
            item.Message = "未声明溢出/下溢补偿的轨道：缺省语义=该方向无事发生或该方向不会发生（见 ontology <ontology::overflow_compensation> 定义）。";
        }

        // 事实卡（广播第三站）：数量/计分题的程序化计算——容量公式求值、计分表区间查表、
        // quantity.numeric 分支选择与求和。程序算得出的数不交给 LLM 从散文里读（1+1 交给计算器）。
        // 问题不含数量词/分数词、或命中概念无结构化数据时返回 null，序列化时省略。
        if (!string.IsNullOrWhiteSpace(question))
            item.Facts = _factService.ExtractFacts(game, matchedElements, question);
        if (item.Facts is { Count: > 0 })
        {
            foreach (var fact in item.Facts)
            {
                item.Payloads.Add(new QueryPayload
                {
                    Type = QueryPayloadTypes.ComputedFact,
                    CanonicalPath = matched[0].CanonicalPath,
                    RulesVersion = rulesVersion,
                    Completeness = QueryCoverageStatuses.Complete,
                    Content = fact.Clone()
                });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return item;
    }


    /// <summary>提取概念顶层的指定字段（无则 null）。</summary>
    private static JsonElement? ExtractTopField(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v)
            ? v
            : null;
    }
}
