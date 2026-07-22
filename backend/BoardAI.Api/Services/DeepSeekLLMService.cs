using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public class DeepSeekLLMService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly LLMOptions _options;

    public DeepSeekLLMService(HttpClient httpClient, IOptions<LLMOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<LLMChatResponse> ChatWithMessagesAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model = _options.Model,
            messages = messages,
            tools = tools?.Count > 0 ? tools : null
        };

        var response = await _httpClient.PostAsJsonAsync(
            "chat/completions",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<LLMChatResponse>(cancellationToken);

        if (result?.Choices == null || result.Choices.Count == 0)
        {
            throw new InvalidOperationException("LLM returned empty response.");
        }

        return result;
    }
}

public class DeepSeekResponse
{
    [JsonPropertyName("choices")]
    public List<Choice>? Choices { get; set; }
}

public class Choice
{
    [JsonPropertyName("message")]
    public Message? Message { get; set; }
}

public class Message
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
