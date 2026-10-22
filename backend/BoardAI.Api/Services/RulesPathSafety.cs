namespace BoardAI.Api.Services;

internal static class RulesPathSafety
{
    public const int MaxGameIdLength = 128;

    /// <summary>
    /// Game IDs become path components for rule files. Keep them to a strict
    /// portable set so no game name can traverse outside the content tree.
    /// Cache files additionally use a content hash, not the game ID, as filename.
    /// </summary>
    public static void ValidateGameId(string game)
    {
        if (string.IsNullOrWhiteSpace(game))
            throw new ArgumentException("Game id is required.", nameof(game));
        if (game.Length > MaxGameIdLength || game is "." or "..")
            throw new ArgumentException("Game id is not safe for a path component.", nameof(game));

        foreach (var c in game)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                throw new ArgumentException("Game id contains unsupported path characters.", nameof(game));
        }
    }

    public static string ResolveWithinRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullCandidate.StartsWith(rootPrefix, StringComparison.Ordinal)
            && !string.Equals(fullCandidate, fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved cache path escaped the configured cache directory.");
        }

        return fullCandidate;
    }
}
