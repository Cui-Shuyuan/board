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
    public const string RuntimeRootEnvironmentVariable = "BOARD_RUNTIME_ROOT";

    private static readonly Lazy<string> ResolvedBasePath = new(FindBasePath);
    private static readonly Lazy<string> ResolvedRuntimeRoot = new(FindRuntimeRoot);

    public static string GetBasePath() => ResolvedBasePath.Value;

    /// <summary>
    /// Machine-local runtime root for build outputs, models, Qdrant data,
    /// cache and logs. It is intentionally outside the source checkout when
    /// BOARD_RUNTIME_ROOT is configured (start.bat sets it to D:\board).
    /// </summary>
    public static string GetRuntimeRoot() => ResolvedRuntimeRoot.Value;

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
    /// Resolves a runtime-owned path. Relative paths are resolved against the
    /// runtime root; empty configuration uses the provided runtime-relative
    /// default. This keeps generated manifests, releases and caches out of the
    /// repository while preserving existing absolute overrides.
    /// </summary>
    public static string ResolveRuntimePath(string? configuredPath, string runtimeRelativeDefault)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(Path.Combine(GetRuntimeRoot(), runtimeRelativeDefault));

        var value = configuredPath.Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(value)
                ? value
                : Path.Combine(GetRuntimeRoot(), value));
    }

    /// <summary>
    /// Resolves Embedding:ModelDir. Empty configuration uses the runtime model
    /// directory; relative overrides are resolved against the runtime root.
    /// The legacy repository path is retained only as a fallback for an
    /// existing checkout while the model is being migrated.
    /// </summary>
    public static string ResolveModelDir(string? configuredModelDir, string? basePath = null)
    {
        var root = string.IsNullOrWhiteSpace(basePath) ? GetBasePath() : basePath!;
        var runtimeModelDir = Path.Combine(
            GetRuntimeRoot(),
            "models",
            "bge-base-zh-v1.5-fp32");
        var legacyModelDir = Path.Combine(
            root,
            "backend",
            "BoardAI.Api",
            "ml_models",
            "bge-base-zh-v1.5-fp32");

        if (string.IsNullOrWhiteSpace(configuredModelDir))
        {
            if (Directory.Exists(runtimeModelDir) || !Directory.Exists(legacyModelDir))
                return Path.GetFullPath(runtimeModelDir);
            return Path.GetFullPath(legacyModelDir);
        }

        var value = configuredModelDir.Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(value)
                ? value
                : Path.Combine(GetRuntimeRoot(), value));
    }

    private static string FindRuntimeRoot()
    {
        var configured = Environment.GetEnvironmentVariable(RuntimeRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        // Safe fallback for direct development runs that do not go through
        // start.bat: keep it ignored and clearly separate from source.
        return Path.GetFullPath(Path.Combine(GetBasePath(), ".runtime"));
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
