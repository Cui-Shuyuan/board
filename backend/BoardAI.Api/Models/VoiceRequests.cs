using System.Text.Json.Serialization;

namespace BoardAI.Api.Models;

public class TtsRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }
}

public class AsrOnceResponse
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;
}
