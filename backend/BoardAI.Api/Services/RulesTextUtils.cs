using System.Text.Json;

namespace BoardAI.Api.Services;

internal static class RulesTextUtils
{
    public static (string? ns, string localId) ParseNamespace(string id)
    {
        // Strip surrounding angle brackets if any, e.g. "<ontology::resource>" -> "ontology::resource"
        var trimmed = id.Trim('<', '>');
        var parts = trimmed.Split(new[] { "::" }, StringSplitOptions.None);
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
        {
            return (parts[0], parts[1]);
        }
        return (null, trimmed);
    }

    public static string? ExtractDescriptionZh(JsonElement element)
    {
        if (element.TryGetProperty("description", out var desc) &&
            desc.TryGetProperty("zh", out var zh))
        {
            return zh.GetString();
        }
        return null;
    }

    /// <summary>读取概念的 id 属性（无则空字符串）。</summary>
    public static string GetElementId(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
    }
}
