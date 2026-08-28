using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace AstraCat;

internal sealed class MpvRenderBenchmarkWindow : Window
{
    private const int SampleSeconds = 20;
    private readonly string _mediaPath;
    private readonly string _resultPath;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MpvVideoHost _host = new();

    public MpvRenderBenchmarkWindow(string mediaPath, string resultPath,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        _mediaPath = mediaPath;
        _resultPath = resultPath;
        _desktop = desktop;
        Title = "AstraCat Render API benchmark";
        Width = 960;
        Height = 600;
        Background = Brushes.Black;
        Content = _host;
        Opened += RunAsync;
    }

    private async void RunAsync(object? sender, EventArgs e)
    {
        var exitCode = 1;
        object result;
        try
        {
            await Task.Delay(500);
            await using var player = new MpvPlayerService();
            await player.StartAsync(_host, _mediaPath);
            await WaitUntilAsync(() => player.HasVideo && player.DurationSeconds > 1 && player.HasRenderContext,
                TimeSpan.FromSeconds(15));
            await player.SetPauseAsync(false);
            await WaitUntilAsync(() => player.PositionSeconds > 0.25, TimeSpan.FromSeconds(5));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var renderBefore = player.RenderCount;
            var renderTicksBefore = player.RenderTicks;
            var updateBefore = player.RenderUpdateCallbackCount;
            var positionBefore = player.PositionNotificationCount;
            var postsBefore = _host.DispatcherPostCount;
            var positionStart = player.PositionSeconds;
            var peakWorkingSet = process.WorkingSet64;
            var avSyncSamples = new List<double>();
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(SampleSeconds))
            {
                await Task.Delay(250);
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                avSyncSamples.Add(Math.Abs(player.AvSyncSeconds * 1000));
            }

            process.Refresh();
            var elapsed = stopwatch.Elapsed.TotalSeconds;
            var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
            var renders = player.RenderCount - renderBefore;
            var renderTicks = player.RenderTicks - renderTicksBefore;
            result = new
            {
                mode = "libmpv-render-api-opengl",
                media = Path.GetFileName(_mediaPath),
                sampleSeconds = elapsed,
                playbackSeconds = player.PositionSeconds - positionStart,
                cpuSeconds,
                cpuPercentOneCore = cpuSeconds / elapsed * 100,
                workingSetMiB = process.WorkingSet64 / 1048576d,
                peakWorkingSetMiB = peakWorkingSet / 1048576d,
                managedAllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576d,
                renderCount = renders,
                renderFps = renders / elapsed,
                averageRenderCallMs = renders == 0 ? 0 : renderTicks * 1000d / Stopwatch.Frequency / renders,
                updateCallbacks = player.RenderUpdateCallbackCount - updateBefore,
                dispatcherPosts = _host.DispatcherPostCount - postsBefore,
                positionNotifications = player.PositionNotificationCount - positionBefore,
                videoAspect = player.VideoAspect,
                hardwareDecoder = player.HardwareDecoder,
                decoderDroppedFrames = player.DecoderDroppedFrames,
                voDroppedFrames = player.VoDroppedFrames,
                avSyncMilliseconds = player.AvSyncSeconds * 1000,
                absoluteAvSyncAverageMilliseconds = avSyncSamples.Count == 0 ? 0 : avSyncSamples.Average(),
                absoluteAvSyncP95Milliseconds = Percentile(avSyncSamples, 0.95),
                absoluteAvSyncMaxMilliseconds = avSyncSamples.Count == 0 ? 0 : avSyncSamples.Max()
            };
            await player.StopAsync();
            exitCode = 0;
        }
        catch (Exception ex)
        {
            result = new { mode = "libmpv-render-api-opengl", error = ex.ToString() };
        }

        var fullPath = Path.GetFullPath(_resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        _desktop.Shutdown(exitCode);
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout) throw new TimeoutException("等待播放器状态超时。");
            await Task.Delay(50);
        }
    }
}
