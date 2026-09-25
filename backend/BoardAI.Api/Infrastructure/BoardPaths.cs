namespace BoardAI.Api.Infrastructure;

/// <summary>
/// Resolves repository-relative paths without hard-coding a developer machine
/// directory.  Resolution order:
///   1. BOARD_BASE_PATH environment variable;
///   2. nearest ancestor of AppContext.BaseDirectory containing both
///      content/games and backend;
///   3. Directory.GetCurrentDirectory().
/// </summary>
public static class BoardPaths
{
    public const string BasePathEnvironmentVariable = "BOARD_BASE_PATH";

    private static readonly Lazy<string> ResolvedBasePath = new(FindBasePath);

    public static string GetBasePath() => ResolvedBasePath.Value;

    /// <summary>
    /// Resolves an optional config override.  Absolute overrides stay as-is;
    /// relative overrides are resolved against the discovered repository root.
    /// </summary>
    public static string ResolveBasePath(string? configuredBasePath)
    {
        if (string.IsNullOrWhiteSpace(configuredBasePath))
            return GetBasePath();

        var value = configuredBasePath.Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(value)
                ? value
                : Path.Combine(GetBasePath(), value));
    }

    /// <summary>
    /// Resolves Embedding:ModelDir.  Empty configuration uses the standard
    /// repository model directory; relative paths are resolved against the
    /// repository root.
    /// </summary>
    public static string ResolveModelDir(string? configuredModelDir, string? basePath = null)
    {
        var root = string.IsNullOrWhiteSpace(basePath) ? GetBasePath() : basePath!;
        var defaultModelDir = Path.Combine(
            root,
            "backend",
            "BoardAI.Api",
            "ml_models",
            "bge-base-zh-v1.5-fp32");

        if (string.IsNullOrWhiteSpace(configuredModelDir))
            return Path.GetFullPath(defaultModelDir);

        var value = configuredModelDir.Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(value)
                ? value
                : Path.Combine(root, value));
    }

    private static string FindBasePath()
    {
        var configured = Environment.GetEnvironmentVariable(BasePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = directory.FullName;
            if (Directory.Exists(Path.Combine(candidate, "content", "games")) &&
                Directory.Exists(Path.Combine(candidate, "backend")))
            {
                return Path.GetFullPath(candidate);
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }
}
