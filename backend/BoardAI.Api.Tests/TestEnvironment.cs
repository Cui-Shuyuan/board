using System.Reflection;
using System.Runtime.CompilerServices;

namespace BoardAI.Api.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Tests normally run from an external artifacts directory. Set the
    /// repository root from build-time metadata before BoardPaths' lazy cache
    /// is touched, so real-content tests keep reading source rule files.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var repoRoot = typeof(TestEnvironment).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BoardRepoRoot")
            ?.Value;

        if (!string.IsNullOrWhiteSpace(repoRoot) && Directory.Exists(repoRoot))
            Environment.SetEnvironmentVariable("BOARD_BASE_PATH", Path.GetFullPath(repoRoot));
    }
}
