using System.Text.Json.Serialization;

namespace BoardAI.Api.Models;

public class ChatRequest
{
    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();
}
