using System.Net;
using System.Text;
using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class DeepSeekLLMServiceTests
{
    [Fact]
    public async Task ToolChoiceRequired_IsSentOnlyWhenToolsArePresent()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var service = new DeepSeekLLMService(http, Options.Create(new LLMOptions
        {
            BaseUrl = "https://example.test/v1/",
            ApiKey = "test-key",
            Thinking = "disabled",
            ToolChoice = "required"
        }));
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "probe",
                    Description = "probe",
                    Parameters = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement
                }
            }
        };

        await service.ChatWithMessagesAsync(
            new List<ChatMessage> { new() { Role = "user", Content = "hi" } }, tools);
        using var withTools = JsonDocument.Parse(handler.LastBody);
        Assert.Equal("required", withTools.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal("disabled", withTools.RootElement.GetProperty("thinking").GetProperty("type").GetString());

        await service.ChatWithMessagesAsync(
            new List<ChatMessage> { new() { Role = "user", Content = "hi" } }, tools: null);
        using var withoutTools = JsonDocument.Parse(handler.LastBody);
        Assert.False(withoutTools.RootElement.TryGetProperty("tool_choice", out _));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
