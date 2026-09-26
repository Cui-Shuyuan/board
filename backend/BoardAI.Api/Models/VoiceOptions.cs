namespace BoardAI.Api.Models;

/// <summary>
/// Configuration for the v1 Python bridge that talks to Volcengine ASR/TTS.
/// Relative script paths are resolved against the repository root by
/// BoardPaths so the API can run from bin/ without machine-specific paths.
/// </summary>
public class VoiceOptions
{
    public string PythonExe { get; set; } = "python";
    public string AsrScript { get; set; } = "tools/voice/asr_once.py";
    public string TtsScript { get; set; } = "tools/voice/tts_once.py";
    public int AsrTimeoutSeconds { get; set; } = 75;
    public int TtsTimeoutSeconds { get; set; } = 75;
}
