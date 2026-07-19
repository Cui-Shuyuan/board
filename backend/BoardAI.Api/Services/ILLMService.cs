namespace BoardAI.Api.Services;

public interface ILLMService
{
    Task<string> ChatAsync(string userInput, CancellationToken cancellationToken = default);
}
