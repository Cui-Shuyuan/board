using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Internal diagnostic harness: freeze a query plan, run only the evidence-pack
// builder, and dump the exact serialized pack. It intentionally uses reflection
// so it can also be compiled against an older checkout for before/after diffs.
if (args.Length < 4)
{
    Console.Error.WriteLine("usage: EvidencePackDump <repo-base> <game> <plan-json> <out-json> [max-pack-chars]");
    return 2;
}

var repoBase = Path.GetFullPath(args[0]);
var game = args[1];
var planPath = Path.GetFullPath(args[2]);
var outPath = Path.GetFullPath(args[3]);
var maxPackChars = args.Length > 4 ? int.Parse(args[4]) : 60000;

var plan = JsonSerializer.Deserialize<List<PlanQuestion>>(
    File.ReadAllText(planPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("plan json must be an array");

using var rules = new GameRulesService(
    Options.Create(new RulesOptions { BasePath = repoBase }),
    vectorSearch: null);
var assembly = typeof(GameRulesService).Assembly;
var builderType = assembly.GetType("BoardAI.Api.Services.RuleEvidencePackBuilder", throwOnError: true)!;
var questionType = assembly.GetType("BoardAI.Api.Services.ProgramSubQuestion", throwOnError: true)!;
var ctor = builderType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    .Single(candidate => candidate.GetParameters().Length == 3);
var builder = ctor.Invoke(new object?[]
{
    rules,
    new LLMOptions
    {
        MaxCandidatesPerQuestion = 3,
        MaxEvidencePerQuestion = 8,
        MaxEvidenceTotal = 24,
        MaxEvidenceJsonChars = 4000,
        MaxEvidencePackChars = maxPackChars,
        MaxSupplementQueries = 1
    },
    NullLogger.Instance
});

var listType = typeof(List<>).MakeGenericType(questionType);
var questions = (IList)Activator.CreateInstance(listType)!;
foreach (var item in plan)
{
    var question = Activator.CreateInstance(questionType, nonPublic: true)!;
    questionType.GetProperty("QuestionId")!.SetValue(question, item.QuestionId);
    questionType.GetProperty("Entity")!.SetValue(question, item.Entity);
    questionType.GetProperty("Relation")!.SetValue(question, item.Relation);
    questionType.GetProperty("Intent")!.SetValue(question, item.Intent);
    questionType.GetProperty("UserContext")!.SetValue(question, item.UserContext);
    questions.Add(question);
}

var build = builderType.GetMethod("BuildAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(builderType.FullName, "BuildAsync");
var task = (Task)build.Invoke(builder, new object?[] { game, questions, CancellationToken.None })!;
await task;
var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};
var json = JsonSerializer.Serialize(result, result.GetType(), jsonOptions);
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllText(outPath, json);
Console.WriteLine(outPath);
return 0;

internal sealed class PlanQuestion
{
    [JsonPropertyName("question_id")]
    public string QuestionId { get; set; } = "";
    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "";
    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "";
    [JsonPropertyName("user_context")]
    public string? UserContext { get; set; }
}
