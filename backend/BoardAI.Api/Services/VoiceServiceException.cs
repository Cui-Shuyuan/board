namespace BoardAI.Api.Services;

/// <summary>
/// Carries a client-visible HTTP status code for failures in the voice bridge.
/// </summary>
public class VoiceServiceException : Exception
{
    public int StatusCode { get; }

    public VoiceServiceException(string message, int statusCode = 502, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
