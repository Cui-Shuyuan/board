using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

/// <summary>
/// Back-end-native Volcengine/Doubao one-shot TTS client.
///
/// The Q&amp;A endpoint used to start tools/voice/tts_once.py for every answer.
/// This client keeps the same one-shot contract (complete MP3 bytes in memory)
/// while implementing the two providers that bridge supported:
///
/// * standard - v1 small-model WebSocket binary protocol, MP3;
/// * seed2    - v3 bidirectional WebSocket protocol, MP3.
///
/// Each Q&amp;A synthesis gets its own short-lived WebSocket connection because
/// both service protocols are session/connection scoped. Connections are not
/// pooled across requests.
/// </summary>
public sealed class VolcengineTtsClient
{
    private const string DefaultProvider = "standard";
    private const string ProviderStandard = "standard";
    private const string ProviderSeed2 = "seed2";

    private const string StandardDefaultEndpoint = "wss://openspeech.bytedance.com/api/v1/tts/ws_binary";
    private const string StandardDefaultVoice = "BV700_streaming";
    private const string StandardDefaultCluster = "volcano_tts";
    private const int DefaultSampleRate = 24_000;

    private const string Seed2DefaultEndpoint = "wss://openspeech.bytedance.com/api/v3/tts/bidirection";
    private const string Seed2DefaultResourceId = "seed-tts-2.0";
    private const int Seed2BitRate = 160_000;

    private const byte V1HeaderSize = 0x01;
    private const byte V1ProtocolVersion = 0x01;
    private const byte V1FullClientRequest = 0x01;
    private const byte V1FullServerResponse = 0x09;
    private const byte V1AudioOnlyServer = 0x0B;
    private const byte V1FrontEndResultServer = 0x0C;
    private const byte V1Error = 0x0F;
    private const byte V1NoFlag = 0x00;
    private const byte V1JsonSerialization = 0x01;
    private const byte V1NoCompression = 0x00;
    private const byte V1GzipCompression = 0x01;

    private const byte V3FullClientRequest = 0x01;
    private const byte V3FullServerResponse = 0x09;
    private const byte V3AudioOnlyServer = 0x0B;
    private const byte V3FrontEndResultServer = 0x0C;
    private const byte V3Error = 0x0F;
    private const byte V3PositiveSequence = 0x01;
    private const byte V3NegativeSequence = 0x03;
    private const byte V3WithEvent = 0x04;
    private const int V3EventStartConnection = 1;
    private const int V3EventFinishConnection = 2;
    private const int V3EventConnectionStarted = 50;
    private const int V3EventConnectionFailed = 51;
    private const int V3EventConnectionFinished = 52;
    private const int V3EventStartSession = 100;
    private const int V3EventFinishSession = 102;
    private const int V3EventSessionStarted = 150;
    private const int V3EventSessionFinished = 152;
    private const int V3EventSessionFailed = 153;
    private const int V3EventTaskRequest = 200;

    private static readonly Regex StandardVoicePattern =
        new(@"^(?:BV|BR)\d+.*_streaming$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly VoiceOptions _options;
    private readonly ILogger<VolcengineTtsClient> _logger;

    public VolcengineTtsClient(
        IOptions<VoiceOptions> options,
        ILogger<VolcengineTtsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        DotEnvLoader.EnsureLoaded();
    }

    public async Task<byte[]> SynthesizeAsync(
        string text,
        string voice,
        double speed,
        CancellationToken cancellationToken)
    {
        var provider = ResolveProvider();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.TtsTimeoutSeconds)));
        var timeoutToken = timeoutCts.Token;
        var started = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var audio = provider switch
            {
                ProviderStandard => await SynthesizeStandardAsync(text, voice, speed, timeoutToken).ConfigureAwait(false),
                ProviderSeed2 => await SynthesizeSeed2Async(text, voice, speed, timeoutToken).ConfigureAwait(false),
                _ => throw new VoiceServiceException(
                    $"不支持的 TTS provider：{provider}（可选 standard/seed2）", 500)
            };

            _logger.LogInformation(
                "Native TTS completed: provider={Provider}, chars={Chars}, audio_bytes={AudioBytes}, elapsed_ms={ElapsedMs:F1}",
                provider,
                text.Length,
                audio.Length,
                started.Elapsed.TotalMilliseconds);
            return audio;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceServiceException(
                $"TTS 超时（超过 {Math.Max(5, _options.TtsTimeoutSeconds)} 秒）", 504);
        }
    }

    private string ResolveProvider()
    {
        var configured = string.IsNullOrWhiteSpace(_options.TtsProvider)
            ? GetEnvironmentValueOrDefault("DOUBAO_TTS_PROVIDER", DefaultProvider)
            : _options.TtsProvider.Trim();
        return configured.ToLowerInvariant();
    }

    private async Task<byte[]> SynthesizeStandardAsync(
        string text,
        string requestedVoice,
        double speed,
        CancellationToken cancellationToken)
    {
        var auth = ResolveStandardAuthentication();
        var endpoint = new Uri(GetEnvironmentValueOrDefault("DOUBAO_TTS_ENDPOINT", StandardDefaultEndpoint));
        var voice = ResolveStandardVoice(requestedVoice);
        var sampleRate = ResolveStandardSampleRate();
        var chunks = SplitTextChunks(text);
        var combined = new MemoryStream();

        if (chunks.Count > 1)
        {
            _logger.LogInformation(
                "Native standard TTS split long text into {ChunkCount} protocol chunks", chunks.Count);
        }

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = BuildStandardRequestFrame(chunk, voice, speed, sampleRate, auth);
            var audio = await SynthesizeStandardChunkAsync(endpoint, auth.Headers, frame, cancellationToken)
                .ConfigureAwait(false);
            combined.Write(audio, 0, audio.Length);
        }

        if (combined.Length == 0)
            throw new VoiceServiceException("标准语音合成没有返回音频数据", 502);
        return combined.ToArray();
    }

    private async Task<byte[]> SynthesizeStandardChunkAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        byte[] requestFrame,
        CancellationToken cancellationToken)
    {
        using var socket = await ConnectAsync(endpoint, headers, cancellationToken).ConfigureAwait(false);
        var audio = new MemoryStream();
        try
        {
            await SendBinaryAsync(socket, requestFrame, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var data = await ReceiveBinaryMessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (data.Length < 4)
                    throw new VoiceServiceException($"标准语音合成响应过短：{data.Length} bytes", 502);

                var headerSize = (data[0] & 0x0F) * 4;
                if (headerSize < 4 || headerSize > data.Length)
                    throw new VoiceServiceException("标准语音合成响应 header 无效", 502);

                var messageType = (byte)(data[1] >> 4);
                var flags = (byte)(data[1] & 0x0F);
                var compression = (byte)(data[2] & 0x0F);
                ReadOnlySpan<byte> payload = data.AsSpan(headerSize);

                if (messageType == V1AudioOnlyServer)
                {
                    if (flags == V1NoFlag)
                        continue; // Empty ACK.

                    if (payload.Length < 8)
                        throw new VoiceServiceException("标准语音合成音频包过短", 502);

                    var sequence = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
                    var payloadSize = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);
                    if (payloadSize < 0 || payloadSize > payload.Length - 8)
                        throw new VoiceServiceException("标准语音合成音频包长度不完整", 502);

                    var chunk = payload.Slice(8, payloadSize).ToArray();
                    if (compression == V1GzipCompression)
                        chunk = GZipDecompress(chunk);
                    else if (compression != V1NoCompression)
                        throw new VoiceServiceException($"标准语音合成不支持的压缩方式：{compression}", 502);

                    audio.Write(chunk, 0, chunk.Length);
                    if (sequence < 0)
                        break;
                    continue;
                }

                if (messageType == V1Error)
                {
                    var (code, message) = DecodeStandardError(payload, compression);
                    throw new VoiceServiceException(
                        $"标准语音合成服务返回错误 {code}：{Truncate(message, 500)}", 502);
                }

                if (messageType is V1FrontEndResultServer or V1FullServerResponse)
                    continue;

                throw new VoiceServiceException(
                    $"标准语音合成未知响应消息类型：{messageType} (flags={flags})", 502);
            }
        }
        finally
        {
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
        }

        if (audio.Length == 0)
            throw new VoiceServiceException("标准语音合成没有返回音频数据", 502);
        return audio.ToArray();
    }

    private async Task<byte[]> SynthesizeSeed2Async(
        string text,
        string voice,
        double speed,
        CancellationToken cancellationToken)
    {
        var apiKey = GetEnvironmentValue("VOLCENGINE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new VoiceServiceException("缺少 VOLCENGINE_API_KEY（seed2 模式）", 503);

        var endpoint = new Uri(Seed2DefaultEndpoint);
        var resourceId = GetEnvironmentValueOrDefault("DOUBAO_RESOURCE_ID", Seed2DefaultResourceId);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Api-Key"] = apiKey,
            ["X-Api-Resource-Id"] = resourceId,
            ["X-Api-Connect-Id"] = Guid.NewGuid().ToString(),
        };

        using var socket = await ConnectAsync(endpoint, headers, cancellationToken).ConfigureAwait(false);
        var audio = new MemoryStream();
        try
        {
            await SendV3ClientEventAsync(
                socket,
                V3EventStartConnection,
                sessionId: null,
                payload: Encoding.UTF8.GetBytes("{}"),
                cancellationToken).ConfigureAwait(false);
            await WaitForV3EventAsync(socket, V3EventConnectionStarted, cancellationToken).ConfigureAwait(false);

            var sessionId = Guid.NewGuid().ToString();
            var startSessionPayload = BuildSeed2RequestPayload(
                eventId: V3EventStartSession,
                text: null,
                voice: voice,
                speed: speed);
            await SendV3ClientEventAsync(
                socket,
                V3EventStartSession,
                sessionId,
                startSessionPayload,
                cancellationToken).ConfigureAwait(false);
            await WaitForV3EventAsync(socket, V3EventSessionStarted, cancellationToken).ConfigureAwait(false);

            var taskRequestPayload = BuildSeed2RequestPayload(
                eventId: V3EventTaskRequest,
                text: text,
                voice: voice,
                speed: speed);
            await SendV3ClientEventAsync(
                socket,
                V3EventTaskRequest,
                sessionId,
                taskRequestPayload,
                cancellationToken).ConfigureAwait(false);
            await SendV3ClientEventAsync(
                socket,
                V3EventFinishSession,
                sessionId,
                Encoding.UTF8.GetBytes("{}"),
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var message = await ReceiveV3MessageAsync(socket, cancellationToken).ConfigureAwait(false);
                if (message.MessageType == V3Error)
                {
                    throw new VoiceServiceException(
                        $"seed2 语音合成服务返回错误码 {message.ErrorCode?.ToString() ?? "unknown"}", 502);
                }

                if (message.MessageType == V3AudioOnlyServer)
                {
                    if (message.Payload.Length > 0)
                        audio.Write(message.Payload, 0, message.Payload.Length);
                    continue;
                }

                if (message.MessageType == V3FullServerResponse)
                {
                    if (message.Event == V3EventSessionFinished)
                        break;

                    if (message.Event is V3EventConnectionFailed or V3EventSessionFailed)
                    {
                        var detail = ReadV3PayloadText(message);
                        throw new VoiceServiceException(
                            $"seed2 语音合成会话失败：{Truncate(detail, 500)}", 502);
                    }

                    // Session/sentence/usage/subtitle events are not needed by
                    // the Q&A endpoint; audio is collected independently.
                    continue;
                }

                if (message.MessageType == V3FrontEndResultServer)
                    continue;

                throw new VoiceServiceException(
                    $"seed2 语音合成未知响应消息类型：{message.MessageType}", 502);
            }

            // Finish the connection after receiving the full audio. Failing to
            // receive the corresponding ack does not discard already generated
            // audio, matching the existing Python one-shot behavior.
            try
            {
                await SendV3ClientEventAsync(
                    socket,
                    V3EventFinishConnection,
                    sessionId: null,
                    payload: Encoding.UTF8.GetBytes("{}"),
                    cancellationToken).ConfigureAwait(false);
                await WaitForV3EventAsync(socket, V3EventConnectionFinished, cancellationToken).ConfigureAwait(false);
            }
            catch (VoiceServiceException)
            {
                // Some server versions close without sending ConnectionFinished.
            }
        }
        finally
        {
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
        }

        if (audio.Length == 0)
            throw new VoiceServiceException("seed2 语音合成没有返回音频数据", 502);
        return audio.ToArray();
    }

    private async Task WaitForV3EventAsync(
        ClientWebSocket socket,
        int expectedEvent,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveV3MessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message.MessageType == V3Error)
                throw new VoiceServiceException(
                    $"seed2 语音合成服务返回错误码 {message.ErrorCode?.ToString() ?? "unknown"}", 502);

            if (message.MessageType != V3FullServerResponse)
            {
                if (message.MessageType == V3AudioOnlyServer)
                    continue;
                throw new VoiceServiceException(
                    $"seed2 语音合成等待事件 {expectedEvent} 时收到消息类型 {message.MessageType}", 502);
            }

            if (message.Event == expectedEvent)
                return;

            if (message.Event is V3EventConnectionFailed or V3EventSessionFailed)
            {
                var detail = ReadV3PayloadText(message);
                throw new VoiceServiceException(
                    $"seed2 语音合成会话失败：{Truncate(detail, 500)}", 502);
            }

            throw new VoiceServiceException(
                $"seed2 语音合成返回意外事件 {message.Event?.ToString() ?? "unknown"}，期望 {expectedEvent}", 502);
        }
    }

    private byte[] BuildSeed2RequestPayload(
        int eventId,
        string? text,
        string voice,
        double speed)
    {
        var additions = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["explicit_language"] = "zh-cn",
            ["disable_markdown_filter"] = true,
            ["enable_subtitle"] = true,
        });

        var requestParams = new Dictionary<string, object?>
        {
            ["speaker"] = voice,
            ["audio_params"] = new Dictionary<string, object?>
            {
                ["format"] = "mp3",
                ["sample_rate"] = DefaultSampleRate,
                ["bit_rate"] = Seed2BitRate,
                ["speech_rate"] = SpeedToSeed2SpeechRate(speed),
                ["loudness_rate"] = 0,
                ["enable_subtitle"] = true,
            },
            ["additions"] = additions,
        };
        if (text != null)
            requestParams["text"] = text;

        var payload = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["uid"] = "board-qa" },
            ["namespace"] = "BidirectionalTTS",
            ["req_params"] = requestParams,
            ["event"] = eventId,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private byte[] BuildStandardRequestFrame(
        string text,
        string voice,
        double speed,
        int sampleRate,
        StandardAuthentication auth)
    {
        var request = new Dictionary<string, object?>
        {
            ["app"] = auth.App,
            ["user"] = new Dictionary<string, object?> { ["uid"] = "board-qa" },
            ["audio"] = new Dictionary<string, object?>
            {
                ["voice_type"] = voice,
                ["encoding"] = "mp3",
                ["rate"] = sampleRate,
                ["speed_ratio"] = speed,
                ["volume_ratio"] = 1.0,
                ["pitch_ratio"] = 1.0,
            },
            ["request"] = new Dictionary<string, object?>
            {
                ["reqid"] = Guid.NewGuid().ToString(),
                ["text"] = text,
                ["text_type"] = "plain",
                ["operation"] = "submit",
                ["silence_duration"] = 125,
            },
        };

        var payload = GZipCompress(JsonSerializer.SerializeToUtf8Bytes(request));
        using var output = new MemoryStream();
        output.WriteByte((byte)((V1ProtocolVersion << 4) | V1HeaderSize));
        output.WriteByte((byte)((V1FullClientRequest << 4) | V1NoFlag));
        output.WriteByte((byte)((V1JsonSerialization << 4) | V1GzipCompression));
        output.WriteByte(0);
        WriteBigEndianInt32(output, payload.Length);
        output.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private StandardAuthentication ResolveStandardAuthentication()
    {
        var cluster = GetEnvironmentValueOrDefault(
            "DOUBAO_TTS_CLUSTER",
            GetEnvironmentValueOrDefault("DOUBAO_CLUSTER", StandardDefaultCluster));
        var preferredMode = GetEnvironmentValue("DOUBAO_TTS_AUTH_MODE").ToLowerInvariant();
        var apiKey = GetEnvironmentValue("VOLCENGINE_API_KEY");
        var appId = GetFirstEnvironmentValue("VOLCENGINE_TTS_APP_ID", "VOLCENGINE_APP_ID");
        var accessToken = GetFirstEnvironmentValue(
            "VOLCENGINE_TTS_ACCESS_TOKEN",
            "VOLCENGINE_ACCESS_TOKEN");

        if (preferredMode == "legacy")
            apiKey = string.Empty;
        else if (preferredMode is "apikey" or "api_key" or "api-key")
            appId = string.Empty;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return new StandardAuthentication(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Api-Key"] = apiKey,
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["token"] = "api-key",
                    ["cluster"] = cluster,
                });
        }

        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(accessToken))
        {
            return new StandardAuthentication(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"Bearer; {accessToken}",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appid"] = appId,
                    ["token"] = accessToken,
                    ["cluster"] = cluster,
                });
        }

        throw new VoiceServiceException(
            "缺少 TTS 凭证：请设置 VOLCENGINE_API_KEY，或控制台 token 认证 " +
            "VOLCENGINE_TTS_APP_ID + VOLCENGINE_TTS_ACCESS_TOKEN", 503);
    }

    private string ResolveStandardVoice(string requestedVoice)
    {
        var requested = (requestedVoice ?? string.Empty).Trim();
        if (StandardVoicePattern.IsMatch(requested))
            return requested;

        var configuredFallback = GetEnvironmentValue("DOUBAO_TTS_VOICE");
        var fallback = StandardVoicePattern.IsMatch(configuredFallback)
            ? configuredFallback
            : StandardDefaultVoice;
        if (requested.Length > 0 && !string.Equals(requested, fallback, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Standard TTS voice '{Voice}' is not a small-model voice; using '{Fallback}'",
                requested,
                fallback);
        }
        return fallback;
    }

    private static int ResolveStandardSampleRate()
    {
        var value = GetEnvironmentValue("DOUBAO_TTS_SAMPLE_RATE");
        return int.TryParse(value, out var sampleRate) && sampleRate > 0
            ? sampleRate
            : DefaultSampleRate;
    }

    private static int SpeedToSeed2SpeechRate(double speed)
        => Math.Clamp((int)Math.Round((speed - 1.0) * 100), -50, 100);

    internal static byte[] BuildV3ClientEvent(
        int eventId,
        string? sessionId,
        byte[] payload)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)((V1ProtocolVersion << 4) | V1HeaderSize));
        output.WriteByte((byte)((V3FullClientRequest << 4) | V3WithEvent));
        output.WriteByte((byte)((V1JsonSerialization << 4) | V1NoCompression));
        output.WriteByte(0);
        WriteBigEndianInt32(output, eventId);

        if (eventId is not (V3EventStartConnection or V3EventFinishConnection))
        {
            var sessionBytes = Encoding.UTF8.GetBytes(sessionId ?? string.Empty);
            WriteBigEndianInt32(output, sessionBytes.Length);
            output.Write(sessionBytes, 0, sessionBytes.Length);
        }

        WriteBigEndianInt32(output, payload.Length);
        output.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    internal static V3Message ParseV3Message(byte[] data)
    {
        if (data.Length < 4)
            throw new VoiceServiceException($"seed2 语音合成响应过短：{data.Length} bytes", 502);

        var headerSize = (data[0] & 0x0F) * 4;
        if (headerSize < 4 || headerSize > data.Length)
            throw new VoiceServiceException("seed2 语音合成响应 header 无效", 502);

        var messageType = (byte)(data[1] >> 4);
        var flags = (byte)(data[1] & 0x0F);
        var compression = (byte)(data[2] & 0x0F);
        var offset = headerSize;
        int? sequence = null;
        uint? errorCode = null;

        if (IsV3SequenceMessageType(messageType) &&
            flags is V3PositiveSequence or V3NegativeSequence)
        {
            EnsureRemaining(data, offset, 4, "sequence");
            sequence = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
        }

        if (messageType == V3Error)
        {
            EnsureRemaining(data, offset, 4, "error code");
            errorCode = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
        }

        int? eventId = null;
        string? sessionId = null;
        if (flags == V3WithEvent)
        {
            EnsureRemaining(data, offset, 4, "event");
            eventId = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;

            if (eventId is not (V3EventStartConnection or V3EventFinishConnection
                or V3EventConnectionStarted or V3EventConnectionFailed or V3EventConnectionFinished))
            {
                EnsureRemaining(data, offset, 4, "session id length");
                var sessionLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (sessionLength < 0 || sessionLength > data.Length - offset)
                    throw new VoiceServiceException("seed2 语音合成 session id 长度无效", 502);
                if (sessionLength > 0)
                {
                    sessionId = Encoding.UTF8.GetString(data, offset, sessionLength);
                    offset += sessionLength;
                }
            }

            if (eventId is V3EventConnectionStarted or V3EventConnectionFailed or V3EventConnectionFinished)
            {
                EnsureRemaining(data, offset, 4, "connection id length");
                var connectionLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (connectionLength < 0 || connectionLength > data.Length - offset)
                    throw new VoiceServiceException("seed2 语音合成 connection id 长度无效", 502);
                offset += connectionLength;
            }
        }

        EnsureRemaining(data, offset, 4, "payload length");
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (payloadLength < 0 || payloadLength > data.Length - offset)
            throw new VoiceServiceException("seed2 语音合成 payload 长度无效", 502);

        var payload = data.AsSpan(offset, payloadLength).ToArray();
        if (compression == V1GzipCompression)
            payload = GZipDecompress(payload);
        else if (compression != V1NoCompression)
            throw new VoiceServiceException($"seed2 语音合成不支持的压缩方式：{compression}", 502);

        return new V3Message(messageType, flags, sequence, eventId, sessionId, errorCode, payload);
    }

    private static bool IsV3SequenceMessageType(byte messageType)
        => messageType is V3FullClientRequest or V3FullServerResponse
            or V3FrontEndResultServer or 0x02 or V3AudioOnlyServer;

    private async Task SendV3ClientEventAsync(
        ClientWebSocket socket,
        int eventId,
        string? sessionId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var frame = BuildV3ClientEvent(eventId, sessionId, payload);
        await SendBinaryAsync(socket, frame, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<V3Message> ReceiveV3MessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var data = await ReceiveBinaryMessageAsync(socket, cancellationToken).ConfigureAwait(false);
        return ParseV3Message(data);
    }

    private static string ReadV3PayloadText(V3Message message)
    {
        if (message.Payload.Length == 0)
            return "服务端未返回错误详情";
        try
        {
            return Encoding.UTF8.GetString(message.Payload);
        }
        catch
        {
            return "服务端返回了无法解析的错误详情";
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        foreach (var header in headers)
            socket.Options.SetRequestHeader(header.Key, header.Value);

        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new VoiceServiceException($"TTS WebSocket 建连失败：{ex.Message}", 502, ex);
        }
    }

    private static async Task SendBinaryAsync(
        ClientWebSocket socket,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await socket.SendAsync(
            new ReadOnlyMemory<byte>(payload),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReceiveBinaryMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new VoiceServiceException(
                    "TTS WebSocket 被服务端关闭；请检查凭证、endpoint、voice 与协议版本", 502);
            }
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new VoiceServiceException("TTS 收到非二进制 WebSocket 响应", 502);

            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return output.ToArray();
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State != WebSocketState.Open &&
            socket.State != WebSocketState.CloseReceived)
        {
            socket.Dispose();
            return;
        }

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                closeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort cleanup; synthesis result/error is already decided.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static (int Code, string Message) DecodeStandardError(
        ReadOnlySpan<byte> payload,
        byte compression)
    {
        if (payload.Length < 8)
            return (-1, "格式错误的错误响应");

        var code = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
        var messageSize = BinaryPrimitives.ReadInt32BigEndian(payload[4..8]);
        if (messageSize < 0 || messageSize > payload.Length - 8)
            return (code, "错误响应长度不完整");

        var message = payload.Slice(8, messageSize).ToArray();
        if (compression == V1GzipCompression)
        {
            try
            {
                message = GZipDecompress(message);
            }
            catch
            {
                // Keep the raw bytes as a diagnostic fallback.
            }
        }

        return (code, Encoding.UTF8.GetString(message));
    }

    /// <summary>
    /// Mirrors the standard bridge's protocol chunking at sentence/punctuation
    /// boundaries. Q&amp;A text stays capped at 500 chars by TtsService; this
    /// only handles the v1 service's 1024-byte request limit.
    /// </summary>
    internal static List<string> SplitTextChunks(string text, int maxBytes = 900)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return new List<string>();
        if (Utf8Length(text) <= maxBytes)
            return new List<string> { text };

        var chunks = new List<string>();
        var buffer = string.Empty;
        const string boundaries = "。！？；.!?;\n";
        foreach (var ch in text)
        {
            if (Utf8Length(buffer + ch) <= maxBytes)
            {
                buffer += ch;
                continue;
            }

            var cut = -1;
            for (var i = buffer.Length - 1; i >= 0; i--)
            {
                if (boundaries.Contains(buffer[i], StringComparison.Ordinal))
                {
                    cut = i + 1;
                    break;
                }
            }

            if (cut <= 0)
                cut = buffer.Length;

            var head = buffer[..cut].Trim();
            var tail = buffer[cut..].Trim();
            if (head.Length > 0)
                chunks.Add(head);
            buffer = tail + ch;

            while (Utf8Length(buffer) > maxBytes)
            {
                var hardCut = Math.Min(maxBytes, buffer.Length);
                while (hardCut > 1 && Utf8Length(buffer[..hardCut]) > maxBytes)
                    hardCut--;
                var hardHead = buffer[..hardCut].Trim();
                if (hardHead.Length > 0)
                    chunks.Add(hardHead);
                buffer = buffer[hardCut..].Trim();
            }
        }

        if (buffer.Trim().Length > 0)
            chunks.Add(buffer.Trim());
        return chunks;
    }

    private static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);

    private static void EnsureRemaining(byte[] data, int offset, int count, string field)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new VoiceServiceException($"seed2 语音合成响应缺少 {field}", 502);
    }

    private static byte[] GZipCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] GZipDecompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteBigEndianInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string GetEnvironmentValue(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    private static string GetEnvironmentValueOrDefault(string name, string fallback)
    {
        var value = GetEnvironmentValue(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string GetFirstEnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetEnvironmentValue(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return string.Empty;
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";

    private sealed record StandardAuthentication(
        IReadOnlyDictionary<string, string> Headers,
        Dictionary<string, string> App);

    internal sealed record V3Message(
        byte MessageType,
        byte Flags,
        int? Sequence,
        int? Event,
        string? SessionId,
        uint? ErrorCode,
        byte[] Payload);
}
