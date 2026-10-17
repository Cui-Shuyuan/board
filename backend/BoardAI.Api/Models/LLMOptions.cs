namespace BoardAI.Api.Models;

public class LLMOptions
{
    public string Provider { get; set; } = "DeepSeek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1/";
    public string Model { get; set; } = "deepseek-v4-pro";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>legacy 自由工具循环专用提示，必须与 a992cfe 基线保持一致。</summary>
    public string LegacySystemPrompt { get; set; } = string.Empty;
    /// <summary>program 两阶段编排专用提示；不包含 execute_plan 自由循环要求。</summary>
    public string ProgramSystemPrompt { get; set; } = string.Empty;
    /// <summary>
    /// 思考模式控制：default（不传参数，模型默认开启）| disabled（thinking.type=disabled）|
    /// low（reasoning_effort=low）。disabled 与 low 不能同时使用。
    /// </summary>
    public string Thinking { get; set; } = "default";

    /// <summary>
    /// 可选 tool_choice。空 = auto；required = 必须调用一个工具。
    /// 思考模式是否支持 required 由 provider 决定，需先做接口冒烟。
    /// </summary>
    public string ToolChoice { get; set; } = "";

    /// <summary>program = 程序控制的理解→取证→回答状态机；legacy = 仅用于本轮对照的旧自由工具循环。</summary>
    public string OrchestrationMode { get; set; } = "legacy";

    /// <summary>程序控制编排包含理解、回答、补充与协议修复在内的硬模型调用上限（建议 2–4）。</summary>
    public int MaxModelCallsPerRequest { get; set; } = 4;

    /// <summary>每个子问题最多主动读取的候选数。</summary>
    public int MaxCandidatesPerQuestion { get; set; } = 3;

    /// <summary>每个子问题最多装入事实包的正式证据条数。</summary>
    public int MaxEvidencePerQuestion { get; set; } = 8;

    /// <summary>整个请求最多装入事实包的正式证据条数。</summary>
    public int MaxEvidenceTotal { get; set; } = 24;

    /// <summary>单条证据序列化后的软上限；超出时改取必要字段投影，并记录 projection。</summary>
    public int MaxEvidenceJsonChars { get; set; } = 4000;

    /// <summary>事实包序列化后的总大小上限；超出时优先省略低优先级 related/candidate 证据。</summary>
    public int MaxEvidencePackChars { get; set; } = 60000;

    /// <summary>一次请求最多允许的补充检索次数。</summary>
    public int MaxSupplementQueries { get; set; } = 1;

    /// <summary>单次 /api/chat 请求内，最多允许的带工具 LLM 轮数；达到后只允许一次无工具总结。</summary>
    public int MaxToolRounds { get; set; } = 6;

    /// <summary>单轮 LLM 响应中最多执行的 tool call 数量；超出部分跳过并进入无工具总结。</summary>
    public int MaxToolCallsPerRound { get; set; } = 4;

    /// <summary>单次 /api/chat 请求内最多执行的 tool call 总数；达到后跳过剩余调用并进入无工具总结。</summary>
    public int MaxTotalToolCalls { get; set; } = 12;

    /// <summary>单次 /api/chat 请求的总预算（秒）；到点后取消所有进行中的 LLM/tool 调用。</summary>
    public int MaxRequestSeconds { get; set; } = 60;

    /// <summary>连续 tool error、连续相同失败或连续相同工具调用的停止阈值。</summary>
    public int MaxRepeatedFailures { get; set; } = 3;

    /// <summary>发送给 LLM 的历史消息上限（只裁剪客户端历史；系统和工具消息由请求内预算控制）。</summary>
    public int MaxConversationMessages { get; set; } = 40;
}
