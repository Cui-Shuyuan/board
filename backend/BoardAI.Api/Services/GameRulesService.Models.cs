using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardAI.Api.Services;

public class ConceptSummary
{
    public string Id { get; set; } = string.Empty;
    /// <summary>稳定寻址路径；局部槽位为 owner.slot，普通概念与 Id 相同。</summary>
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public float Score { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, JsonElement>? Media { get; set; }
    /// <summary>每个切分词的双通道匹配分（仅 search_concepts 返回时填充；单词查询时该词即整句，FullQueryScore 为 null）。</summary>
    public Dictionary<string, ChannelScores>? TermScores { get; set; }
    /// <summary>整句查询的双通道匹配分（多词查询时存在；整句主导排序，配合 SubqueryBoost 与 Score 对账）。</summary>
    public ChannelScores? FullQueryScore { get; set; }
    /// <summary>完整查询的匹配证据；自动解析只检查此项，不把子词加分当成置信度。</summary>
    public ChannelScores? PhraseScore { get; set; }
    /// <summary>子查询的小幅补充分；Score = PhraseScore.Vector + PhraseScore.Keyword + SubqueryBoost。</summary>
    public float SubqueryBoost { get; set; }
    /// <summary>仅名称补充候选的原始分。计划里的 Score 以 0.55 为上限，PhraseScore 留空，不参与自动解析。</summary>
    public float? NameMatchScore { get; set; }
    /// <summary>候选来自哪条检索证据：exact_identity / intent / entity / intent+entity / reference。仅用于日志与回归解释。</summary>
    public string? MatchReason { get; set; }
    /// <summary>not_read | auto_read；正式读取不推导目标身份已确认。</summary>
    public string? ReadStatus { get; set; }
    /// <summary>仅描述候选内容是否已正式读到，与 target_resolved 分开。</summary>
    public bool? TargetResolved { get; set; }
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
    public string Strategy { get; set; } = "phrase_first_vector_keyword";
    public string Note { get; set; } = "Top results only — NOT exhaustive. If you need to see ALL concepts (e.g., to browse what exists), use list_concept_ids.";
}

public class GetConceptResult
{
    public List<JsonElement> Matched { get; set; } = new();
    public List<JsonElement> Related { get; set; } = new();
    public string Note { get; set; } =
        "related 是 matched 直接引用的概念，已自动扩展一层；related 内概念的引用已标注中文名，如需更深一层的详情请用其 ID 继续调用 get_concept。";
}



/// <summary>
/// 统一查询结果中的正式负载类型。类型字符串沿用契约名，未知类型必须由消费者
/// 明确报 unsupported，而不是继续当作 ok + 空正文。
/// </summary>
public static class QueryPayloadTypes
{
    public const string Definition = "definition";
    public const string CatalogPage = "catalog_page";
    public const string FlowSlice = "flow_slice";
    public const string RelationFacts = "relation_facts";
    public const string ComputedFact = "computed_fact";
    public const string MemberSet = "member_set";
}

/// <summary>一次查询的结果覆盖范围，不代表最终自然语言回答一定充分。</summary>
public static class QueryCoverageStatuses
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Unknown = "unknown";
}

/// <summary>目标解析状态；与 coverage 分开记录。</summary>
public static class QueryResolutionStatuses
{
    public const string Exact = "exact";
    public const string Candidates = "candidates";
    public const string NotFound = "not_found";
    public const string Unsupported = "unsupported";
    public const string Invalid = "invalid";
}

/// <summary>正式读取到的有类型负载。Content 保留原始节点/章节；Data 只放该负载自己的结构化字段。</summary>
public sealed class QueryPayload
{
    public string Type { get; set; } = string.Empty;
    public string CanonicalPath { get; set; } = string.Empty;
    public string? RulesVersion { get; set; }
    /// <summary>complete | partial | unknown；只描述本负载覆盖的字段/范围。</summary>
    public string Completeness { get; set; } = QueryCoverageStatuses.Complete;
    public JsonElement? Content { get; set; }
    public Dictionary<string, JsonElement>? Data { get; set; }
    public string? DefinitionSource { get; set; }
}

/// <summary>无法交付某一部分时的明确原因与续读入口。</summary>
public sealed class QueryOmission
{
    public string Reason { get; set; } = string.Empty;
    public string? CanonicalPath { get; set; }
    public string? FieldPath { get; set; }
    public QueryContinuation? Continuation { get; set; }
}

/// <summary>程序生成的续读描述；模型不得自行拼接任意路径。</summary>
public sealed class QueryContinuation
{
    /// <summary>程序生成、请求内可登记的续读句柄；模型不自行构造路径。</summary>
    public string? Handle { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string? Entity { get; set; }
    public string? Scope { get; set; }
    /// <summary>绑定该续读的原始查询语义；rule_lookup 分页必须按它重新排序。</summary>
    public string? Intent { get; set; }
    public int? Offset { get; set; }
    public int? Limit { get; set; }
    public List<string> Include { get; set; } = new();
}

/// <summary>错误/空结果分类，避免把“规则库没有”与“预算没读完”混成一句话。</summary>
public static class QueryOmissionReasons
{
    public const string NoRuleCoverage = "no_rule_coverage";
    public const string UnsupportedRelation = "unsupported_relation";
    public const string FieldMissing = "field_missing";
    public const string BudgetTruncated = "budget_truncated";
    public const string AmbiguousIdentity = "ambiguous_identity";
    public const string Oversized = "oversized";
    public const string ContinuationRequired = "continuation_required";
    public const string InvalidRequest = "invalid_request";
}

public class PlanExecutionResult
{
    public string RequestId { get; set; } = "";
    public string Game { get; set; } = "";
    public List<PlanItemResult> Results { get; set; } = new();
    public string Note { get; set; } = "";
    /// <summary>本次 execute_plan 绑定的规则内容版本（快照哈希）；直接构造 plan 服务且无请求 scope 时为 null。</summary>
    public string? RulesVersion { get; set; }
}

public class PlanItemResult
{
    /// <summary>本 query 在当次 execute_plan 中的零基下标。</summary>
    public int QueryIndex { get; set; }
    /// <summary>当次 execute_plan 内稳定 query id；用于账本与 trace 对账。</summary>
    public string QueryId { get; set; } = "";
    /// <summary>结果绑定的规则内容版本；由 ExecutePlanAsync 统一回填。</summary>
    public string? RulesVersion { get; set; }
    /// <summary>精确目标解析状态：exact | candidates | not_found | unsupported | invalid。</summary>
    public string Resolution { get; set; } = "";
    /// <summary>规范路径集合；身份以 (game, rules_version, canonical_path) 为准。</summary>
    public List<string> ResolvedTargets { get; set; } = new();
    /// <summary>有类型的正式负载；消费者必须穷举类型，未知类型显式 unsupported。</summary>
    public List<QueryPayload> Payloads { get; set; } = new();
    /// <summary>本次查询范围的数据覆盖：complete | partial | unknown。</summary>
    public string Coverage { get; set; } = QueryCoverageStatuses.Unknown;
    /// <summary>本次未交付的字段/范围及原因；不得用空数组伪装完整。</summary>
    public List<QueryOmission> Omissions { get; set; } = new();
    public string Relation { get; set; } = "";
    public string Entity { get; set; } = "";
    /// <summary>当前子问题要知道什么（用户意图）；单 query 缺省时可由服务端回填客人原问句。</summary>
    public string Intent { get; set; } = "";
    /// <summary>本次查询实际送入检索的名字/意图文本，用于排查实体定位与场景定位的差异。</summary>
    public List<string> RetrievalQueries { get; set; } = new();
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
    /// <summary>目录中的总条目数，不是当前页条数。</summary>
    public int TotalCount { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public bool HasMore { get; set; }
    public string Note { get; set; } = "Catalog page. total_count describes the full catalog; has_more and offset/limit describe this page. A page is not the complete catalog.";
}

/// <summary>正式读取结果中的稳定路径+原始 JSON 节点。</summary>
public sealed class ConceptPathHit
{
    public string CanonicalPath { get; set; } = string.Empty;
    public JsonElement Element { get; set; }
    /// <summary>仅 related 命中使用：引用该条目的父节点规范路径；matched 为 null。</summary>
    public string? ParentPath { get; set; }
}

/// <summary>带规范路径的概念读取结果，供事实包构建使用。</summary>
public sealed class ConceptReadResult
{
    public List<ConceptPathHit> Matched { get; set; } = new();
    public List<ConceptPathHit> Related { get; set; } = new();
    public string Note { get; set; } = "";
}
