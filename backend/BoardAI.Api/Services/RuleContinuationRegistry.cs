using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 单次聊天请求内登记的程序生成续读句柄。模型只能回传已登记 handle，
/// 不能凭句柄自行拼接任意路径；规则版本不一致时句柄失效。
/// </summary>
internal sealed class RuleContinuationRegistry
{
    private readonly Dictionary<string, ContinuationRegistration> _byHandle = new(StringComparer.Ordinal);

    public int Count => _byHandle.Count;

    public bool TryGet(string handle, out ContinuationRegistration registration)
        => _byHandle.TryGetValue(handle, out registration!);

    public void ObserveToolResult(string toolResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var rulesVersion = root.TryGetProperty("rules_version", out var version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null;
            if (!root.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array)
                return;

            foreach (var query in queries.EnumerateArray())
            {
                if (!query.TryGetProperty("omissions", out var omissions)
                    || omissions.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var omission in omissions.EnumerateArray())
                {
                    if (!omission.TryGetProperty("continuation", out var continuation)
                        || continuation.ValueKind != JsonValueKind.Object)
                        continue;
                    var handle = ReadString(continuation, "handle");
                    var operation = ReadString(continuation, "operation");
                    if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(operation))
                        continue;
                    _byHandle[handle] = new ContinuationRegistration
                    {
                        Handle = handle,
                        Operation = operation,
                        Entity = ReadString(continuation, "entity"),
                        Scope = ReadString(continuation, "scope"),
                        Intent = ReadString(continuation, "intent"),
                        Offset = ReadInt(continuation, "offset"),
                        Limit = ReadInt(continuation, "limit"),
                        Include = ReadStringArray(continuation, "include"),
                        RulesVersion = rulesVersion
                    };
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON results do not register handles.
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}

internal sealed class ContinuationRegistration
{
    public string Handle { get; init; } = "";
    public string Operation { get; init; } = "";
    public string? Entity { get; init; }
    public string? Scope { get; init; }
    public string? Intent { get; init; }
    public int? Offset { get; init; }
    public int? Limit { get; init; }
    public List<string> Include { get; init; } = new();
    public string? RulesVersion { get; init; }
}
