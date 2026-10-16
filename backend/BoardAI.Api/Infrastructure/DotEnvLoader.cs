namespace BoardAI.Api.Infrastructure;

/// <summary>
/// Minimal repository-root .env loader for back-end-native speech services.
///
/// Existing process environment variables always win, matching the Python
/// bridge's <c>os.environ.setdefault</c> behavior.
/// </summary>
public static class DotEnvLoader
{
    private static readonly object Sync = new();
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;

        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                var path = Path.Combine(BoardPaths.GetBasePath(), ".env");
                if (!File.Exists(path)) return;

                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;

                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim().Trim('"').Trim('\'');
                    if (key.Length == 0) continue;

                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }
            }
            catch
            {
                // Credential resolution reports the useful error later.
            }
        }
    }
}
