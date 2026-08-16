using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public class GameRulesService
{
    private readonly string _basePath;
    private readonly VectorSearchService? _vectorSearch;
    private readonly Dictionary<string, JsonDocument> _loadedFiles = new();

    /// <summary>概念引用正则——注解（AnnotateReferences）与一层扩展（GetConceptsWithExpansion）共用。</summary>
    private static readonly Regex ConceptRefRegex = new(
        @"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>",
        RegexOptions.Compiled);

    /// <summary>不转义 &lt;&gt; 的序列化选项——引用提取必须看到字面 &lt;concept_id&gt;（与工具返回的 ToolResultOptions 同理）。</summary>
    private static readonly JsonSerializerOptions RelaxedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] ConceptArrayTypes = { "objects", "actions", "triggers", "conditions" };
    private static readonly string[] InstanceArrayTypes = { "effects", "modules", "cards", "continent_tiles", "sites", "chips" };

    public GameRulesService(IOptions<RulesOptions> options, VectorSearchService? vectorSearch = null)
    {
        _basePath = options.Value.BasePath;
        if (string.IsNullOrWhiteSpace(_basePath))
        {
            throw new InvalidOperationException("Rules:BasePath is not configured.");
        }
        _vectorSearch = vectorSearch;
    }

    public IReadOnlyList<string> GetGames()
    {
        var gamesDir = Path.Combine(_basePath, "games");
        if (!Directory.Exists(gamesDir)) return Array.Empty<string>();
        return Directory.GetDirectories(gamesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList()!;
    }

    public IReadOnlyList<string> GetConceptTypes(string game)
    {
        var types = new List<string> { "ontology" };
        var concepts = LoadGameConcepts(game);
        if (concepts != null)
        {
            foreach (var type in ConceptArrayTypes)
            {
                if (concepts.RootElement.TryGetProperty(type, out _))
                    types.Add(type);
            }
            types.Add("top_level_refs");
            types.Add("flow");
        }
        var instances = LoadGameInstances(game);
        if (instances != null)
        {
            foreach (var type in InstanceArrayTypes)
            {
                if (instances.RootElement.TryGetProperty(type, out var arr) && arr.GetArrayLength() > 0)
                    types.Add(type);
            }
        }
        return types;
    }

    public IReadOnlyList<ConceptSummary> ListConcepts(string game, string type)
    {
        if (type == "ontology")
        {
            var ontology = LoadOntology();
            return ExtractArrayConcepts(ontology, "concepts");
        }

        if (type == "flow")
        {
            var flow = LoadGameFlow(game);
            return flow == null ? Array.Empty<ConceptSummary>() : ExtractFlowConcepts(flow);
        }

        var concepts = LoadGameConcepts(game);
        if (concepts == null) return Array.Empty<ConceptSummary>();

        if (type == "top_level_refs")
        {
            var results = new List<ConceptSummary>();
            foreach (var key in GetTopLevelRefKeys(concepts))
            {
                if (concepts.RootElement.TryGetProperty(key, out var element))
                {
                    results.Add(new ConceptSummary
                    {
                        Id = key,
                        Name = ExtractName(element),
                        Type = "top_level_ref"
                    });
                }
            }
            return results;
        }

        if (ConceptArrayTypes.Contains(type))
        {
            return ExtractArrayConcepts(concepts, type);
        }

        if (InstanceArrayTypes.Contains(type))
        {
            var instances = LoadGameInstances(game);
            return instances == null ? Array.Empty<ConceptSummary>() : ExtractArrayConcepts(instances, type);
        }

        if (type == "slots")
        {
            var results = new List<ConceptSummary>();
            void CollectSlots(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    if (node.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var slot in slots.EnumerateArray())
                        {
                            if (slot.ValueKind != JsonValueKind.Object) continue;
                            foreach (var prop in slot.EnumerateObject())
                            {
                                if (prop.Name.StartsWith("<")) continue;
                                results.Add(new ConceptSummary { Id = prop.Name, Name = "", Type = "slot" });
                            }
                        }
                    }
                    foreach (var prop in node.EnumerateObject())
                        CollectSlots(prop.Value);
                }
                else if (node.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in node.EnumerateArray())
                        CollectSlots(item);
                }
            }
            CollectSlots(concepts.RootElement);
            var inst = LoadGameInstances(game);
            if (inst != null) CollectSlots(inst.RootElement);
            return results;
        }

        return Array.Empty<ConceptSummary>();
    }

    public IReadOnlyList<JsonElement> GetConcepts(string game, string id)
    {
        var results = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(id)) return results;

        // Parse namespace prefix if present, e.g. "ontology::resource"
        var (ns, localId) = ParseNamespace(id);

        // Search ontology when no namespace or explicitly "ontology"
        if (string.IsNullOrEmpty(ns) || ns == "ontology")
        {
            var ontology = LoadOntology();
            if (TryFindInArray(ontology.RootElement, "concepts", localId, out var ontologyConcept))
                results.Add(ontologyConcept);
        }

        // Search game concepts when no namespace
        if (string.IsNullOrEmpty(ns))
        {
            var concepts = LoadGameConcepts(game);
            if (concepts != null)
            {
                foreach (var type in ConceptArrayTypes)
                {
                    if (TryFindInArray(concepts.RootElement, type, localId, out var concept))
                        results.Add(concept);
                }

                foreach (var key in GetTopLevelRefKeys(concepts))
                {
                    if (key == localId && concepts.RootElement.TryGetProperty(key, out var topLevel))
                        results.Add(topLevel);
                }
            }

            // Search flow
            var flow = LoadGameFlow(game);
            if (flow != null)
            {
                if (TryFindFlowProcedure(flow.RootElement, localId, out var procedure))
                    results.Add(procedure);
            }

            // Search instances (effects, modules, cards, tiles, sites, chips)
            var instances = LoadGameInstances(game);
            if (instances != null)
            {
                foreach (var type in InstanceArrayTypes)
                {
                    if (TryFindInArray(instances.RootElement, type, localId, out var instance))
                        results.Add(instance);
                }
            }

            // Search slots (bare-key slot names within concepts, e.g. population/expansion)
            if (concepts != null)
            {
                var slot = FindSlot(concepts.RootElement, localId);
                if (slot.HasValue)
                    results.Add(slot.Value);
            }
        }

        return results;
    }

    /// <summary>递归在 slots 数组中查找裸键槽位 (与 Python rebuild_index 的 slot 提取一致)</summary>
    private static JsonElement? FindSlot(JsonElement node, string slotId)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var slot in slots.EnumerateArray())
                {
                    if (slot.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in slot.EnumerateObject())
                    {
                        if (prop.Name == slotId)
                            return prop.Value;
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
            {
                var found = FindSlot(prop.Value, slotId);
                if (found.HasValue) return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var found = FindSlot(item, slotId);
                if (found.HasValue) return found;
            }
        }
        return null;
    }

    public JsonElement? GetConcept(string game, string id)
    {
        var concepts = GetConcepts(game, id);
        return concepts.Count > 0 ? concepts[0] : null;
    }

    public IReadOnlyList<JsonElement> GetActionConditions(string game, string actionId)
    {
        var action = GetConcept(game, actionId);
        if (!action.HasValue) return Array.Empty<JsonElement>();

        var result = new List<JsonElement>();

        if (action.Value.TryGetProperty("<trigger>", out var triggerRef))
        {
            JsonElement triggerElement;
            if (triggerRef.ValueKind == JsonValueKind.String)
            {
                var triggerId = triggerRef.GetString()?.Trim('<', '>');
                if (string.IsNullOrEmpty(triggerId)) return result;
                var trigger = GetConcept(game, triggerId);
                if (!trigger.HasValue) return result;
                triggerElement = trigger.Value;
            }
            else if (triggerRef.ValueKind == JsonValueKind.Object)
            {
                triggerElement = triggerRef;
            }
            else
            {
                return result;
            }

            if (triggerElement.TryGetProperty("<condition>", out var triggerCondition))
            {
                var conditionId = triggerCondition.GetString()?.Trim('<', '>');
                if (!string.IsNullOrEmpty(conditionId))
                {
                    var condition = GetConcept(game, conditionId);
                    if (condition.HasValue)
                        result.Add(condition.Value);
                }
            }
        }

        return result;
    }

    private readonly Dictionary<string, Dictionary<string, string>> _nameMaps = new();

    /// <summary>
    /// 把文本中的概念引用 <concept_id> / <ontology::concept_id> 注解为 <id>(中文名)，
    /// 让 LLM 无需自行翻译英文 id（如 <idea_marker>(创意标记)）。
    /// 查不到映射的引用（枚举值、未知 id）保持原样。
    /// </summary>
    public string AnnotateReferences(string text, string game)
    {
        var map = GetNameMap(game);
        if (map.Count == 0) return text;
        return ConceptRefRegex.Replace(
            text,
            m =>
            {
                var raw = m.Groups[1].Value;
                var localId = raw.Contains("::") ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..] : raw;
                if (map.TryGetValue(localId, out var name) && !string.IsNullOrEmpty(name))
                    return $"<{raw}>({name})";
                return m.Value;
            });
    }

    /// <summary>
    /// get_concept 的一层引用扩展：matched 概念直接引用的概念一并返回为 related。
    /// 引用提取与注解共用同一个正则；能解析（GetConcepts 查得到）的才扩展，
    /// 排除自身与 matched 集合；related 内部的引用不再递归扩展。
    /// 按首现顺序最多扩展 MaxRelatedConcepts 个，超出截断并在 Note 提示。
    /// </summary>
    private const int MaxRelatedConcepts = 10;

    public GetConceptResult GetConceptsWithExpansion(string game, string id)
    {
        var matched = GetConcepts(game, id).ToList();
        if (matched.Count == 0)
            return new GetConceptResult();

        var (_, localId) = ParseNamespace(id);

        // 排除集合：查询概念自身 + 已命中的概念（避免 related 里出现 matched 副本）
        var excluded = new HashSet<string>(StringComparer.Ordinal) { localId };
        foreach (var element in matched)
        {
            var matchedId = GetElementId(element);
            if (!string.IsNullOrEmpty(matchedId))
                excluded.Add(matchedId);
        }

        var related = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 已扩展概念 id——不同引用串（如 <score_track> 与 <ontology::score_track>）可能解析到同一概念，按解析结果去重
        var appendedIds = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var element in matched)
        {
            var text = JsonSerializer.Serialize(element, RelaxedJsonOptions);
            foreach (Match m in ConceptRefRegex.Matches(text))
            {
                var raw = m.Groups[1].Value;
                if (!seen.Add(raw)) continue;

                var local = raw.Contains("::")
                    ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..]
                    : raw;
                if (excluded.Contains(local)) continue;

                foreach (var found in GetConcepts(game, raw))
                {
                    var foundId = GetElementId(found);
                    if (!string.IsNullOrEmpty(foundId) && !appendedIds.Add(foundId))
                        continue; // 已扩展过该概念
                    if (related.Count >= MaxRelatedConcepts)
                    {
                        truncated = true;
                        break;
                    }
                    related.Add(found);
                }
                if (truncated) break;
            }
            if (truncated) break;
        }

        var result = new GetConceptResult { Matched = matched, Related = related };
        if (truncated)
        {
            result.Note +=
                $"本次直接引用较多，related 仅返回首现顺序的前 {MaxRelatedConcepts} 个；其余引用请用具体 ID 继续调用 get_concept。";
        }
        return result;
    }

    /// <summary>读取概念的 id 属性（无则空字符串）。</summary>
    private static string GetElementId(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
    }

    // ---- 查询计划（execute_plan）----

    /// <summary>
    /// 执行 LLM 提交的查询计划。各 relation 均为确定性数据导航：
    /// explain/condition/ordering/boundary 按实体解析（精确 id → 精确名/别名/基名 →
    /// 问题原文直呼 → 名称包含候选）；identify 按外观/位置描述搜索候选；
    /// flow/list 不需要实体。question 为客人问题原文——实体解析失败时程序直接扫原文
    /// 找概念名（「广播」第二站），不依赖 LLM 的转述质量。
    /// </summary>
    public async Task<PlanExecutionResult> ExecutePlanAsync(string game, JsonElement plan, string question = "")
    {
        var result = new PlanExecutionResult();
        if (!plan.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Array)
        {
            result.Note = "plan 缺少 queries 数组。";
            return result;
        }

        foreach (var q in queries.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object) continue;
            var relation = q.TryGetProperty("relation", out var rp) ? rp.GetString() ?? "" : "";
            var entity = q.TryGetProperty("entity", out var ep) ? ep.GetString() ?? "" : "";
            result.Results.Add(await ExecutePlanQueryAsync(game, relation, entity, question));
        }

        if (result.Results.Any(r => r.Status is "unresolved" or "unsupported" or "no_match"))
        {
            var notes = new List<string>();
            if (result.Results.Any(r => r.Status == "unresolved"))
                notes.Add("unresolved 的查询请用候选中的确切 id 或名字重试");
            if (result.Results.Any(r => r.Status == "no_match"))
                notes.Add("no_match 的查询表示规则库无相近概念，该部分只能自行发挥（详见查询 Message）");
            if (result.Results.Any(r => r.Status == "unsupported"))
                notes.Add("unsupported 的 relation 请改用 identify 或 list");
            result.Note = "部分查询未完成：" + string.Join("；", notes) + "。";
        }
        return result;
    }

    private static readonly HashSet<string> PlanRelations = new(StringComparer.Ordinal)
    {
        "explain", "condition", "ordering", "boundary", "flow", "list", "identify"
    };

    /// <summary>语义候选的最低可信分数——低于此分数视为「规则库查不到」（tier 3）。
    /// 分数为多通道累加归一化值（向量余弦 + 关键词小幅加成）。关键词加成已降级
    /// （名字命中 +0.3、内容命中 +0.05），仅凭关键词无法过阈值——过线的概念必须
    /// 有真实向量语义支撑。实测校准（2026-08-16，name 集合，bge-base-zh-v1.5 量化版）：
    /// 噪声带 0.36–0.47（无意义词「小精灵」top=0.466 全是无关概念），可靠匹配 ≥0.53
    /// （「钱币」=0.701、「招募官」=0.638、转述「领工人的角色」→ worker=0.672）。
    /// 经典版别名（杜布隆/市长/殖民者/探矿者）语义匹配全部失败——这类映射必须走
    /// 数据层 aliases 精确匹配，向量兜底只对自然语言转述有效。阈值取 0.50：
    /// 噪声与信号的实测分界，宁漏勿错。</summary>
    private const float SemanticCandidateThreshold = 0.50f;

    private async Task<PlanItemResult> ExecutePlanQueryAsync(string game, string relation, string entity, string question = "")
    {
        if (!PlanRelations.Contains(relation))
        {
            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "unsupported",
                Message = "该 relation 不在支持列表（explain/condition/ordering/boundary/flow/list/identify）。"
            };
        }

        // identify：客人用外观/位置描述某物时，按描述搜索候选概念（带定义）。
        // 问题原文直接命名了已知概念时（含别名/基名），直接返回该概念——比候选列表更确定
        if (relation == "identify")
        {
            var qhits = ResolveFromQuestion(game, question, out var qsource);
            if (qhits.Count > 0)
                return BuildOkResult(game, relation, entity, qhits, qsource);

            var search = await SearchConceptsAsync(game, entity);
            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "ok",
                Candidates = search.Results,
                Message = "按描述匹配的候选概念（含定义与匹配分数）。请挑出与客人描述最吻合的一个，用其 id 或中文名发起 explain/condition 查询；若都不吻合，请继续向客人确认细节。"
            };
        }

        // flow/list 不需要实体解析
        if (relation == "flow")
        {
            var gameConcept = GetConcepts(game, "game");
            return gameConcept.Count > 0
                ? new PlanItemResult { Relation = relation, Entity = entity, Status = "ok", Matched = gameConcept.ToList() }
                : new PlanItemResult
                {
                    Relation = relation,
                    Entity = entity,
                    Status = "no_match",
                    Message = "规则库中没有该游戏的流程定义，无法返回任何流程数据。此问题只能由你基于自己的知识自行发挥——请谨慎回答，并建议向客人说明这是规则库之外的信息。"
                };
        }
        if (relation == "list")
        {
            return new PlanItemResult { Relation = relation, Entity = entity, Status = "ok", Catalog = ListAllConceptIds(game) };
        }

        // 实体解析：精确 id → 精确名/别名/基名（直呼工具）→ 名称包含候选
        var matched = ResolvePlanEntity(game, entity, out var candidates, out var source);
        if (matched.Count == 0)
        {
            // 问题级直呼（广播第二站）：实体转述失败时扫客人问题原文。
            // 命中即拍板（source=question_hit），返回的概念就是程序给出的全部事实——
            // related 轻量化不展开本体概念，减少 LLM 接收的噪声
            var qhits = ResolveFromQuestion(game, question, out var qsource);
            if (qhits.Count > 0)
                return BuildOkResult(game, relation, entity, qhits, qsource);
            // Tier 2：程序自己跑语义检索补候选（相似度匹配交给向量库，不交给 LLM）。
            // 名称包含的确定性候选无条件保留；语义候选须过分数阈值。
            var merged = new List<ConceptSummary>(candidates);
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < merged.Count; i++) indexById[merged[i].Id] = i;
            if (_vectorSearch != null)
            {
                var semantic = await SearchConceptsAsync(game, entity, searchMode: "name");
                foreach (var c in semantic.Results.Where(r => r.Score >= SemanticCandidateThreshold))
                {
                    if (indexById.TryGetValue(c.Id, out var i))
                    {
                        // 同一 id 的确定性候选（名称包含，分 0）与语义结果并存时取高分——
                        // 先到先得会把语义高分丢成 0，正确概念垫底、噪声概念霸榜（2026-08-16 实测）
                        if (c.Score > merged[i].Score) merged[i] = c;
                    }
                    else
                    {
                        indexById[c.Id] = merged.Count;
                        merged.Add(c);
                        if (merged.Count >= 8) break;
                    }
                }
            }
            // 更新/合并后重新排序，保证候选列表按分数降序呈现给 LLM
            merged = merged.OrderByDescending(c => c.Score).ToList();

            if (merged.Count > 0)
            {
                // 高置信语义候选直接解析为命中（2026-08-16 QA 实测）：LLM 对 tier2 候选
                // 常不重新查询、直接凭记忆作答（18/55 题 C 类）。程序自己判定：top1 显著
                // 领先且过线时无需 LLM 二次确认，直接取 top1 概念的数据——「程序自己推理」
                // 的比重由此扩大。阈值用 QA 全量候选分布校准：低分或胶着区间不得自动解析
                // （错把 scoring_pad 当计分、错把 accumulation_refill 当累积空间都是胶着区间）。
                var autoTop = merged[0];
                var autoGap = merged.Count > 1 ? autoTop.Score - merged[1].Score : float.PositiveInfinity;
                if ((autoTop.Score >= 0.72f && autoGap >= 0.08f)
                    || (autoGap >= 0.15f && autoTop.Score >= 0.60f)
                    || (autoTop.Score >= 1.0f && autoGap >= 0.05f))
                {
                    var autoMatched = GetConcepts(game, autoTop.Id).ToList();
                    if (autoMatched.Count > 0)
                        return BuildOkResult(game, relation, entity, autoMatched, "auto_semantic");
                }

                return new PlanItemResult
                {
                    Relation = relation,
                    Entity = entity,
                    Status = "unresolved",
                    Candidates = merged,
                    Message = "实体未精确命中——程序已检索出以下候选概念（含相似度分）。请从中挑选确切的概念，用它的 id 或中文名重新发起查询；若候选都不合适，再用 list 浏览全部概念。"
                };
            }

            // Tier 3：确定性匹配与语义检索都没有可信结果——显式告知 LLM 规则库无此信息，
            // 让「自行发挥」成为一个被程序声明、可观测的状态，而不是 LLM 的隐式选择。
            // 消息先给恢复路径（list/identify），再允许记忆兜底——避免 LLM 放弃得太早。
            return new PlanItemResult
            {
                Relation = relation,
                Entity = entity,
                Status = "no_match",
                Message = $"程序未能在规则库中找到与「{entity}」足够相近的概念，本查询无法获得任何规则数据。请先尝试：一、用 list 浏览概念目录确认是否真的没有相近概念（也许名字不同）；二、用 identify 按外观或位置描述再找一次。若确认规则库中没有此概念，此问题只能由你基于自己的知识自行发挥——请谨慎回答，并向客人说明这是规则库之外的信息。"
            };
        }

        return BuildOkResult(game, relation, entity, matched, source);
    }

    /// <summary>
    /// 把已解析的概念构造成 ok 结果：引用扩展数据 + 流程位置 + 关系专属字段。
    /// 精确解析、问题级直呼与「高置信语义候选自动解析」共用此路径（2026-08-16 提取）。
    /// 多命中（基名/别名/问题直呼）时 Matched 返回全部命中概念，Related 为各概念
    /// 直接引用的并集——多概念共享的事实（如播种与谷物）一次性给全。
    /// </summary>
    private PlanItemResult BuildOkResult(string game, string relation, string entity, List<JsonElement> matched, string source = "")
    {
        var resolvedId = GetElementId(matched[0]);

        var item = new PlanItemResult
        {
            Relation = relation,
            Entity = entity,
            Status = "ok",
            Source = source,
            Matched = matched,
            Related = ExpandRelated(game, matched, light: source == "question_hit")
        };

        var localId = resolvedId.Contains("::") ? resolvedId[(resolvedId.IndexOf("::", StringComparison.Ordinal) + 2)..] : resolvedId;
        GetFlowPositions(game).TryGetValue(localId, out var pos);

        // 流程位置链（flow 节点才有）：回答「在哪个阶段/回合」类语境
        if (pos != null)
            item.FlowContext = pos.Ancestors;

        // condition 关系：额外提取「能不能」答案所需的三要素——条件谓词、费用、目标约束
        if (relation == "condition" && matched.Count > 0)
        {
            var el = matched[0];
            item.Condition = ExtractTopField(el, "<ontology::condition>");
            item.Cost = ExtractTopField(el, "<ontology::cost>");
            item.Target = ExtractTopField(el, "target");
        }

        // ordering 关系：同级选项顺序 + 位置 + 循环结构（回答「之后是什么/何时结束」）
        if (relation == "ordering" && pos != null)
        {
            item.Siblings = pos.Siblings;
            item.PositionIndex = pos.Index;
            item.Loop = pos.Loop;
        }

        // boundary 关系：溢出/下溢/圈事件等边界字段 + 缺省语义说明
        if (relation == "boundary" && matched.Count > 0)
        {
            var el = matched[0];
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
            item.Message = "未声明溢出/下溢补偿的轨道：缺省语义=该方向无事发生或该方向不会发生（见 ontology <ontology::overflow_compensation> 定义）。";
        }

        return item;
    }

    /// <summary>提取概念顶层的指定字段（无则 null）。</summary>
    private static JsonElement? ExtractTopField(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v)
            ? v
            : null;
    }

    /// <summary>
    /// ok 结果的引用扩展：所有命中概念直接引用的概念的并集（去重、按首现顺序、
    /// 上限 MaxRelatedConcepts）。light=true（问题级直呼）时跳过本体引用
    /// （&lt;ontology::x&gt;）——程序已拍板目标概念，只给游戏概念引用，减少 LLM 噪声。
    /// </summary>
    private List<JsonElement> ExpandRelated(string game, List<JsonElement> matched, bool light)
    {
        var related = new List<JsonElement>();
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in matched)
        {
            var mid = GetElementId(el);
            if (!string.IsNullOrEmpty(mid)) excluded.Add(mid);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var appendedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in matched)
        {
            var text = JsonSerializer.Serialize(el, RelaxedJsonOptions);
            foreach (Match m in ConceptRefRegex.Matches(text))
            {
                var raw = m.Groups[1].Value;
                if (!seen.Add(raw)) continue;
                if (light && raw.Contains("::")) continue; // 本体概念在问题级直呼时不展开

                var local = raw.Contains("::") ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..] : raw;
                if (excluded.Contains(local)) continue;

                foreach (var found in GetConcepts(game, raw))
                {
                    var foundId = GetElementId(found);
                    if (!string.IsNullOrEmpty(foundId) && !appendedIds.Add(foundId))
                        continue;
                    if (related.Count >= MaxRelatedConcepts) return related;
                    related.Add(found);
                }
            }
        }
        return related;
    }

    private List<JsonElement> ResolvePlanEntity(string game, string entity, out List<ConceptSummary> candidates, out string source)
    {
        candidates = new List<ConceptSummary>();
        source = "";
        entity = entity.Trim();

        var byId = GetConcepts(game, entity).ToList();
        if (byId.Count > 0)
        {
            source = "exact_id";
            return byId;
        }

        // 精确匹配：中文名 / 英文名 / 别名 / 基名（大小写不敏感；覆盖 flow 节点与实例）。
        // 「名词直呼」工具：客人按名字提到概念时，程序直接确定检索目标，不走向量。
        // 一个键可映射多个概念（基名「家庭成长」→ 需/无需房间两个行动），多命中全部返回。
        var lookup = GetExactLookup(game);
        if (lookup.TryGetValue(entity, out var hits))
        {
            var exact = new List<JsonElement>();
            foreach (var h in hits) exact.AddRange(GetConcepts(game, h.Id));
            if (exact.Count > 0)
            {
                source = hits[0].Kind;
                return exact;
            }
        }

        // 名称包含 → 唯一则直接解析，多个则返回候选供消歧
        var map = GetNameMap(game);
        var containing = map.Where(kv => kv.Value.Contains(entity, StringComparison.Ordinal)).Take(6).ToList();
        if (containing.Count == 1)
        {
            source = "contain_unique";
            return GetConcepts(game, containing[0].Key).ToList();
        }

        candidates = containing
            .Select(kv => new ConceptSummary { Id = kv.Key, Name = kv.Value })
            .ToList();
        return new List<JsonElement>();
    }

    /// <summary>
    /// 「问题级直呼」（广播第二站）：实体解析失败时，直接扫客人问题原文——命中的
    /// 概念名/别名/基名即拍板，不再依赖 LLM 的转述质量（2026-08-16 QA：C 类 10 题
    /// 的查询几乎全是转述失败，如「谷物播种」「开局食物」「农场空格」）。
    /// 最长名字优先 + 区间不重叠；命中概念去重后最多返回 MaxQuestionHits 个。
    /// </summary>
    private const int MaxQuestionHits = 4;

    private List<JsonElement> ResolveFromQuestion(string game, string question, out string source)
    {
        source = "";
        if (string.IsNullOrWhiteSpace(question)) return new List<JsonElement>();

        var lookup = GetExactLookup(game);

        // 收集所有命中区间（key, 起点, 长度）
        var spans = new List<(string Key, int Start, int Len)>();
        foreach (var (key, _) in lookup)
        {
            var idx = question.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                spans.Add((key, idx, key.Length));
                idx = question.IndexOf(key, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (spans.Count == 0) return new List<JsonElement>();

        spans.Sort((a, b) => a.Len != b.Len ? b.Len.CompareTo(a.Len) : a.Start.CompareTo(b.Start));

        var takenUntil = -1;
        var result = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, start, len) in spans)
        {
            if (start < takenUntil) continue; // 被更长的名字覆盖（如「家庭成员」盖住「成员」）
            takenUntil = start + len;
            foreach (var h in lookup[key])
            {
                if (!seen.Add(h.Id)) continue;
                result.AddRange(GetConcepts(game, h.Id));
                if (result.Count >= MaxQuestionHits)
                {
                    source = "question_hit";
                    return result;
                }
            }
        }

        if (result.Count > 0) source = "question_hit";
        return result;
    }

    /// <summary>flow 节点 id → 流程位置（祖先链 + 同级选项顺序 + 位置 + 最近 loop）。</summary>
    private Dictionary<string, FlowPosition>? _flowPositions;

    public class FlowPosition
    {
        /// <summary>同级 options 的 id/引用列表（按序）——回答「之后是什么」。</summary>
        public List<string> Siblings { get; set; } = new();
        /// <summary>在同级 options 中的下标。</summary>
        public int Index { get; set; } = -1;
        /// <summary>祖先链 zh 名（不含自己）。</summary>
        public List<string> Ancestors { get; set; } = new();
        /// <summary>最近的 loop 结构（round/phase 的 count/until）——回答「何时结束」。</summary>
        public JsonElement? Loop { get; set; }
    }

    private Dictionary<string, FlowPosition> GetFlowPositions(string game)
    {
        if (_flowPositions != null) return _flowPositions;
        var result = new Dictionary<string, FlowPosition>();
        var flow = LoadGameFlow(game);
        if (flow != null)
            WalkFlowPositions(flow.RootElement, new List<string>(), null, -1, null, result);
        _flowPositions = result;
        return result;
    }

    private static void WalkFlowPositions(
        JsonElement node, List<string> ancestors,
        List<string>? siblingIds, int siblingIndex,
        JsonElement? currentLoop,
        Dictionary<string, FlowPosition> result)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        var hasId = node.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(idProp.GetString());
        string? id = hasId ? idProp.GetString() : null;
        JsonElement zhp = default;
        var hasName = node.TryGetProperty("name", out var nm)
            && nm.ValueKind == JsonValueKind.Object
            && nm.TryGetProperty("zh", out zhp)
            && zhp.ValueKind == JsonValueKind.String;

        // loop 沿树向下传递：最近的 procedure 祖先的 loop 是子树的循环边界
        var loopHere = node.TryGetProperty("loop", out var lp) ? lp : currentLoop;

        if (!string.IsNullOrEmpty(id))
        {
            result[id] = new FlowPosition
            {
                Siblings = siblingIds != null ? new List<string>(siblingIds) : new List<string>(),
                Index = siblingIndex,
                Ancestors = new List<string>(ancestors),
                Loop = loopHere
            };
            if (hasName && !string.IsNullOrEmpty(zhp.GetString()))
                ancestors.Add(zhp.GetString()!);
        }

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name == "id" || prop.Name == "name" || prop.Name == "description" || prop.Name == "loop") continue;

            if (prop.Name == "options" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                // 收集同级 id 列表，然后逐个下钻（带新上下文）
                var sibIds = new List<string>();
                foreach (var opt in prop.Value.EnumerateArray())
                {
                    if (opt.ValueKind == JsonValueKind.String)
                        sibIds.Add(opt.GetString()!);
                    else if (opt.ValueKind == JsonValueKind.Object
                        && opt.TryGetProperty("id", out var oid) && oid.ValueKind == JsonValueKind.String)
                        sibIds.Add(oid.GetString()!);
                }
                for (var i = 0; i < prop.Value.GetArrayLength(); i++)
                    WalkFlowPositions(prop.Value[i], ancestors, sibIds, i, loopHere, result);
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
                WalkFlowPositions(prop.Value, ancestors, siblingIds, siblingIndex, loopHere, result);
            else if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in prop.Value.EnumerateArray())
                    WalkFlowPositions(item, ancestors, siblingIds, siblingIndex, loopHere, result);
        }

        if (!string.IsNullOrEmpty(id) && hasName && !string.IsNullOrEmpty(zhp.GetString()))
            ancestors.RemoveAt(ancestors.Count - 1);
    }

    private Dictionary<string, string> GetNameMap(string game)
    {
        if (_nameMaps.TryGetValue(game, out var cached)) return cached;
        var map = new Dictionary<string, string>();

        foreach (var type in GetConceptTypes(game))
        {
            foreach (var summary in ListConcepts(game, type))
            {
                if (!string.IsNullOrEmpty(summary.Id) && !string.IsNullOrEmpty(summary.Name) && summary.Id != summary.Name)
                    map[summary.Id] = summary.Name;
            }
        }

        // flow.json 递归节点（procedures/triggers 内嵌的 id + name.zh）
        var flow = LoadGameFlow(game);
        if (flow != null) WalkFlowForNames(flow.RootElement, map);
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null) WalkFlowForNames(ontologyFlow.RootElement, map);

        _nameMaps[game] = map;
        return map;
    }

    private static void WalkFlowForNames(JsonElement node, Dictionary<string, string> map)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(id)
                    && node.TryGetProperty("name", out var name)
                    && name.TryGetProperty("zh", out var zh))
                {
                    var zhName = zh.GetString() ?? "";
                    if (!string.IsNullOrEmpty(zhName) && !map.ContainsKey(id))
                        map[id] = zhName;
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkFlowForNames(prop.Value, map);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowForNames(item, map);
        }
    }

    private readonly Dictionary<string, Dictionary<string, List<(string Id, string Kind)>>> _exactLookups = new();

    /// <summary>
    /// 「名词直呼」精确查找表：zh 名 / en 名（大小写不敏感）/ aliases / 基名（括号注解剥除）
    /// → 概念 id 列表 + 匹配类别。一个键可以映射多个概念（如基名「家庭成长」→ 需空房间与
    /// 无需房间两个行动；别名「随时转换效果」→ 烹饪与生吃），多命中全部返回由 LLM 读数据取舍。
    /// 与 GetNameMap 同源（概念 + 实例 + flow + 本体）。首次访问时构建并缓存；
    /// JSON 修改后需重启 API 才生效（与 LoadGameConcepts 一致）。
    /// </summary>
    private Dictionary<string, List<(string Id, string Kind)>> GetExactLookup(string game)
    {
        if (_exactLookups.TryGetValue(game, out var cached)) return cached;

        var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string id, string kind)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<(string, string)>();
                map[key] = list;
            }
            if (!list.Any(e => e.Item1 == id)) list.Add((id, kind));
        }

        // 1) 既有来源的 zh 名（与 GetNameMap 同源，保持原行为）
        foreach (var type in GetConceptTypes(game))
            foreach (var summary in ListConcepts(game, type))
                Add(summary.Name, summary.Id, "exact_name_zh");

        // 2) 游戏概念 + 实例的 zh/en 名与 aliases（只有原始 JSON 才有这些字段）
        AddRawNames(LoadGameConcepts(game), ConceptArrayTypes, Add);
        AddRawNames(LoadGameInstances(game), InstanceArrayTypes, Add);

        // 3) 本体概念 en 名（zh 名太通用——行动/转移/对象——不进直呼表，避免噪声）
        AddRawNames(LoadOntology(), new[] { "concepts" }, Add);

        // 4) flow 节点 zh/en 名
        var flow = LoadGameFlow(game);
        if (flow != null) WalkFlowNames(flow.RootElement, Add);
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null) WalkFlowNames(ontologyFlow.RootElement, Add);

        // 5) 基名：把 zh 名里的括号注解剥掉（「家庭成长（需空房间）」→「家庭成长」），
        //    让客人/LLM 只说名字主体也能直呼命中——2026-08-16 QA 显示 C 类题几乎全是
        //    转述与全名不一致导致的实体解析失败
        var bases = new List<(string Key, string Id)>();
        foreach (var (key, list) in map)
            foreach (var (id, kind) in list)
                if (kind == "exact_name_zh")
                {
                    var b = StripAnnotations(key);
                    if (b != null && !string.Equals(b, key, StringComparison.Ordinal))
                        bases.Add((b, id));
                }
        foreach (var (key, id) in bases)
            Add(key, id, "exact_base");

        _exactLookups[game] = map;
        return map;
    }

    /// <summary>剥除中文名里的括号注解（全角/半角均可），剥后不足 2 字返回 null。</summary>
    private static string? StripAnnotations(string name)
    {
        var s = ParentheticalRegex.Replace(name, "");
        s = s.Trim();
        return s.Length >= 2 ? s : null;
    }

    private static readonly Regex ParentheticalRegex = new(@"[（(][^（）()]*[）)]");

    private static void AddRawNames(JsonDocument? doc, string[] arrays,
        Action<string, string, string> add)
    {
        if (doc == null) return;
        foreach (var type in arrays)
        {
            if (!doc.RootElement.TryGetProperty(type, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
            {
                var id = GetElementId(el);
                if (string.IsNullOrEmpty(id)) continue;
                if (el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
                {
                    if (name.TryGetProperty("zh", out var zh))
                    {
                        var zhName = zh.GetString() ?? "";
                        if (!string.IsNullOrEmpty(zhName)) add(zhName, id, "exact_name_zh");
                    }
                    if (name.TryGetProperty("en", out var en))
                    {
                        var enName = en.GetString() ?? "";
                        if (!string.IsNullOrEmpty(enName)) add(enName, id, "exact_name_en");
                    }
                }
                if (el.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
                {
                    foreach (var lang in new[] { "zh", "en" })
                    {
                        if (al.TryGetProperty(lang, out var list) && list.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in list.EnumerateArray())
                            {
                                var s = a.GetString() ?? "";
                                if (!string.IsNullOrEmpty(s)) add(s, id, "exact_alias");
                            }
                        }
                    }
                }
            }
        }
    }

    private static void WalkFlowNames(JsonElement node, Action<string, string, string> add)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(id)
                    && node.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.Object)
                {
                    if (name.TryGetProperty("zh", out var zh))
                    {
                        var zhName = zh.GetString() ?? "";
                        if (!string.IsNullOrEmpty(zhName)) add(zhName, id, "exact_name_zh");
                    }
                    if (name.TryGetProperty("en", out var en))
                    {
                        var enName = en.GetString() ?? "";
                        if (!string.IsNullOrEmpty(enName)) add(enName, id, "exact_name_en");
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkFlowNames(prop.Value, add);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowNames(item, add);
        }
    }

    public async Task<SearchConceptsResult> SearchConceptsAsync(string game, string query, string searchMode = "full")
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchConceptsResult { Results = new List<ConceptSummary>(), Query = query };

        var useNameOnly = searchMode == "name";

        // 把 query 拆成子查询，加上原句一起并行搜
        var subQueries = SplitQuery(query);
        var allQueries = new HashSet<string>(subQueries) { query };

        // 并行：每个子句同时跑向量搜索 + 关键词搜索（批次带子句标记，供逐词分数记账）
        async Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)> VectorChannelAsync(string q, string mode)
        {
            // topK 与合并结果的 top-15 对齐——5 会把原始相似度第 6 名开外的概念
            // 的向量分截成 0（激活骰被 favor_test 等挤出 top-5 的教训，2026-08-13）
            var items = await VectorSearchAsync(game, q, topK: 15, searchMode: mode);
            // 同一 concept_id 可能来自多个来源（ontology 通用概念 + 游戏层具体实现，
            // 如 public_board），批次内去重取最高分——否则合并求和会重复计分
            var deduped = items
                .GroupBy(x => x.Summary.Id)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .ToList();
            return (q, true, deduped);
        }

        async Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)> KeywordChannelAsync(string q)
        {
            var items = await KeywordSearchWithScoreAsync(game, q);
            return (q, false, items);
        }

        var tasks = new List<Task<(string SubQuery, bool IsVector, List<(ConceptSummary Summary, float Score)> Items)>>();
        foreach (var q in allQueries)
        {
            // 路由（2026-08-13 实验）：短词（≤2 字）是词级对局——查名字集合
            // （短文本对短文本，实测排序有意义）；长词/整句是句级对局——查全文集合。
            // BGE 句模型配「短词 vs 全文」落在窄锥噪声带（无关文本余弦基线 0.74-0.82，
            // 实测「白色」top-16 无一个相关概念），排序近似随机
            if (_vectorSearch != null)
            {
                var effectiveMode = searchMode == "name" || q.Length <= 2 ? "name" : "full";
                tasks.Add(VectorChannelAsync(q, effectiveMode));
            }
            tasks.Add(KeywordChannelAsync(q));
        }

        var allBatches = await Task.WhenAll(tasks);

        // 合并去重：同一概念累加各通道分数（AND 语义——匹配子词越多得分越高）；
        // 同时按子词分别记账 vector/keyword 双通道分数，供 LLM 判断每个词匹配强弱
        var merged = new Dictionary<string, AccumulatedSearch>();
        foreach (var (subQuery, isVector, items) in allBatches)
        {
            foreach (var (summary, score) in items)
            {
                if (!merged.TryGetValue(summary.Id, out var acc))
                {
                    acc = new AccumulatedSearch { Summary = summary };
                    merged[summary.Id] = acc;
                }
                acc.Total += score;
                var byTerm = isVector ? acc.VectorByTerm : acc.KeywordByTerm;
                byTerm[subQuery] = byTerm.GetValueOrDefault(subQuery) + score;
            }
        }

        // 按子词数量归一化：匹配词越多的概念得分越高（排名算法不变）
        var divisor = Math.Max(subQueries.Length, 1);
        var hasFullQuery = !subQueries.Contains(query);
        var results = merged.Values
            .Select(acc => new { acc, RawScore = acc.Total / divisor })
            .OrderByDescending(x => x.RawScore)
            .Take(15)
            .Select(x =>
            {
                var summary = x.acc.Summary;
                summary.Score = MathF.Round(x.RawScore, 2);
                summary.TermScores = subQueries.ToDictionary(
                    t => t,
                    t => new ChannelScores
                    {
                        Vector = MathF.Round(x.acc.VectorByTerm.GetValueOrDefault(t), 2),
                        Keyword = MathF.Round(x.acc.KeywordByTerm.GetValueOrDefault(t), 2)
                    });
                if (hasFullQuery)
                {
                    summary.FullQueryScore = new ChannelScores
                    {
                        Vector = MathF.Round(x.acc.VectorByTerm.GetValueOrDefault(query), 2),
                        Keyword = MathF.Round(x.acc.KeywordByTerm.GetValueOrDefault(query), 2)
                    };
                }
                return summary;
            })
            .ToList();

        // 向量搜索结果没有 description，从概念数据中补上
        PopulateDescriptions(game, results);

        return new SearchConceptsResult
        {
            Results = results,
            Query = query,
            SplitTerms = subQueries.ToList()
        };
    }

    /// <summary>搜索合并过程中的单概念累计分数（按子词分通道记账）。</summary>
    private sealed class AccumulatedSearch
    {
        public ConceptSummary Summary = null!;
        public float Total;
        public readonly Dictionary<string, float> VectorByTerm = new();
        public readonly Dictionary<string, float> KeywordByTerm = new();
    }

    private void PopulateDescriptions(string game, List<ConceptSummary> results)
    {
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r.Description)) continue;
            var detail = GetConcept(game, r.Id);
            if (detail.HasValue)
            {
                r.Description = ExtractDescriptionZh(detail.Value);
            }
        }
    }

    public ListConceptsResult ListAllConceptIds(string game)
    {
        var byType = new Dictionary<string, List<ConceptSummary>>();
        foreach (var type in GetConceptTypes(game))
        {
            var concepts = ListConcepts(game, type);
            if (concepts.Count > 0)
                byType[type] = concepts.Select(c => new ConceptSummary
                {
                    Id = c.Id,
                    Name = c.Name,
                    Type = c.Type
                }).ToList();
        }

        return new ListConceptsResult
        {
            ByType = byType,
            TotalCount = byType.Values.Sum(v => v.Count)
        };
    }

    private async Task<List<(ConceptSummary Summary, float Score)>> VectorSearchAsync(
        string game, string query, int topK, string searchMode = "full")
    {
        try
        {
            var results = await _vectorSearch!.SearchAsync(game, query, topK: topK, searchMode: searchMode);
            return results.Select(r => (
                new ConceptSummary { Id = r.ConceptId, Name = r.NameZh, Type = r.Type },
                r.Score
            )).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Vector search failed for '{query}': {ex.Message}");
            return new();
        }
    }

    private Task<List<(ConceptSummary Summary, float Score)>> KeywordSearchWithScoreAsync(
        string game, string query)
    {
        return Task.Run(() =>
        {
            var results = KeywordSearch(game, query);
            return results.Select(r =>
            {
                // 关键词匹配只作小幅加成，不主导排序（2026-08-16 实测：内容命中 0.90 的
                // 固定分把「描述里提到该词」的无关概念顶上榜首，盖过向量语义分）。
                // 向量语义分是排序主体；关键词加成只用于打破同分与弱向量时的微调。
                var terms = Tokenize(query).ToList();
                float score;
                if (terms.Any(t => r.Id.Equals(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.5f;
                else if (terms.Any(t => r.Name.Contains(t, StringComparison.InvariantCultureIgnoreCase)))
                    score = 0.3f;
                else
                    score = 0.05f;
                return (r, score);
            }).ToList();
        });
    }

    /// <summary>
    /// 按标点和空格拆 query，不做 lowercase，保留 LLM 原始意图。
    /// </summary>
    private static string[] SplitQuery(string query)
    {
        return query.Split(
            new[] { ' ', '\t', '\n', '\r', '，', '。', '、', '？', '！', '；', '：', '"', '"' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// 原有关键词搜索逻辑（向量搜索不可用或没结果时降级）。
    /// </summary>
    public IReadOnlyList<ConceptSummary> KeywordSearch(string game, string query)
    {
        var terms = Tokenize(query).ToList();
        if (terms.Count == 0) return Array.Empty<ConceptSummary>();

        var (ns, localQuery) = ParseNamespace(query);
        var localTerms = Tokenize(localQuery).ToList();

        var all = new List<ConceptSummary>();
        if (string.IsNullOrEmpty(ns) || ns == "ontology")
            all.AddRange(ListConcepts(game, "ontology"));

        if (string.IsNullOrEmpty(ns))
        {
            all.AddRange(ListConcepts(game, "objects"));
            all.AddRange(ListConcepts(game, "actions"));
            all.AddRange(ListConcepts(game, "triggers"));
            all.AddRange(ListConcepts(game, "conditions"));
            all.AddRange(ListConcepts(game, "top_level_refs"));
            all.AddRange(ListConcepts(game, "effects"));
            all.AddRange(ListConcepts(game, "modules"));
            all.AddRange(ListConcepts(game, "cards"));
            all.AddRange(ListConcepts(game, "continent_tiles"));
            all.AddRange(ListConcepts(game, "sites"));
            all.AddRange(ListConcepts(game, "chips"));
            all.AddRange(ListConcepts(game, "slots"));
            all.AddRange(ListConcepts(game, "flow"));
        }

        var activeTerms = localTerms.Count > 0 ? localTerms : terms;
        var results = new List<ConceptSummary>();
        foreach (var summary in all)
        {
            if (activeTerms.Any(t => MatchesSummary(summary, t)))
            {
                results.Add(summary);
                continue;
            }

            var detail = GetConcept(game, summary.Id);
            if (detail.HasValue && activeTerms.Any(t => ContainsTerm(detail.Value, t)))
            {
                results.Add(summary);
            }
        }

        return results;
    }

    /// <summary>
    /// 提取游戏所有概念的索引条目，用于向量化。
    /// </summary>
    public IReadOnlyList<ConceptIndexItem> GetIndexItems(string game)
    {
        var result = new List<ConceptIndexItem>();

        // ontology 概念（不限定 game）
        foreach (var c in ListConcepts(game, "ontology"))
        {
            var detail = GetConcept(game, c.Id);
            result.Add(new ConceptIndexItem
            {
                ConceptId = c.Id,
                Type = "ontology",
                NameZh = c.Name,
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result);
        }

        foreach (var type in ConceptArrayTypes)
        {
            foreach (var c in ListConcepts(game, type))
            {
                var detail = GetConcept(game, c.Id);
                result.Add(new ConceptIndexItem
                {
                    ConceptId = c.Id,
                    Type = type,
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result);
            }
        }

        // instances.json 实例
        foreach (var type in InstanceArrayTypes)
        {
            foreach (var c in ListConcepts(game, type))
            {
                var detail = GetConcept(game, c.Id);
                result.Add(new ConceptIndexItem
                {
                    ConceptId = c.Id,
                    Type = type,
                    NameZh = c.Name,
                    NameEn = ExtractEnName(detail),
                    SearchText = BuildSearchText(c, detail),
                });
                if (detail.HasValue) ExtractSlots(detail.Value, result);
            }
        }

        // 顶层引用
        foreach (var c in ListConcepts(game, "top_level_refs"))
        {
            var detail = GetConcept(game, c.Id);
            result.Add(new ConceptIndexItem
            {
                ConceptId = c.Id,
                Type = "top_level_ref",
                NameZh = c.Name,
                NameEn = ExtractEnName(detail),
                SearchText = BuildSearchText(c, detail),
            });
            if (detail.HasValue) ExtractSlots(detail.Value, result);
        }

        // ontology/flow.json
        var ontologyFlow = LoadOntologyFlow();
        if (ontologyFlow != null)
        {
            ExtractFlowItems(ontologyFlow.RootElement, result);
        }

        // flow.json 流程
        var flow = LoadGameFlow(game);
        if (flow != null)
        {
            ExtractFlowItems(flow.RootElement, result);
        }

        return result;
    }

    /// <summary>递归提取 slots 元素 (裸键槽名如 population/expansion 作为概念, 与 Python rebuild_index 一致)</summary>
    private static void ExtractSlots(JsonElement node, List<ConceptIndexItem> result)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var slot in slots.EnumerateArray())
                {
                    if (slot.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in slot.EnumerateObject())
                    {
                        // 裸键 = 槽位名 (可索引); <概念> 键 = 已有定义的概念引用, 跳过
                        if (prop.Name.StartsWith("<") || prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var parts = new List<string>();
                        CollectIndexText(prop.Value, parts);
                        result.Add(new ConceptIndexItem
                        {
                            ConceptId = prop.Name,
                            Type = "slot",
                            NameZh = "",
                            NameEn = "",
                            SearchText = string.Join(" ", parts),
                        });
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
                ExtractSlots(prop.Value, result);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                ExtractSlots(item, result);
        }
    }

    /// <summary>递归收集 id/name/description 文本 (slots 深层效果描述)</summary>
    private static void CollectIndexText(JsonElement node, List<string> parts)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in node.EnumerateObject())
            {
                if (prop.Name == "id")
                    parts.Add(prop.Value.GetString() ?? "");
                else if (prop.Name == "name" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("zh", out var z)) parts.Add(z.GetString() ?? "");
                    if (prop.Value.TryGetProperty("en", out var e)) parts.Add(e.GetString() ?? "");
                }
                else if (prop.Name == "description" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("zh", out var z)) parts.Add(z.GetString() ?? "");
                    if (prop.Value.TryGetProperty("en", out var e)) parts.Add(e.GetString() ?? "");
                }
                else
                {
                    CollectIndexText(prop.Value, parts);
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                CollectIndexText(item, parts);
        }
    }

    private static void ExtractFlowItems(JsonElement root, List<ConceptIndexItem> result)
    {
        // 游戏 flow.json：procedures 树 + triggers 组
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!root.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                WalkFlowNode(item, result);
            }
        }

        // ontology flow.json：pipeline.options 数组
        if (root.TryGetProperty("pipeline", out var pipeline) &&
            pipeline.TryGetProperty("options", out var options))
        {
            foreach (var opt in options.EnumerateArray())
            {
                WalkFlowNode(opt, result);
            }
        }
    }

    /// <summary>
    /// 独立概念节点判据：带层级关系字段（specifies/extends/instance_of）的节点才是
    /// 可被搜索的概念；仅 id+name 的节点是 pipeline 局部步骤（do_after 引用名），
    /// 不入搜索索引与目录（get_concept 按 id 仍可查到）。
    /// </summary>
    private static bool IsStandaloneFlowNode(JsonElement node) =>
        node.TryGetProperty("specifies", out _)
        || node.TryGetProperty("extends", out _)
        || node.TryGetProperty("instance_of", out _);

    private static void WalkFlowNode(JsonElement node, List<ConceptIndexItem> result)
    {
        var id = node.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        // 只索引独立概念节点（有 id 且有 specifies/extends/instance_of）：
        // 局部步骤（仅 id+name，do_after 引用用）不入索引——短名短描述是向量噪音，
        // 且同 id 跨位置重复互相覆盖；步骤信息随父概念的 get_concept 完整返回。
        // game 等通用容器概念（各游戏共有的顶层流程宿主）也不入索引。
        if (!string.IsNullOrEmpty(id) && id != "game" && IsStandaloneFlowNode(node))
        {
            var zhParts = new List<string> { id };

            var nodeType = node.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(nodeType))
                zhParts.Add(nodeType);

            if (node.TryGetProperty("name", out var name) && name.TryGetProperty("zh", out var nzh))
                zhParts.Add(nzh.GetString()!);
            if (node.TryGetProperty("description", out var desc) && desc.TryGetProperty("zh", out var dzh))
                zhParts.Add(dzh.GetString()!);

            result.Add(new ConceptIndexItem
            {
                ConceptId = id,
                Type = "flow",
                NameZh = node.TryGetProperty("name", out var nm) && nm.TryGetProperty("zh", out var nz)
                    ? nz.GetString() : id,
                NameEn = null,
                // <> 引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
                SearchText = ConceptRefRegex.Replace(
                    string.Join(" ", zhParts.Where(p => !string.IsNullOrEmpty(p))), ""),
            });
        }

        // 递归无条件下钻（匿名 pipeline 容器也要深入）
        // (2026-08-13: 无 id 直接 return 曾导致嵌套在 pipeline 中的流程节点
        //  从索引缺失——与 rebuild_index 同源修复)
        if (node.TryGetProperty("events", out var events))
        {
            foreach (var evt in events.EnumerateArray())
            {
                WalkFlowNode(evt, result);
            }
        }

        if (node.TryGetProperty("options", out var opts))
        {
            foreach (var opt in opts.EnumerateArray())
            {
                WalkFlowNode(opt, result);
            }
        }

        foreach (var containerKey in new[] {
            "<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
            "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
            "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
            "<ontology::instant_content>", "<ontology::instant_cost>",
            "<ontology::continuous_effect>", "<ontology::effect>" })
        {
            if (node.TryGetProperty(containerKey, out var container) && container.ValueKind == JsonValueKind.Object)
                WalkFlowNode(container, result);
        }
    }

    /// <summary>
    /// 为指定游戏重建向量索引。
    /// </summary>
    public async Task BuildEmbeddingIndexAsync(string game)
    {
        if (_vectorSearch == null) return;
        var items = GetIndexItems(game);
        await _vectorSearch.RebuildIndexAsync(game, items);
    }

    // ---- 私有辅助 ----

    private static string BuildSearchText(ConceptSummary summary, JsonElement? detail)
    {
        var parts = new List<string> { summary.Id, summary.Name };
        if (detail.HasValue)
        {
            var detailEl = detail.Value;
            if (detailEl.TryGetProperty("name", out var name))
            {
                if (name.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString()!);
                if (name.TryGetProperty("en", out var en)) parts.Add(en.GetString()!);
            }
            if (detailEl.TryGetProperty("description", out var def))
            {
                if (def.TryGetProperty("zh", out var zh)) parts.Add(zh.GetString()!);
                if (def.TryGetProperty("en", out var en)) parts.Add(en.GetString()!);
            }
        }
        // <> 包裹的概念引用不参与相似度计算（与 rebuild_index.py 的 strip_refs 一致）
        return ConceptRefRegex.Replace(string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p))), "");
    }

    private static string? ExtractEnName(JsonElement? element)
    {
        if (element.HasValue
            && element.Value.TryGetProperty("name", out var name)
            && name.TryGetProperty("en", out var en))
        {
            return en.GetString();
        }
        return null;
    }

    private JsonDocument LoadOntology()
    {
        var path = Path.Combine(_basePath, "ontology", "concepts.json");
        return LoadJson(path);
    }

    private JsonDocument? LoadGameConcepts(string game)
    {
        var path = Path.Combine(_basePath, "games", game, "concepts.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private JsonDocument? LoadOntologyFlow()
    {
        var path = Path.Combine(_basePath, "ontology", "flow.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private JsonDocument? LoadGameFlow(string game)
    {
        var path = Path.Combine(_basePath, "games", game, "flow.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private JsonDocument? LoadGameInstances(string game)
    {
        var path = Path.Combine(_basePath, "games", game, "instances.json");
        if (!File.Exists(path)) return null;
        return LoadJson(path);
    }

    private static (string? ns, string localId) ParseNamespace(string id)
    {
        // Strip surrounding angle brackets if any, e.g. "<ontology::resource>" -> "ontology::resource"
        var trimmed = id.Trim('<', '>');
        var parts = trimmed.Split(new[] { "::" }, StringSplitOptions.None);
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
        {
            return (parts[0], parts[1]);
        }
        return (null, trimmed);
    }

    private static IEnumerable<string> GetTopLevelRefKeys(JsonDocument concepts)
    {
        var results = new List<string>();
        foreach (var property in concepts.RootElement.EnumerateObject())
        {
            if (ConceptArrayTypes.Contains(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            results.Add(property.Name);
        }
        return results;
    }

    private JsonDocument LoadJson(string path)
    {
        if (_loadedFiles.TryGetValue(path, out var doc)) return doc;
        var json = File.ReadAllText(path);
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        _loadedFiles[path] = document;
        return document;
    }

    private static IReadOnlyList<ConceptSummary> ExtractArrayConcepts(JsonDocument doc, string propertyName)
    {
        var results = new List<ConceptSummary>();
        if (!doc.RootElement.TryGetProperty(propertyName, out var array)) return results;
        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? string.Empty;
            var summary = new ConceptSummary
            {
                Id = id,
                Name = ExtractName(item),
                Type = propertyName,
                Description = ExtractDescriptionZh(item),
                Media = ExtractMedia(item)
            };
            results.Add(summary);
        }
        return results;
    }

    private static Dictionary<string, JsonElement>? ExtractMedia(JsonElement item)
    {
        if (!item.TryGetProperty("media", out var mediaElement) || mediaElement.ValueKind != JsonValueKind.Object)
            return null;

        var media = new Dictionary<string, JsonElement>();
        foreach (var property in mediaElement.EnumerateObject())
        {
            // 支持 string 或 string[]，其他类型忽略
            if (property.Value.ValueKind == JsonValueKind.String ||
                property.Value.ValueKind == JsonValueKind.Array)
            {
                media[property.Name] = property.Value.Clone();
            }
        }
        return media.Count > 0 ? media : null;
    }

    private static IReadOnlyList<ConceptSummary> ExtractFlowConcepts(JsonDocument flow)
    {
        var results = new List<ConceptSummary>();
        var seen = new HashSet<string>();
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!flow.RootElement.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                WalkFlowForSummaries(item, results, seen);
            }
        }
        return results;
    }

    private static void WalkFlowForSummaries(JsonElement node, List<ConceptSummary> results, HashSet<string> seen)
    {
        // 只汇总独立概念节点（有 id 且有 specifies/extends/instance_of）；
        // 匿名容器（pipeline 等）不汇总但继续下钻
        // (2026-08-13: 无 id 直接 return 曾导致嵌套在 pipeline 中的流程节点
        //  从 list_concept_ids 缺失——与 rebuild_index 同源修复)
        if (node.TryGetProperty("id", out var idProp))
        {
            var id = idProp.GetString() ?? string.Empty;
            // game 等通用容器不进关键词搜索与目录（整体流程走 get_game_flow 工具）
            if (!string.IsNullOrEmpty(id) && id != "game" && IsStandaloneFlowNode(node) && seen.Add(id))
            {
                results.Add(new ConceptSummary
                {
                    Id = id,
                    Name = ExtractName(node),
                    Type = "flow"
                });
            }
        }

        // 递归无条件下钻：events、options、容器字段
        foreach (var arrayKey in new[] { "events", "options" })
        {
            if (!node.TryGetProperty(arrayKey, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    WalkFlowForSummaries(item, results, seen);
            }
        }
        foreach (var containerKey in new[] {
            "<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
            "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
            "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
            "<ontology::instant_content>", "<ontology::instant_cost>",
            "<ontology::continuous_effect>", "<ontology::effect>" })
        {
            if (node.TryGetProperty(containerKey, out var container) && container.ValueKind == JsonValueKind.Object)
                WalkFlowForSummaries(container, results, seen);
        }
    }

    private static bool TryFindInArray(JsonElement root, string arrayProperty, string id, out JsonElement found)
    {
        found = default;
        if (!root.TryGetProperty(arrayProperty, out var array)) return false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idProp) && idProp.GetString() == id)
            {
                found = item;
                return true;
            }
        }
        return false;
    }

    private static bool TryFindFlowProcedure(JsonElement root, string id, out JsonElement found)
    {
        found = default;

        // search both procedures and triggers at top level, then recurse
        foreach (var arrayKey in new[] { "procedures", "triggers" })
        {
            if (!root.TryGetProperty(arrayKey, out var arr)) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (TryFindFlowNodeRecursive(item, id, out found))
                    return true;
            }
        }

        return false;
    }

    private static bool TryFindFlowNodeRecursive(JsonElement node, string id, out JsonElement found)
    {
        found = default;
        if (node.TryGetProperty("id", out var idProp) && idProp.GetString() == id)
        {
            found = node;
            return true;
        }

        // recurse into events, options, and content containers
        foreach (var arrayKey in new[] { "events", "options" })
        {
            if (!node.TryGetProperty(arrayKey, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (TryFindFlowNodeRecursive(item, id, out found))
                    return true;
            }
        }

        // recurse into container fields: pipelines, procedures, content, cost, condition, effect
        // (2026-08-13: 补 <ontology::pipeline> 等 procedure 持有键——此前嵌套在 pipeline
        //  中的流程节点（如 event_era_scoring）get_concept 查不到，导致 LLM 反复搜索)
        foreach (var containerKey in new[] {
            "<ontology::pipeline>", "<ontology::action>", "<ontology::turn>",
            "<ontology::round>", "<ontology::phase>", "<ontology::procedure>",
            "<ontology::content>", "<ontology::cost>", "<ontology::condition>",
            "<ontology::instant_content>", "<ontology::instant_cost>",
            "<ontology::continuous_effect>", "<ontology::effect>" })
        {
            if (node.TryGetProperty(containerKey, out var container) && container.ValueKind == JsonValueKind.Object)
            {
                if (TryFindFlowNodeRecursive(container, id, out found))
                    return true;
            }
        }

        return false;
    }

    private static string ExtractName(JsonElement element)
    {
        if (element.TryGetProperty("name", out var name) &&
            name.TryGetProperty("zh", out var zh))
        {
            return zh.GetString() ?? string.Empty;
        }
        if (element.TryGetProperty("id", out var id))
        {
            return id.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string? ExtractDescriptionZh(JsonElement element)
    {
        if (element.TryGetProperty("description", out var desc) &&
            desc.TryGetProperty("zh", out var zh))
        {
            return zh.GetString();
        }
        return null;
    }

    private static IEnumerable<string> Tokenize(string query)
    {
        return query.Split(
            new[] { ' ', '\t', '\n', '\r', '，', '。', '、', '？', '！', '；', '：', '"', '"', '+', '-', '_', '.', '/', '<', '>' },
            StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct();
    }

    private static bool MatchesSummary(ConceptSummary summary, string term)
    {
        return summary.Id.Contains(term, StringComparison.InvariantCultureIgnoreCase) ||
               summary.Name.Contains(term, StringComparison.InvariantCultureIgnoreCase);
    }

    private static bool ContainsTerm(JsonElement element, string term)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Contains(term, StringComparison.InvariantCultureIgnoreCase) ?? false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsTerm(property.Value, term)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsTerm(item, term)) return true;
                }
                break;
        }
        return false;
    }
}

public class ConceptSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public float Score { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, JsonElement>? Media { get; set; }
    /// <summary>每个切分词的双通道匹配分（仅 search_concepts 返回时填充；单词查询时该词即整句，FullQueryScore 为 null）。</summary>
    public Dictionary<string, ChannelScores>? TermScores { get; set; }
    /// <summary>整句查询的双通道匹配分（多词查询时存在；整句参与总分，补上它才能与 Score 对账）。</summary>
    public ChannelScores? FullQueryScore { get; set; }
}

/// <summary>单个查询（切分词或整句）在向量/关键词两通道上的匹配分。</summary>
public class ChannelScores
{
    public float Vector { get; set; }
    public float Keyword { get; set; }
}

public class SearchConceptsResult
{
    public List<ConceptSummary> Results { get; set; } = new();
    public int Count => Results.Count;
    public string Query { get; set; } = string.Empty;
    public List<string> SplitTerms { get; set; } = new();
    public string Strategy { get; set; } = "hybrid_vector_keyword";
    public string Note { get; set; } = "Top results only — NOT exhaustive. If you need to see ALL concepts (e.g., to browse what exists), use list_concept_ids.";
}

public class GetConceptResult
{
    public List<JsonElement> Matched { get; set; } = new();
    public List<JsonElement> Related { get; set; } = new();
    public string Note { get; set; } =
        "related 是 matched 直接引用的概念，已自动扩展一层；related 内概念的引用已标注中文名，如需更深一层的详情请用其 ID 继续调用 get_concept。";
}

public class PlanExecutionResult
{
    public List<PlanItemResult> Results { get; set; } = new();
    public string Note { get; set; } = "";
}

public class PlanItemResult
{
    public string Relation { get; set; } = "";
    public string Entity { get; set; } = "";
    /// <summary>ok | unresolved（实体未命中，看 Candidates）| unsupported（relation 未支持，走兜底工具）</summary>
    public string Status { get; set; } = "ok";
    /// <summary>拍板来源：exact_id / exact_name_zh / exact_name_en / exact_alias / contain_unique / auto_semantic；空 = 程序未拍板（unresolved/no_match 由 LLM 决定）。</summary>
    public string Source { get; set; } = "";
    public List<JsonElement> Matched { get; set; } = new();
    public List<JsonElement> Related { get; set; } = new();
    /// <summary>flow 节点的祖先链（zh 名）——回答「在哪个阶段/回合发生」的语境。</summary>
    public List<string> FlowContext { get; set; } = new();
    public List<ConceptSummary>? Candidates { get; set; }
    public string Message { get; set; } = "";
    /// <summary>condition 关系专用：条件谓词（&lt;ontology::condition&gt; 字段）。</summary>
    public JsonElement? Condition { get; set; }
    /// <summary>condition 关系专用：费用结构（&lt;ontology::cost&gt; 字段）。</summary>
    public JsonElement? Cost { get; set; }
    /// <summary>condition 关系专用：目标约束（target 字段）。</summary>
    public JsonElement? Target { get; set; }
    /// <summary>ordering 关系专用：同级 options 的 id/引用列表（按序）。</summary>
    public List<string> Siblings { get; set; } = new();
    /// <summary>ordering 关系专用：在同级 options 中的下标。</summary>
    public int PositionIndex { get; set; } = -1;
    /// <summary>ordering 关系专用：最近的 loop 结构（count/until）。</summary>
    public JsonElement? Loop { get; set; }
    /// <summary>boundary 关系专用：溢出/下溢/圈事件/容量/循环等边界字段。</summary>
    public Dictionary<string, JsonElement>? Boundary { get; set; }
    /// <summary>list 关系专用：全量概念目录（id+名称，按类型分组）。</summary>
    public ListConceptsResult? Catalog { get; set; }
}

public class ListConceptsResult
{
    public Dictionary<string, List<ConceptSummary>> ByType { get; set; } = new();
    public int TotalCount { get; set; }
    public string Note { get; set; } = "Exhaustive listing of ALL concept IDs and names grouped by type. Use this to confirm a concept doesn't exist or to browse the full catalog.";
}
