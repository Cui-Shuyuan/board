namespace BoardAI.Api.Services;

/// <summary>Pooling changes vector coordinates even when model weights and dimensions stay the same.</summary>
internal static class EmbeddingProfile
{
    public static string NormalizePooling(string pooling) => pooling.Trim().ToLowerInvariant() switch
    {
        "mean" => "mean",
        "cls" => "cls",
        _ => throw new ArgumentException("Embedding pooling must be mean or cls.", nameof(pooling))
    };

    public static string ModelId(string directoryName, string pooling) =>
        NormalizePooling(pooling) == "mean" ? directoryName : directoryName + "/cls-v1";

    public static bool IsCompatible(string? storedModelId, string directoryName, string pooling) =>
        string.IsNullOrEmpty(storedModelId)
            ? NormalizePooling(pooling) == "mean" // legacy indexes used mean and had no metadata
            : storedModelId == ModelId(directoryName, pooling);
}
