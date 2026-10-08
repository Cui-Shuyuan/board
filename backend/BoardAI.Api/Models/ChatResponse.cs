using System.Text.Json.Serialization;

namespace BoardAI.Api.Models;

public class ChatResponse
{
    public string Reply { get; set; } = string.Empty;

    /// <summary>
    /// 逐查询证据元数据；旧客户端可忽略。只用于追溯/评测，不自动证明 LLM 最终每句话为真。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnswerEvidence? Evidence { get; set; }
}

/// <summary>一次回答的证据汇总：按 query 记录，而不是见到任意一条 ok 就把整条回答判为完全有据。</summary>
public class AnswerEvidence
{
    /// <summary>tier1 = 所有查询都命中规则数据；partial = 有命中也有缺失/未解决；tier2 = 只有候选兜底；tier3 = 无数据。</summary>
    public string Tier { get; set; } = "tier3";

    public bool HasData { get; set; }
    public bool HasCandidates { get; set; }
    public bool IsComplete { get; set; }
    public string? RulesVersion { get; set; }
    public int QueryCount { get; set; }
    public int OkCount { get; set; }
    public int UnresolvedCount { get; set; }
    public int NoMatchCount { get; set; }
    public int UnsupportedCount { get; set; }
    public List<QueryEvidence> Queries { get; set; } = new();
}

public class QueryEvidence
{
    public string Relation { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? RulesVersion { get; set; }
    public string? Message { get; set; }
    public bool HasData { get; set; }

    /// <summary>已命中概念（explain/condition/ordering/boundary/flow 的 Matched）。</summary>
    public List<EvidenceConcept> Matched { get; set; } = new();

    /// <summary>程序无法解析实体时给出的候选概念（unresolved/identify）。</summary>
    public List<EvidenceConcept> Candidates { get; set; } = new();

    /// <summary>命中概念直接引用的一层概念，供客户端定位上下文；不递归展开。</summary>
    public List<EvidenceConcept> References { get; set; } = new();

    public List<string> FlowContext { get; set; } = new();
}

public class EvidenceConcept
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Description { get; set; }
    public float? Score { get; set; }
}


/// <summary>聊天服务的内部返回值：回复与证据一起交给 Controller；旧 ProcessAsync 仍只取 Reply。</summary>
public sealed record ChatProcessResult(string Reply, AnswerEvidence Evidence);
