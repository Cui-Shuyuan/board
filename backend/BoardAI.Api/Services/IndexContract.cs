using System.Security.Cryptography;
using System.Text;

namespace BoardAI.Api.Services;

/// <summary>
/// Shared naming/version contract for the full-text and name-only Qdrant
/// index.  Stable query names are aliases; concrete collections are versioned.
/// The UUID derivation and canonical version line must stay in sync with
/// tools/indexing/rebuild_index.py.
/// </summary>
public static class IndexContract
{
    public const string Schema = "board-index/v1";

    public static string CollectionPrefix(string gameId) => $"board_{gameId}";

    public static string ActiveAlias(string gameId, bool nameOnly) =>
        nameOnly ? $"{CollectionPrefix(gameId)}__active_name" : $"{CollectionPrefix(gameId)}__active";

    public static string LegacyCollectionName(string gameId, bool nameOnly) =>
        nameOnly ? $"{CollectionPrefix(gameId)}_name" : CollectionPrefix(gameId);

    public static string VersionedCollectionName(string gameId, string version, bool nameOnly)
    {
        var name = $"{CollectionPrefix(gameId)}__v{version}";
        return nameOnly ? $"{name}__name" : name;
    }

    public static string ComputeIndexVersion(
        string gameId,
        string modelId,
        int dimension,
        IEnumerable<ConceptIndexItem> items)
    {
        var builder = new StringBuilder();
        builder.Append(Schema).Append('\n');
        builder.Append(gameId).Append('\n');
        builder.Append(modelId).Append('\n');
        builder.Append(dimension).Append('\n');

        foreach (var item in items
                     .OrderBy(item => item.Source, StringComparer.Ordinal)
                     .ThenBy(item => item.ConceptId, StringComparer.Ordinal)
                     .ThenBy(item => item.Type, StringComparer.Ordinal)
                     .ThenBy(item => item.NameZh ?? "", StringComparer.Ordinal)
                     .ThenBy(item => item.NameEn ?? "", StringComparer.Ordinal)
                     .ThenBy(item => item.NameText ?? "", StringComparer.Ordinal)
                     .ThenBy(item => item.SearchText, StringComparer.Ordinal))
        {
            builder
                .Append(item.Source).Append('\t')
                .Append(item.ConceptId).Append('\t')
                .Append(item.Type).Append('\t')
                .Append(item.NameZh ?? "").Append('\t')
                .Append(item.NameEn ?? "").Append('\t')
                .Append(item.NameText ?? "").Append('\t')
                .Append(item.SearchText)
                .Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Python-compatible UUID: SHA-256, first 16 bytes, version/variant bits,
    /// rendered in big-endian byte order (same as Python uuid.UUID(bytes=...)).
    /// The point id includes both source and concept id so the same concept in
    /// ontology/game/instance/flow sources cannot overwrite another source.
    /// </summary>
    public static string ComputePointId(string gameId, string source, string conceptId, bool nameOnly)
    {
        var input = $"{gameId}::{source}::{conceptId}" + (nameOnly ? "_name" : "");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        // Must mirror make_uuid() in rebuild_index.py exactly, including
        // its byte positions, so CLI/API point identities remain interchangeable.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return string.Create(36, bytes, static (span, value) =>
        {
            const string hex = "0123456789abcdef";
            var pos = 0;
            for (var i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10)
                    span[pos++] = '-';
                span[pos++] = hex[value[i] >> 4];
                span[pos++] = hex[value[i] & 0x0F];
            }
        });
    }
}

/// <summary>
/// Result metadata for a completed, atomically switched index build.
/// </summary>
public sealed record IndexBuildResult
{
    public required string GameId { get; init; }
    public required string ModelId { get; init; }
    public required string Version { get; init; }
    public required string FullCollection { get; init; }
    public required string NameCollection { get; init; }
    public required string FullAlias { get; init; }
    public required string NameAlias { get; init; }
    public required int FullCount { get; init; }
    public required int NameCount { get; init; }
    public required bool ReusedExistingCollections { get; init; }
}
