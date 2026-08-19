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
}
