using System.Collections;
using System.Text;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// Coarse retained-size estimates for cache admission and diagnostics. These
/// numbers intentionally include a safety multiplier for object/header overhead;
/// they are not GC-reported sizes and must not be presented as a process limit.
/// </summary>
internal static class RulesCacheSizeEstimator
{
    private const int ObjectOverheadBytes = 64;
    private const int RawDocumentMultiplier = 2;

    public static long EstimateDocument(JsonDocument? document)
    {
        if (document == null) return 0;
        var rawBytes = Encoding.UTF8.GetByteCount(document.RootElement.GetRawText());
        return ObjectOverheadBytes + rawBytes * RawDocumentMultiplier;
    }

    public static long EstimateString(string? value)
        => value == null ? 0 : 24 + value.Length * 2L;

    public static long EstimateObject(object? value)
    {
        switch (value)
        {
            case null:
                return 0;
            case string text:
                return EstimateString(text);
            case JsonElement element:
                return ObjectOverheadBytes + Encoding.UTF8.GetByteCount(element.GetRawText()) * 2L;
            case byte[] bytes:
                return ObjectOverheadBytes + bytes.LongLength;
            case IDictionary dictionary:
            {
                long total = ObjectOverheadBytes;
                foreach (DictionaryEntry entry in dictionary)
                    total += EstimateObject(entry.Key) + EstimateObject(entry.Value);
                return total;
            }
            case IEnumerable enumerable:
            {
                long total = ObjectOverheadBytes;
                foreach (var item in enumerable)
                    total += EstimateObject(item);
                return total;
            }
            default:
                return ObjectOverheadBytes + 32;
        }
    }

    public static long EstimateStringDictionary(IReadOnlyDictionary<string, string>? dictionary)
    {
        if (dictionary == null) return 0;
        long total = ObjectOverheadBytes;
        foreach (var (key, value) in dictionary)
            total += EstimateString(key) + EstimateString(value);
        return total;
    }

    public static long EstimateNameLookup(
        IReadOnlyDictionary<string, IReadOnlyList<ExactNameMatch>>? dictionary)
    {
        if (dictionary == null) return 0;
        long total = ObjectOverheadBytes;
        foreach (var (key, matches) in dictionary)
        {
            total += EstimateString(key) + ObjectOverheadBytes;
            foreach (var match in matches)
                total += EstimateString(match.Id) + EstimateString(match.Kind);
        }
        return total;
    }
}
