using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService
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
                return BuildOkResult(game, relation, entity, qhits, qsource, question);

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
                return BuildOkResult(game, relation, entity, qhits, qsource, question);
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
                        return BuildOkResult(game, relation, entity, autoMatched, "auto_semantic", question);
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

        return BuildOkResult(game, relation, entity, matched, source, question);
    }

    /// <summary>
    /// 把已解析的概念构造成 ok 结果：引用扩展数据 + 流程位置 + 关系专属字段。
    /// 精确解析、问题级直呼与「高置信语义候选自动解析」共用此路径（2026-08-16 提取）。
    /// 多命中（基名/别名/问题直呼）时 Matched 返回全部命中概念，Related 为各概念
    /// 直接引用的并集——多概念共享的事实（如播种与谷物）一次性给全。
    /// </summary>
    private PlanItemResult BuildOkResult(string game, string relation, string entity, List<JsonElement> matched, string source = "", string question = "")
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

        // 事实卡（广播第三站）：数量/计分题的程序化计算——容量公式求值、计分表区间查表、
        // quantity.numeric 分支选择与求和。程序算得出的数不交给 LLM 从散文里读（1+1 交给计算器）。
        // 问题不含数量词/分数词、或命中概念无结构化数据时返回 null，序列化时省略。
        if (!string.IsNullOrWhiteSpace(question))
            item.Facts = ExtractFacts(game, matched, question);

        return item;
    }

    // ---- 事实卡（广播第三站）：数量/计分题的程序化计算 ----
    // 程序算得出的数不交给 LLM 从散文里读：容量公式求值、计分表区间查表、
    // quantity.numeric 分支选择与求和。数据未结构化时静默返回 null（既有散文路径兜底）。

    /// <summary>分数词：问题在问计分/得分（含「扣1分」「+3分」这类数字嵌入形式）。</summary>
    private static readonly Regex ScoreWordRegex = new(
        @"计分|得分|扣分|加分|几分|多少分|算分|分数|\d+\s*分|VP", RegexOptions.Compiled);

    /// <summary>数量词：问题在问数量/容量/上限。</summary>
    private static readonly Regex QuantityWordRegex = new(
        @"几个|几只|几头|几块|几根|几间|几格|几多|几张|多少|上限|容量|能养|能放|拿几|放几|给几|得几",
        RegexOptions.Compiled);

    private static readonly Regex IntegerRegex = new(@"\d+", RegexOptions.Compiled);

    private const int MaxFacts = 6;

    /// <summary>
    /// 事实卡入口：数量词触发 quantity/容量事实，分数词触发计分表事实。
    /// 返回 null = 无可抽取的结构化事实（序列化时 Facts 字段省略）。
    /// </summary>
    private List<JsonElement>? ExtractFacts(string game, List<JsonElement> matched, string question)
    {
        if (string.IsNullOrWhiteSpace(question) || matched.Count == 0) return null;
        var scoreQ = ScoreWordRegex.IsMatch(question);
        var quantityQ = QuantityWordRegex.IsMatch(question);
        if (!scoreQ && !quantityQ) return null;

        var facts = new List<object>();
        if (scoreQ) ExtractScoreFacts(game, matched, question, facts);
        if (quantityQ) ExtractQuantityFacts(matched, question, facts);
        if (facts.Count == 0) return null;

        return facts.Select(f => JsonSerializer.SerializeToElement(f, RelaxedJsonOptions)).ToList();
    }

    private string? _scoreTableGame;
    private JsonElement? _scoreTable;

    /// <summary>flow 终局计分里的结构化计分表（缓存；JSON 修改后需重启 API）。</summary>
    private JsonElement? GetScoreTable(string game)
    {
        if (_scoreTableGame == game && _scoreTable.HasValue) return _scoreTable;
        var flow = LoadGameFlow(game);
        _scoreTableGame = game;
        _scoreTable = flow == null ? null : FindKeyInTree(flow.RootElement, "score_table");
        return _scoreTable;
    }

    private static JsonElement? FindKeyInTree(JsonElement node, string key)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty(key, out var v)) return v;
            foreach (var prop in node.EnumerateObject())
            {
                var r = FindKeyInTree(prop.Value, key);
                if (r.HasValue) return r;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var r = FindKeyInTree(item, key);
                if (r.HasValue) return r;
            }
        }
        return null;
    }

    /// <summary>
    /// 计分事实：从计分表选问题涉及的类别（查询命中的概念 / 问题提到类别名或别名 / 表级别名），
    /// 问题含唯一整数时程序直接查表给数。一类别都没选中或超上限时整表返回。
    /// </summary>
    private void ExtractScoreFacts(string game, List<JsonElement> matched, string question, List<object> facts)
    {
        var table = GetScoreTable(game);
        if (!table.HasValue || table.Value.ValueKind != JsonValueKind.Object) return;

        // 命中概念的本地 id 集合——查询目标本身就是计分类别（如 field_tile）时直接选中该类别
        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in matched)
        {
            var id = GetElementId(m);
            if (id.Contains("::")) id = id[(id.IndexOf("::", StringComparison.Ordinal) + 2)..];
            matchedIds.Add(id);
        }

        // 全部类别条目按 what 引用索引（<field_tile> → 条目）
        var byRef = new Dictionary<string, (string Group, JsonElement Entry)>(StringComparer.Ordinal);
        foreach (var group in new[] { "by_count", "per_unit", "by_material", "by_card" })
        {
            if (!table.Value.TryGetProperty(group, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in arr.EnumerateArray())
            {
                var r = EntryRef(entry);
                if (r != null) byRef.TryAdd(r, (group, entry));
            }
        }

        var selected = new Dictionary<string, (string Group, JsonElement Entry)>(StringComparer.Ordinal);
        foreach (var (r, ge) in byRef)
        {
            if (matchedIds.Contains(Unwrap(r)) || CategoryMatchesQuestion(ge.Entry, question))
                selected.TryAdd(r, ge);
        }

        // 表级别名（动物→羊/野猪/牛；作物→谷物/蔬菜）
        if (table.Value.TryGetProperty("aliases", out var ta) && ta.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in ta.EnumerateArray())
            {
                var az = a.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String
                    ? z.GetString() ?? ""
                    : "";
                if (az.Length < 2 || !question.Contains(az, StringComparison.Ordinal)) continue;
                if (!a.TryGetProperty("whats", out var whats) || whats.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var w in whats.EnumerateArray())
                {
                    var wid = w.GetString();
                    if (wid != null && byRef.TryGetValue(wid, out var ge)) selected.TryAdd(wid, ge);
                }
            }
        }

        if (selected.Count == 0 || selected.Count > MaxFacts)
        {
            facts.Add(new Dictionary<string, object?> { ["kind"] = "score_table", ["table"] = table.Value });
            return;
        }

        // 问题中的唯一整数 → 程序查表/乘法直接给数（「3 个空农场格」→ -3；「6 头牛」→ +4）
        var ints = new List<int>();
        foreach (Match m in IntegerRegex.Matches(question)) ints.Add(int.Parse(m.Value));
        var n = ints.Count == 1 ? ints[0] : (int?)null;

        foreach (var (_, ge) in selected)
        {
            var (group, entry) = ge;
            var fact = new Dictionary<string, object?>
            {
                ["kind"] = GroupToKind(group),
                ["subject"] = EntryRef(entry),
                ["zh"] = EntryZh(entry)
            };
            switch (group)
            {
                case "by_count":
                    fact["rows"] = entry.GetProperty("rows");
                    if (entry.TryGetProperty("note", out var note)) fact["note"] = note;
                    if (n.HasValue)
                        foreach (var row in entry.GetProperty("rows").EnumerateArray())
                        {
                            var min = row.GetProperty("min").GetInt32();
                            var max = row.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number
                                ? (int?)mx.GetInt32()
                                : null;
                            if (n >= min && (max == null || n <= max))
                                fact["computed"] = new { count = n.Value, vp = row.GetProperty("vp").GetInt32() };
                        }
                    break;
                case "per_unit":
                    fact["vp"] = entry.GetProperty("vp").GetInt32();
                    if (entry.TryGetProperty("unit", out var unit)) fact["unit"] = unit;
                    var d = entry.TryGetProperty("divisor", out var dv) && dv.ValueKind == JsonValueKind.Number
                        ? dv.GetInt32()
                        : 1;
                    if (d != 1) fact["divisor"] = d;
                    if (n.HasValue)
                        fact["computed"] = new { count = n.Value, vp = entry.GetProperty("vp").GetInt32() * n.Value / d };
                    break;
                case "by_material":
                    fact["rows"] = entry.GetProperty("rows");
                    break;
                default: // by_card
                    if (entry.TryGetProperty("rule", out var rule)) fact["rule"] = rule;
                    break;
            }
            facts.Add(fact);
        }
    }

    private static string? GroupToKind(string group) => group switch
    {
        "by_count" => "score_rows",
        "per_unit" => "score_per_unit",
        "by_material" => "score_by_material",
        _ => "score_by_card"
    };

    /// <summary>类别条目的 what 引用（&lt;field_tile&gt;）；无则 null。</summary>
    private static string? EntryRef(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("what", out var w) && w.ValueKind == JsonValueKind.String
            ? w.GetString()
            : null;

    private static string Unwrap(string r) =>
        r.Contains("::") ? r[(r.IndexOf("::", StringComparison.Ordinal) + 2)..] : r.Trim('<', '>');

    private static string? EntryZh(JsonElement entry) =>
        entry.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String
            ? z.GetString()
            : null;

    /// <summary>问题提到类别 zh 名或别名即选中（别名允许单字——「田」「牛」）。</summary>
    private static bool CategoryMatchesQuestion(JsonElement entry, string question)
    {
        var zh = EntryZh(entry);
        if (!string.IsNullOrEmpty(zh) && question.Contains(zh, StringComparison.Ordinal)) return true;
        if (entry.TryGetProperty("aliases", out var als) && als.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in als.EnumerateArray())
            {
                var s = a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                if (!string.IsNullOrEmpty(s) && question.Contains(s, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 数量事实：容量公式（代入示例绑定程序求值）+ quantity.numeric 分支（按问题关键词选分支）。
    /// 选中分支是全部分支的真子集时求和（播种谷物：自己 1 + 供应 2 = 3）；
    /// 无分支匹配时全部摆出不求和（互斥选项，如起始玩家 2 / 其他玩家 3）。
    /// </summary>
    private void ExtractQuantityFacts(List<JsonElement> matched, string question, List<object> facts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in matched)
        {
            var id = GetElementId(el);
            if (string.IsNullOrEmpty(id) || !seen.Add(id) || el.ValueKind != JsonValueKind.Object)
                continue;
            var subject = $"<{id}>";

            // 容量公式：程序代入示例绑定求值（牧场容量 = 2 × 格数 × 2^马厩数 → 16）
            if (el.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Object
                && cap.TryGetProperty("formula", out var formula))
            {
                var fact = new Dictionary<string, object?>
                {
                    ["kind"] = "capacity_formula",
                    ["subject"] = subject,
                    ["formula"] = formula
                };
                if (cap.TryGetProperty("examples", out var exs) && exs.ValueKind == JsonValueKind.Array)
                {
                    var computed = new List<object>();
                    foreach (var ex in exs.EnumerateArray())
                    {
                        if (ex.ValueKind != JsonValueKind.Object
                            || !ex.TryGetProperty("bindings", out var b))
                            continue;
                        var r = EvalFormula(formula, b);
                        if (!r.HasValue) continue;
                        var zh = ex.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String
                            ? z.GetString()
                            : null;
                        computed.Add(new { zh, result = FormatNumber(r.Value) });
                    }
                    if (computed.Count > 0) fact["computed"] = computed;
                }
                facts.Add(fact);
            }

            // quantity.numeric：收集子树里所有转移数量分支
            var branches = new List<JsonElement>();
            WalkNumericQuantities(el, branches);
            if (branches.Count == 0) continue;

            var selectedBranches = new List<JsonElement>();
            foreach (var b in branches)
            {
                if (BranchMatchesQuestion(b, question)) selectedBranches.Add(b);
            }
            if (selectedBranches.Count == 0) selectedBranches = branches;

            var display = new List<object>();
            string? unit = null;
            foreach (var b in selectedBranches)
            {
                var item = new Dictionary<string, object?> { ["value"] = b.GetProperty("value").GetInt32() };
                if (b.TryGetProperty("when", out var when) && when.ValueKind == JsonValueKind.Object
                    && when.TryGetProperty("zh", out var wz) && wz.ValueKind == JsonValueKind.String)
                    item["when"] = wz.GetString();
                if (b.TryGetProperty("per", out var per) && per.ValueKind == JsonValueKind.Object
                    && per.TryGetProperty("zh", out var pz) && pz.ValueKind == JsonValueKind.String)
                    item["per"] = pz.GetString();
                if (b.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
                    && from.TryGetProperty("zh", out var fz) && fz.ValueKind == JsonValueKind.String)
                    item["from"] = fz.GetString();
                if (unit == null && b.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String)
                    unit = u.GetString();
                display.Add(item);
            }

            var qfact = new Dictionary<string, object?>
            {
                ["kind"] = "quantity_numeric",
                ["subject"] = subject,
                ["branches"] = display
            };
            if (unit != null) qfact["unit"] = unit;
            if (selectedBranches.Count > 0 && selectedBranches.Count < branches.Count)
                qfact["total"] = selectedBranches.Sum(b => b.GetProperty("value").GetInt32());
            facts.Add(qfact);
        }
    }

    /// <summary>收集子树中所有 quantity.numeric 的分支数组。</summary>
    private static void WalkNumericQuantities(JsonElement node, List<JsonElement> branches)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("numeric", out var num) && num.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in num.EnumerateArray())
                {
                    if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("value", out var v)
                        && v.ValueKind == JsonValueKind.Number)
                        branches.Add(b);
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkNumericQuantities(prop.Value, branches);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkNumericQuantities(item, branches);
        }
    }

    /// <summary>分支选择：无条件分支恒选；when.zh 的任一词（、，/空格分隔）出现在问题中即选中。</summary>
    private static bool BranchMatchesQuestion(JsonElement branch, string question)
    {
        if (!branch.TryGetProperty("when", out var when)) return true;
        if (when.ValueKind != JsonValueKind.Object || !when.TryGetProperty("zh", out var zh)
            || zh.ValueKind != JsonValueKind.String)
            return true;
        foreach (var seg in zh.GetString()!.Split('、', '，', ',', '/', ' '))
        {
            if (seg.Length > 0 && question.Contains(seg, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>求值小公式：op ∈ {*, +, ^}；operand 为数字 / 绑定变量（{"var": name}）/ 嵌套公式。</summary>
    private static double? EvalFormula(JsonElement node, JsonElement bindings)
    {
        if (node.ValueKind == JsonValueKind.Number) return node.GetDouble();
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("var", out var v) && v.ValueKind == JsonValueKind.String
                && bindings.ValueKind == JsonValueKind.Object
                && bindings.TryGetProperty(v.GetString()!, out var bv))
                return bv.ValueKind == JsonValueKind.Number ? bv.GetDouble() : null;

            if (node.TryGetProperty("op", out var op) && node.TryGetProperty("operands", out var ops)
                && ops.ValueKind == JsonValueKind.Array)
            {
                var vals = new List<double>();
                foreach (var o in ops.EnumerateArray())
                {
                    var r = EvalFormula(o, bindings);
                    if (!r.HasValue) return null;
                    vals.Add(r.Value);
                }
                if (vals.Count == 0) return null;
                return op.GetString() switch
                {
                    "*" => vals.Aggregate(1.0, (a, b) => a * b),
                    "+" => vals.Sum(),
                    "^" => vals.Skip(1).Aggregate(vals[0], (a, b) => Math.Pow(a, b)),
                    _ => null
                };
            }
        }
        return null;
    }

    /// <summary>整数则去掉小数点（16.0 → 16），否则保留原值。</summary>
    private static object FormatNumber(double d)
    {
        var rounded = Math.Round(d);
        return Math.Abs(d - rounded) < 1e-9 ? (object)(long)rounded : d;
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
                if (!string.IsNullOrEmpty(id))
                {
                    if (node.TryGetProperty("name", out var name)
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
                    // flow 节点别名（与概念同形）——如 give_starting_food 别名「开局食物」
                    if (node.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
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
            foreach (var prop in node.EnumerateObject())
                WalkFlowNames(prop.Value, add);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowNames(item, add);
        }
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
    /// <summary>事实卡（广播第三站）：数量/计分题的程序化计算事实（容量公式求值、计分表查表、数量分支求和）。无结构化数据时为 null，序列化省略。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? Facts { get; set; }
}

public class ListConceptsResult
{
    public Dictionary<string, List<ConceptSummary>> ByType { get; set; } = new();
    public int TotalCount { get; set; }
    public string Note { get; set; } = "Exhaustive listing of ALL concept IDs and names grouped by type. Use this to confirm a concept doesn't exist or to browse the full catalog.";
}
