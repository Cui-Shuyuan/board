using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

// Respond-only diagnostic: load a frozen serialized evidence pack, use the same
// program prompt + BuildRespondMessages + submit_answer tool as production, and
// call the answer model exactly once. It does not invoke Understand or retrieval.
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: RespondPackProbe <repo-base> <game> <question> <pack-json> <out-json>");
    return 2;
}

var repoBase = Path.GetFullPath(args[0]);
var game = args[1];
var question = args[2];
var packPath = Path.GetFullPath(args[3]);
var outPath = Path.GetFullPath(args[4]);

var appSettingsPath = Path.Combine(repoBase, "backend", "BoardAI.Api", "appsettings.json");
using var appSettings = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
var llmOptions = JsonSerializer.Deserialize<LLMOptions>(
    appSettings.RootElement.GetProperty("LLM").GetRawText(),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("could not load LLMOptions");
var systemPrompt = llmOptions.ProgramSystemPrompt.Replace("{game_name}", game, StringComparison.OrdinalIgnoreCase);
var baseMessages = new List<ChatMessage>
{
    new() { Role = "system", Content = systemPrompt },
    new() { Role = "user", Content = question }
};

var assembly = typeof(GameRulesService).Assembly;
var packType = assembly.GetType("BoardAI.Api.Services.RuleEvidencePack", throwOnError: true)!;
var pack = JsonSerializer.Deserialize(
    File.ReadAllText(packPath),
    packType,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("could not load evidence pack");
var orchestratorType = assembly.GetType("BoardAI.Api.Services.ProgramControlledChatOrchestrator", throwOnError: true)!;
var buildMessages = orchestratorType.GetMethod("BuildRespondMessages", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(orchestratorType.FullName, "BuildRespondMessages");
var buildTool = orchestratorType.GetMethod("BuildAnswerTool", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(orchestratorType.FullName, "BuildAnswerTool");
var messages = (List<ChatMessage>)buildMessages.Invoke(null, new object?[] { baseMessages, question, pack })!;
var tool = (ToolDefinition)buildTool.Invoke(null, null)!;

using var httpClient = new HttpClient();
var llm = new DeepSeekLLMService(httpClient, Options.Create(llmOptions));
var started = DateTimeOffset.UtcNow;
var response = await llm.ChatWithMessagesAsync(messages, new List<ToolDefinition> { tool }, CancellationToken.None);
var elapsedMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
var assistant = response.Choices?.FirstOrDefault()?.Message;
var toolCall = assistant?.ToolCalls?.FirstOrDefault();
JsonElement? arguments = null;
if (toolCall != null)
{
    try { arguments = JsonDocument.Parse(toolCall.Function.Arguments).RootElement.Clone(); }
    catch (JsonException) { }
}

var result = new
{
    game,
    question,
    elapsedMs = Math.Round(elapsedMs, 2),
    prompt_tokens = response.Usage?.PromptTokens,
    completion_tokens = response.Usage?.CompletionTokens,
    reasoning_tokens = response.Usage?.CompletionTokenDetails?.ReasoningTokens,
    tool_name = toolCall?.Function.Name,
    arguments,
    raw_arguments = toolCall?.Function.Arguments,
    message_content = assistant?.Content
};
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllText(outPath, JsonSerializer.Serialize(result, jsonOptions));
Console.WriteLine(outPath);
return 0;
