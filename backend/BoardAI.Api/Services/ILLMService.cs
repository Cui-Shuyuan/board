using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

public interface ILLMService
{
    Task<LLMChatResponse> ChatWithMessagesAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);
}
