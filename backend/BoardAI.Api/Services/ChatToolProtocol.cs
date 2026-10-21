using System.Text.Encodings.Web;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// legacy 工具协议的错误包装、结果归一化与日志格式化。工具结果和错误始终走同一套
/// relaxed JSON 序列化选项，避免动态错误文本破坏 JSON 结构。
/// </summary>
internal static class ChatToolProtocol
{
    internal static readonly JsonSerializerOptions ToolResultOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions PrettyLogOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Serializes tool errors through the same relaxed JSON options as tool
    /// results. Dynamic error text must never be concatenated into JSON.
    /// </summary>
    internal static string SerializeToolError(string error)
        => JsonSerializer.Serialize(new { error }, ToolResultOptions);

    /// <summary>
    /// Inspects a tool result and normalizes protocol failures. A result is a
    /// failure when it is invalid JSON or when it is a JSON object with an
    /// "error" property. Invalid JSON is replaced with a serialized protocol
    /// error so the model never receives an unparseable tool message.
    /// </summary>
    internal static bool TryNormalizeToolResult(
        string result,
        out string normalizedResult,
        out string errorText)
    {
        normalizedResult = result;
        errorText = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                errorText = error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? string.Empty
                    : error.GetRawText();
                return true;
            }
        }
        catch (JsonException)
        {
            errorText = "工具结果不是合法 JSON，已按协议错误处理。";
            normalizedResult = SerializeToolError(errorText);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compatibility helper used by tests: invalid JSON is reported as a tool
    /// failure instead of being treated as a successful result.
    /// </summary>
    internal static bool TryGetToolError(string result, out string errorText)
        => TryNormalizeToolResult(result, out _, out errorText);

    internal static string FormatForLog(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, PrettyLogOptions);
        }
        catch
        {
            return json;
        }
    }
}
