using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

/// <summary>
/// OpenAI 兼容的本地/通用 LLM 实现（Ollama / llama.cpp / vLLM 等）。
/// 与 DeepSeek 版区别：不传 DeepSeek 专属的 thinking / reasoning_effort 字段。
/// </summary>
public class OpenAICompatibleLLMService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly LLMOptions _options;

    public OpenAICompatibleLLMService(HttpClient httpClient, IOptions<LLMOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<LLMChatResponse> ChatWithMessagesAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            ["tools"] = tools?.Count > 0 ? tools : null
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
