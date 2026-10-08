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
    /// <summary>本次 execute_plan 绑定的规则内容版本（快照哈希）；直接构造 plan 服务且无请求 scope 时为 null。</summary>
    public string? RulesVersion { get; set; }
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
