using System.Diagnostics;
using System.Globalization;

namespace AstraCat;

/// <summary>
/// 基于 FFmpeg 视频流分析的场景切换（镜头分割）检测服务。
/// </summary>
internal static class SceneDetectionService
{
    public static async Task<IReadOnlyList<double>> DetectSceneChangesAsync(
        string mediaPath,
        double threshold = 0.4,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return Array.Empty<double>();

        var ffmpeg = MediaToolLocator.FindFfmpeg();
        if (ffmpeg is null) return Array.Empty<double>();

        double totalDuration = 0;
        if (AstraCoreNative.TryProbe(mediaPath, out var probe) && probe.DurationSeconds > 0)
            totalDuration = probe.DurationSeconds;

        var thresholdStr = Math.Clamp(threshold, 0.1, 0.9).ToString("0.##", CultureInfo.InvariantCulture);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // 缩放到 320p 进行极速分析，降低 CPU/GPU 负载并保持数十倍速扫描
        var args = new[]
        {
            "-nostdin", "-hide_banner", "-v", "info", "-i", mediaPath,
            "-filter:v", $"scale=320:-1,select='gt(scene,{thresholdStr})',showinfo",
            "-f", "null", "-"
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        if (process is null) return Array.Empty<double>();

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        var sceneCuts = new List<double>();
        var stderr = process.StandardError;

        try
        {
            while (await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // 解析 showinfo 输出：[Parsed_showinfo_1 ...] n:   1 pts: 120120 pts_time:5.005 ...
                var ptsTimeIdx = line.IndexOf("pts_time:", StringComparison.Ordinal);
                if (ptsTimeIdx >= 0)
                {
                    var start = ptsTimeIdx + "pts_time:".Length;
                    var end = line.IndexOf(' ', start);
                    var numStr = end > start ? line[start..end] : line[start..];
                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    {
                        sceneCuts.Add(seconds);
                        if (totalDuration > 0 && progress is not null)
                            progress.Report(Math.Min(1.0, seconds / totalDuration));
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(1.0);
            return sceneCuts;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch
        {
            return sceneCuts;
        }
    }
}
