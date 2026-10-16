using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

/// <summary>
/// Back-end-native Volcengine v2 one-sentence ASR client.
///
/// This mirrors <c>tools/voice/asr_once.py</c> but runs inside the .NET API
/// process: no short-lived child process, no temp audio file on disk.
/// </summary>
public sealed class VolcengineAsrClient
{
    private const byte ProtocolVersion = 0b0001;
    private const byte HeaderSize = 0b0001;

    private const byte ClientFullRequest = 0b0001;
    private const byte ClientAudioOnlyRequest = 0b0010;
    private const byte ServerFullResponse = 0b1001;
    private const byte ServerAck = 0b1011;
    private const byte ServerErrorResponse = 0b1111;

    private const byte NoSequence = 0b0000;
    private const byte NegativeSequence = 0b0010;

    private const byte NoSerialization = 0b0000;
    private const byte JsonSerialization = 0b0001;

    private const byte NoCompression = 0b0000;
    private const byte GzipCompression = 0b0001;

    private const int SuccessCode = 1000;
    private const string DefaultEndpoint = "wss://openspeech.bytedance.com/api/v2/asr";
    private const string DefaultWorkflow =
        "audio_in,resample,partition,vad,fe,decode,itn,nlu_punctuate";
    private const string DefaultUid = "boardai-android";

    private readonly VoiceOptions _options;
    private readonly ILogger<VolcengineAsrClient> _logger;

    public VolcengineAsrClient(
        IOptions<VoiceOptions> options,
        ILogger<VolcengineAsrClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        DotEnvLoader.EnsureLoaded();
    }

    public async Task<AsrOnceResult> TranscribeAsync(
        byte[] wavBytes,
        WavInfo wavInfo,
        CancellationToken cancellationToken)
    {
        if (wavInfo.Channels != 1 ||
            wavInfo.BitsPerSample != 16 ||
            wavInfo.SampleRate != 16_000)
        {
            throw new VoiceServiceException(
                $"ASR 输入必须是 16k/16bit/单声道 WAV；当前为 " +
                $"{wavInfo.SampleRate}Hz/{wavInfo.BitsPerSample}bit/{wavInfo.Channels}ch",
                400);
        }

        var credentials = ResolveCredentials();
        var endpoint = GetEnvironmentValueOrDefault("VOLCENGINE_ASR_ENDPOINT", DefaultEndpoint);
        var uid = GetEnvironmentValueOrDefault("VOLCENGINE_ASR_UID", DefaultUid);
        var workflow = GetEnvironmentValueOrDefault("VOLCENGINE_ASR_WORKFLOW", DefaultWorkflow);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.AsrTimeoutSeconds)));
        var timeoutToken = overallCts.Token;

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        foreach (var header in credentials.Headers)
        {
            socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        try
        {
            await socket.ConnectAsync(new Uri(endpoint), timeoutToken);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            throw CreateTimeoutException();
        }
        catch (Exception ex) when (ex is not VoiceServiceException)
        {
            throw new VoiceServiceException($"ASR WebSocket 建连失败：{ex.Message}", 502, ex);
        }

        try
        {
            var requestId = Guid.NewGuid().ToString();
            var fullRequest = BuildFullRequest(
                credentials.App,
                uid,
                workflow,
                wavInfo,
                requestId);
            await SendBinaryAsync(socket, fullRequest, timeoutToken);

            var fullResponse = await ReceiveMessageAsync(socket, 30, timeoutToken);
            var fullPayload = EnsureSuccess(ParseServerResponse(fullResponse));
            var text = CollectText(fullPayload);
            var responseRequestId = ReadString(fullPayload, "reqid");
            if (!string.IsNullOrWhiteSpace(responseRequestId))
            {
                requestId = responseRequestId.Trim();
            }

            var segmentSize = Math.Max(
                3_200,
                Math.Min(wavInfo.ByteRate * 10, 1_000_000));
            var offset = 0;
            while (offset < wavBytes.Length)
            {
                var length = Math.Min(segmentSize, wavBytes.Length - offset);
                var isLast = offset + length >= wavBytes.Length;
                var chunk = new byte[length];
                Buffer.BlockCopy(wavBytes, offset, chunk, 0, length);
                offset += length;

                var audioRequest = BuildAudioRequest(chunk, isLast);
                await SendBinaryAsync(socket, audioRequest, timeoutToken);

                var audioResponse = await ReceiveMessageAsync(socket, 60, timeoutToken);
                var audioPayload = EnsureSuccess(ParseServerResponse(audioResponse));
                var chunkText = CollectText(audioPayload);
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    text = chunkText;
                }

                var chunkRequestId = ReadString(audioPayload, "reqid");
                if (!string.IsNullOrWhiteSpace(chunkRequestId))
                {
                    requestId = chunkRequestId.Trim();
                }
            }

            var finalText = text.Trim();
            if (finalText.Length == 0)
            {
                throw new VoiceServiceException("ASR 未识别出文本", 422);
            }

            _logger.LogInformation(
                "Native ASR recognized {CharCount} chars, request_id={RequestId}",
                finalText.Length,
                requestId);

            return new AsrOnceResult(finalText, requestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException();
        }
        catch (VoiceServiceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VoiceServiceException($"ASR 失败：{ex.Message}", 502, ex);
        }
        finally
        {
            await CloseQuietlyAsync(socket);
        }
    }

    private VoiceServiceException CreateTimeoutException()
        => new($"ASR 超时（超过 {Math.Max(5, _options.AsrTimeoutSeconds)} 秒）", 504);

    private static async Task SendBinaryAsync(
        ClientWebSocket socket,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await socket.SendAsync(
            new ReadOnlyMemory<byte>(payload),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<byte[]> ReceiveMessageAsync(
        ClientWebSocket socket,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var bufferStream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                timeoutCts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new VoiceServiceException(
                    "ASR WebSocket 被服务端关闭，请检查凭证、endpoint 与一句话识别权限",
                    502);
            }

            bufferStream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return bufferStream.ToArray();
    }

    private static byte[] BuildFullRequest(
        IReadOnlyDictionary<string, string> app,
        string uid,
        string workflow,
        WavInfo wavInfo,
        string requestId)
    {
        var request = new Dictionary<string, object?>
        {
            ["app"] = app,
            ["user"] = new Dictionary<string, object?>
            {
                ["uid"] = uid
            },
            ["audio"] = new Dictionary<string, object?>
            {
                ["format"] = "wav",
                ["codec"] = "raw",
                ["rate"] = wavInfo.SampleRate,
                ["bits"] = wavInfo.BitsPerSample,
                ["channel"] = wavInfo.Channels,
                ["language"] = "zh-CN"
            },
            ["request"] = new Dictionary<string, object?>
            {
                ["reqid"] = requestId,
                ["nbest"] = 1,
                ["workflow"] = workflow,
                ["show_utterances"] = true,
                ["result_type"] = "full",
                ["sequence"] = 1
            }
        };

        var json = JsonSerializer.Serialize(request);
        var compressed = GZipCompress(Encoding.UTF8.GetBytes(json));

        using var output = new MemoryStream();
        output.Write(BuildHeader(ClientFullRequest, NoSequence, JsonSerialization, GzipCompression));
        output.Write(BigEndianLength(compressed.Length));
        output.Write(compressed);
        return output.ToArray();
    }

    private static byte[] BuildAudioRequest(byte[] chunk, bool isLast)
    {
        var compressed = GZipCompress(chunk);
        var flags = isLast ? NegativeSequence : NoSequence;

        using var output = new MemoryStream();
        output.Write(BuildHeader(
            ClientAudioOnlyRequest,
            flags,
            NoSerialization,
            GzipCompression));
        output.Write(BigEndianLength(compressed.Length));
        output.Write(compressed);
        return output.ToArray();
    }

    private static byte[] BuildHeader(
        byte messageType,
        byte flags,
        byte serialization,
        byte compression)
    {
        return new[]
        {
            (byte)((ProtocolVersion << 4) | HeaderSize),
            (byte)((messageType << 4) | flags),
            (byte)((serialization << 4) | compression),
            (byte)0x00
        };
    }

    private static byte[] BigEndianLength(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        return bytes;
    }

    private static byte[] GZipCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }

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

    private static JsonElement? ParseServerResponse(byte[] data)
    {
        if (data.Length < 4)
        {
            throw new VoiceServiceException($"ASR 响应过短：{data.Length} bytes", 502);
        }

        var headerSize = (data[0] & 0x0F) * 4;
        var messageType = data[1] >> 4;
        var compression = data[2] & 0x0F;
        if (headerSize < 4 || headerSize > data.Length)
        {
            throw new VoiceServiceException("ASR 响应头无效", 502);
        }

        ReadOnlySpan<byte> payload = data.AsSpan(headerSize);
        int? serverErrorCode = null;

        switch (messageType)
        {
            case ServerFullResponse:
                if (payload.Length < 4)
                    throw new VoiceServiceException("ASR full response 缺少 payload size", 502);
                payload = payload[4..];
                break;
            case ServerAck:
                if (payload.Length < 8)
                    throw new VoiceServiceException("ASR ACK 缺少 payload size", 502);
                payload = payload[8..];
                break;
            case ServerErrorResponse:
                if (payload.Length < 8)
                    throw new VoiceServiceException("ASR error response 缺少 payload", 502);
                serverErrorCode = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
                payload = payload[8..];
                break;
            default:
                throw new VoiceServiceException($"未知 ASR 消息类型：{messageType}", 502);
        }

        JsonElement? payloadMessage = null;
        if (payload.Length > 0)
        {
            if (compression == GzipCompression)
            {
                payload = GZipDecompress(payload);
            }
            else if (compression != NoCompression)
            {
                throw new VoiceServiceException($"未知 ASR 压缩类型：{compression}", 502);
            }

            if (payload.Length > 0)
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                payloadMessage = document.RootElement.Clone();
            }
        }

        if (serverErrorCode is not null)
        {
            throw new VoiceServiceException(
                $"ASR 服务返回错误 {serverErrorCode}: {ReadString(payloadMessage, "message") ?? "unknown error"}",
                502);
        }

        return payloadMessage;
    }

    private static JsonElement? EnsureSuccess(JsonElement? payload)
    {
        if (payload is not JsonElement element)
        {
            return null;
        }

        var code = SuccessCode;
        if (element.TryGetProperty("code", out var codeElement))
        {
            if (codeElement.ValueKind == JsonValueKind.Number)
            {
                code = codeElement.GetInt32();
            }
            else if (codeElement.ValueKind == JsonValueKind.String &&
                     int.TryParse(codeElement.GetString(), out var parsedCode))
            {
                code = parsedCode;
            }
        }

        if (code != SuccessCode)
        {
            var message = TryReadString(element, "message");
            throw new VoiceServiceException(
                $"ASR 服务返回错误 {code}: {message ?? "unknown error"}",
                502);
        }

        return element;
    }

    private static string CollectText(JsonElement? payload)
    {
        if (payload is not JsonElement element ||
            !element.TryGetProperty("result", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var lastText = string.Empty;
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("text", out var textElement)) continue;

            var text = textElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                lastText = text;
            }
        }

        return lastText;
    }

    private static string? ReadString(JsonElement? payload, string propertyName)
    {
        if (payload is not JsonElement element)
        {
            return null;
        }

        return TryReadString(element, propertyName);
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var elementValue) ||
            elementValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return elementValue.GetString();
    }

    private static string GetEnvironmentValueOrDefault(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string GetFirstEnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return string.Empty;
    }

    private static Credentials ResolveCredentials()
    {
        var apiKey = GetFirstEnvironmentValue("VOLCENGINE_API_KEY");
        var appId = GetFirstEnvironmentValue(
            "VOLCENGINE_ASR_APP_ID",
            "VOLCENGINE_APP_ID");
        var accessToken = GetFirstEnvironmentValue(
            "VOLCENGINE_ASR_ACCESS_TOKEN",
            "VOLCENGINE_ACCESS_TOKEN");
        var cluster = GetFirstEnvironmentValue(
            "VOLCENGINE_ASR_CLUSTER",
            "VOLCENGINE_CLUSTER");
        var resourceId = GetFirstEnvironmentValue("VOLCENGINE_ASR_RESOURCE_ID");
        var authMode = GetFirstEnvironmentValue(
            "VOLCENGINE_ASR_AUTH",
            "DOUBAO_ASR_AUTH").ToLowerInvariant();

        var consoleTokenConfigured =
            !string.IsNullOrWhiteSpace(appId) &&
            !string.IsNullOrWhiteSpace(accessToken) &&
            !string.IsNullOrWhiteSpace(cluster);

        bool useApiKey;
        if (authMode is "legacy" or "token" or "authorization")
        {
            useApiKey = false;
        }
        else if (authMode is "apikey" or "api_key" or "api-key" or "x-api-key")
        {
            useApiKey = true;
        }
        else if (consoleTokenConfigured)
        {
            useApiKey = false;
        }
        else
        {
            useApiKey = !string.IsNullOrWhiteSpace(apiKey);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var app = new Dictionary<string, string>(StringComparer.Ordinal);

        if (useApiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new VoiceServiceException("缺少 VOLCENGINE_API_KEY（API Key 模式）", 503);
            }

            headers["X-Api-Key"] = apiKey;
            if (!string.IsNullOrWhiteSpace(cluster)) app["cluster"] = cluster;
            if (!string.IsNullOrWhiteSpace(appId)) app["appid"] = appId;
            if (!string.IsNullOrWhiteSpace(accessToken)) app["token"] = accessToken;
            if (!string.IsNullOrWhiteSpace(resourceId))
            {
                headers["X-Api-Resource-Id"] = resourceId;
            }
        }
        else
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(appId)) missing.Add("VOLCENGINE_ASR_APP_ID");
            if (string.IsNullOrWhiteSpace(accessToken)) missing.Add("VOLCENGINE_ASR_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(cluster)) missing.Add("VOLCENGINE_ASR_CLUSTER");
            if (missing.Count > 0)
            {
                throw new VoiceServiceException(
                    "缺少 ASR 凭证：" + string.Join(", ", missing),
                    503);
            }

            headers["Authorization"] = $"Bearer; {accessToken}";
            app["appid"] = appId;
            app["token"] = accessToken;
            app["cluster"] = cluster;
        }

        return new Credentials(headers, app);
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
                closeCts.Token);
        }
        catch
        {
            // best effort
        }
        finally
        {
            socket.Dispose();
        }
    }

    private sealed record Credentials(
        IReadOnlyDictionary<string, string> Headers,
        IReadOnlyDictionary<string, string> App);
}
