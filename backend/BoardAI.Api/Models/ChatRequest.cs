using System.Text.Json.Serialization;

namespace BoardAI.Api.Models;

public class ChatRequest
{
    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("context")]
    public ChatContext? Context { get; set; }
}

/// <summary>
/// Optional playback context supplied by the Android tutorial player.  All
/// fields are snake_case on the wire so the Kotlin client can send the same
/// payload shape it uses for the rest of the API.
/// </summary>
public class ChatContext
{
    [JsonPropertyName("game_name")]
    public string? GameName { get; set; }

    [JsonPropertyName("cue_id")]
    public string? CueId { get; set; }

    [JsonPropertyName("cue_index")]
    public int? CueIndex { get; set; }

    [JsonPropertyName("cue_text")]
    public string? CueText { get; set; }

    [JsonPropertyName("group_path")]
    public List<string>? GroupPath { get; set; }

    [JsonPropertyName("position")]
    public float? Position { get; set; }
}
