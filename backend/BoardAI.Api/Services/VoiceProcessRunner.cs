using System.Diagnostics;
using System.Text;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

/// <summary>
/// Thin, synchronous-looking v1 bridge that starts a Python script and waits
/// for a JSON line on stdout.  stdout/stderr are drained asynchronously so a
/// chatty Python process cannot deadlock the API.
/// </summary>
public sealed class VoiceProcessRunner
{
    private readonly VoiceOptions _options;
    private readonly ILogger<VoiceProcessRunner> _logger;

    public VoiceProcessRunner(
        IOptions<VoiceOptions> options,
        ILogger<VoiceProcessRunner> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> RunJsonAsync(
        string configuredScript,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        string label,
        CancellationToken cancellationToken)
    {
        var root = BoardPaths.GetBasePath();
        var scriptPath = ResolveScript(root, configuredScript);
        var pythonExe = string.IsNullOrWhiteSpace(_options.PythonExe)
            ? "python"
            : _options.PythonExe.Trim();

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new VoiceServiceException($"无法启动 Python：{pythonExe}", 503);
        }
        catch (VoiceServiceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VoiceServiceException($"无法启动 Python：{ex.Message}", 503, ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* ignored */ }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            throw new VoiceServiceException($"{label} 超时（超过 {timeoutSeconds} 秒）", 504);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            var statusCode = detail.Contains("缺少") ||
                             detail.Contains("未配置") ||
                             detail.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                ? 503
                : 502;
            _logger.LogWarning("{Label} Python bridge failed with exit code {ExitCode}: {Detail}",
                label, process.ExitCode, Truncate(detail, 1_000));
            throw new VoiceServiceException($"{label} 失败：{Truncate(detail, 800)}", statusCode);
        }

        if (string.IsNullOrWhiteSpace(stdout))
            throw new VoiceServiceException($"{label} 没有返回 JSON", 502);

        _logger.LogInformation("{Label} Python bridge completed, stdout={StdoutLength} chars",
            label, stdout.Length);
        return stdout;
    }

    private static string ResolveScript(string root, string configuredScript)
    {
        if (string.IsNullOrWhiteSpace(configuredScript))
            throw new VoiceServiceException("Voice 脚本路径未配置", 500);

        var value = configuredScript.Trim();
        var path = Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(root, value));

        if (!File.Exists(path))
            throw new VoiceServiceException($"Voice 脚本不存在：{path}", 500);

        return path;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "...";
}
