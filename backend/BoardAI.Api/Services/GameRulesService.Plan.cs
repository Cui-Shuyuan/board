using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService
{

    /// <summary>游戏概念 id → type，用于 execute_plan 的 relation 级类型约束。</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _conceptTypeMaps = new();


    /// <summary>特定 relation 下，实体最可能属于的 concept 类型。</summary>
    private static readonly Dictionary<string, string[]> PlanRelationExpectedTypes = new()
    {
        ["condition"] = new[] { "actions", "triggers", "conditions", "flow" },
        ["ordering"] = new[] { "flow", "triggers", "actions" },
        ["boundary"] = new[] { "triggers", "conditions", "flow" },
    };


    private Dictionary<string, string> GetConceptTypeMap(string game)
    {
        if (_conceptTypeMaps.TryGetValue(game, out var cached)) return cached;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in GetConceptTypes(game))
            foreach (var summary in ListConcepts(game, type))
                if (!string.IsNullOrEmpty(summary.Id))
                    map[summary.Id] = type;
        _conceptTypeMaps[game] = map;
        return map;
    }


    private bool IsExpectedPlanType(string game, string relation, string conceptId)
    {
        if (string.IsNullOrEmpty(conceptId)) return false;
        if (!PlanRelationExpectedTypes.TryGetValue(relation, out var expected)) return true;
        var map = GetConceptTypeMap(game);
        return map.TryGetValue(conceptId, out var type) && expected.Contains(type);
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
            // Tier 2：程序自己跑语义检索补候选（相似度匹配交给向量库，不交给 LLM）。
            // 名称包含的确定性候选无条件保留；语义候选须过分数阈值。
            // 注意：语义要优先于“问题级直呼”。否则问题原文里偶然出现的区域名
            // （如「宝石供应堆」）会把 action 实体（如「拿取宝石」）错误拍板成 zone。
            var merged = new List<ConceptSummary>(candidates);
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < merged.Count; i++) indexById[merged[i].Id] = i;
            if (_vectorSearch != null)
            {
                // 名称索引擅长 action/短实体的消歧；完整索引擅长颜色/外观/描述型问法。
                // 两条路都不要滥用：entity 型（condition/ordering/boundary）走名称索引，
                // 描述型 explain 才走完整索引。
                // explain 里短实体多是概念名转述（走名称索引），长实体才更像
                // 颜色/外观/描述型问法（走完整索引）；condition/ordering/boundary
                // 仍一律走名称索引。
                var explainDescriptive = relation == "explain" && entity.Length > 6;
                var semantic = explainDescriptive
                    ? await SearchConceptsAsync(game, entity)
                    : await SearchConceptsAsync(game, entity, searchMode: "name");
                var semItems = semantic.Results
                    .Where(r => r.Score >= SemanticCandidateThreshold)
                    .OrderByDescending(r => r.Score)
                    .ToList();
                // relation 级类型优先：condition 问句不要被 zone/对象抢走实体。
                // 只有在存在符合类型的候选时才收窄，避免把原本可用的泛候选误杀。
                var expected = semItems.Where(c => IsExpectedPlanType(game, relation, c.Id)).ToList();
                if (expected.Count > 0) semItems = expected;
                foreach (var c in semItems)
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

            // 语义候选已经可用时，不再让“问题原文里出现的名字”抢走实体。
            // 只有语义也弱/为空时，才启用问题级直呼作为兜底。
            if (merged.Count == 0 || merged[0].Score < 0.55f)
            {
                var qhits = ResolveFromQuestion(game, question, out var qsource)
                    .Where(el => IsExpectedPlanType(game, relation, RulesTextUtils.GetElementId(el)))
                    .ToList();
                if (qhits.Count > 0)
                    return BuildOkResult(game, relation, entity, qhits, qsource, question);
            }

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
        var resolvedId = RulesTextUtils.GetElementId(matched[0]);

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
        _flowService.GetFlowPositions(game).TryGetValue(localId, out var pos);

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
        var lookup = _nameIndex.GetExactLookup(game);
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
        var map = _nameIndex.GetNameMap(game);
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

        var lookup = _nameIndex.GetExactLookup(game);

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
}
