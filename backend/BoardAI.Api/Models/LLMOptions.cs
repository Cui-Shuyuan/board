namespace BoardAI.Api.Models;

public class LLMOptions
{
    public string Provider { get; set; } = "DeepSeek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1/";
    public string Model { get; set; } = "deepseek-v4-pro";
    public string ApiKey { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    /// <summary>
    /// 思考模式控制：default（不传参数，模型默认开启）| disabled（thinking.type=disabled）|
    /// low（reasoning_effort=low）。disabled 与 low 不能同时使用。
    /// </summary>
    public string Thinking { get; set; } = "default";

    /// <summary>单次 /api/chat 请求内，最多允许的带工具 LLM 轮数；达到后只允许一次无工具总结。</summary>
    public int MaxToolRounds { get; set; } = 6;

    /// <summary>单次 /api/chat 请求的总预算（秒）；到点后取消所有进行中的 LLM/tool 调用。</summary>
    public int MaxRequestSeconds { get; set; } = 60;

    /// <summary>连续 tool error、连续相同失败或连续相同工具调用的停止阈值。</summary>
    public int MaxRepeatedFailures { get; set; } = 3;

    /// <summary>发送给 LLM 的历史消息上限（只裁剪客户端历史；系统和工具消息由请求内预算控制）。</summary>
    public int MaxConversationMessages { get; set; } = 40;
}
