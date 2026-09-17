using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AstraCat;

public sealed record DownloadRequest(
    string Url,
    string OutputDir,
    bool AudioOnly = false,
    bool WriteSubtitles = true,
    string SubLangs = "zh.*,en.*",
    string? Proxy = null,
    string? CookiesFromBrowser = null);

internal sealed class DownloadWorkerClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _disposed;

    public static bool IsAvailable() =>
        MediaToolLocator.FindDownloadPython() is not null &&
        MediaToolLocator.FindDownloadWorker() is not null;

    private Process EnsureStarted()
    {
        if (_process is not null && !_process.HasExited)
            return _process;

        var python = MediaToolLocator.FindDownloadPython()
            ?? throw new FileNotFoundException("未找到内置或系统的 Python 解释器。");
        var worker = MediaToolLocator.FindDownloadWorker()
            ?? throw new FileNotFoundException("未找到 download_worker.py 脚本。");

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add(worker);

        var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException("启动下载 Worker 进程失败。");

        // 异步丢弃/排空 stderr 防止缓冲区堵塞
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } _)
                {
                    // 仅排空
                }
            }
            catch { }
        });

        _process = proc;
        return proc;
    }

    public async Task<bool> PingAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proc = EnsureStarted();
            var reqId = Guid.NewGuid().ToString("N");
            var req = $"{{\"id\":\"{reqId}\",\"action\":\"ping\"}}";

            await proc.StandardInput.WriteLineAsync(req.AsMemory(), token).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(token).ConfigureAwait(false);

            while (await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idElem) && idElem.GetString() == reqId)
                {
                    return root.TryGetProperty("ok", out var okElem) && okElem.GetBoolean();
                }
            }
            return false;
        }
        catch
        {
            KillProcess();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DownloadInspectResult> InspectAsync(
        string url,
        string? proxy = null,
        string? cookiesFromBrowser = null,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proc = EnsureStarted();
            using var reg = token.Register(KillProcess);

            var reqId = Guid.NewGuid().ToString("N");
            var reqObj = new Dictionary<string, object?>
            {
                ["id"] = reqId,
                ["action"] = "inspect",
                ["url"] = url,
                ["proxy"] = proxy,
                ["cookies_from_browser"] = cookiesFromBrowser
            };
            var reqJson = AotJson.Serialize(reqObj);

            await proc.StandardInput.WriteLineAsync(reqJson.AsMemory(), token).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(token).ConfigureAwait(false);

            while (await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idElem) && idElem.GetString() == reqId)
                {
                    if (root.TryGetProperty("ok", out var okElem) && okElem.GetBoolean())
                    {
                        var resultJson = root.GetProperty("result").GetRawText();
                        return AotJson.Deserialize<DownloadInspectResult>(resultJson)
                            ?? throw new InvalidOperationException("无法解析媒体探测结果。");
                    }
                    var err = root.TryGetProperty("error", out var errElem) ? errElem.GetString() : "嗅探失败";
                    throw new InvalidOperationException(err);
                }
            }
            throw new InvalidOperationException("下载 Worker 异常中断，未返回嗅探数据。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DownloadCompletedResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgressEvent>? progress = null,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var proc = EnsureStarted();
            using var reg = token.Register(KillProcess);

            var ffmpeg = MediaToolLocator.FindFfmpeg();
            var ffmpegDir = ffmpeg is not null ? Path.GetDirectoryName(ffmpeg) : null;

            var reqId = Guid.NewGuid().ToString("N");
            var reqObj = new Dictionary<string, object?>
            {
                ["id"] = reqId,
                ["action"] = "download",
                ["url"] = request.Url,
                ["output_dir"] = request.OutputDir,
                ["audio_only"] = request.AudioOnly,
                ["write_subtitles"] = request.WriteSubtitles,
                ["sub_langs"] = request.SubLangs,
                ["proxy"] = request.Proxy,
                ["cookies_from_browser"] = request.CookiesFromBrowser,
                ["ffmpeg_location"] = ffmpegDir
            };
            var reqJson = AotJson.Serialize(reqObj);

            await proc.StandardInput.WriteLineAsync(reqJson.AsMemory(), token).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(token).ConfigureAwait(false);

            while (await proc.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // 进度事件
                if (root.TryGetProperty("event", out var evElem) && evElem.GetString() == "progress" &&
                    root.TryGetProperty("id", out var evIdElem) && evIdElem.GetString() == reqId)
                {
                    var prog = AotJson.Deserialize<DownloadProgressEvent>(root.GetRawText());
                    if (prog is not null) progress?.Report(prog);
                    continue;
                }

                // 结果事件
                if (root.TryGetProperty("id", out var idElem) && idElem.GetString() == reqId)
                {
                    if (root.TryGetProperty("ok", out var okElem) && okElem.GetBoolean())
                    {
                        var resultJson = root.GetProperty("result").GetRawText();
                        return AotJson.Deserialize<DownloadCompletedResult>(resultJson)
                            ?? throw new InvalidOperationException("无法解析下载完成结果。");
                    }
                    var err = root.TryGetProperty("error", out var errElem) ? errElem.GetString() : "下载失败";
                    throw new InvalidOperationException(err);
                }
            }
            throw new InvalidOperationException("下载 Worker 异常中断，未返回完成信息。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KillProcess();
        _gate.Dispose();
    }
}
