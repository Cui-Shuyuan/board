namespace BoardAI.Api.Models;

/// <summary>
/// Configuration for Volcengine speech services.
///
/// ASR defaults to the in-process C# client; setting AsrMode to "python"
/// keeps the legacy short-lived Python bridge fallback. TTS still uses the
/// Python bridge. Relative script paths are resolved against the repository
/// root by BoardPaths so the API can run from bin/ without machine-specific
/// paths.
/// </summary>
public class VoiceOptions
{
    public string PythonExe { get; set; } = "python";

    /// <summary>
    /// ASR implementation: "native" calls Volcengine directly in the API
    /// process, "python" keeps the legacy Python bridge fallback.
    /// </summary>
    public string AsrMode { get; set; } = "native";

    public string AsrScript { get; set; } = "tools/voice/asr_once.py";
    public string TtsScript { get; set; } = "tools/voice/tts_once.py";
    public int AsrTimeoutSeconds { get; set; } = 75;
    public int TtsTimeoutSeconds { get; set; } = 75;

    /// <summary>Standard small-model default voice used when Android omits voice.</summary>
    public string DefaultTtsVoice { get; set; } = "BV700_streaming";

    /// <summary>Passed to tts_once.py as --provider; standard is the default small model.</summary>
    public string TtsProvider { get; set; } = "standard";
}
