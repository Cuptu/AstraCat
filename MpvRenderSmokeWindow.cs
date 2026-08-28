using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AstraCat;

/// <summary>Opt-in end-to-end diagnostic for CI and local Render API regression tests.</summary>
internal sealed class MpvRenderSmokeWindow : Window
{
    private readonly string _mediaPath;
    private readonly string _logPath;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MpvVideoHost _videoHost = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.White, Margin = new Avalonia.Thickness(12) };
    private readonly List<string> _log = [];

    public MpvRenderSmokeWindow(string mediaPath, string logPath, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _mediaPath = mediaPath;
        _logPath = logPath;
        _desktop = desktop;
        Title = "AstraCat libmpv Render API smoke test";
        Width = 960;
        Height = 600;
        Background = Brushes.Black;
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                _videoHost,
                _status
            }
        };
        Grid.SetRow(_status, 1);
        _videoHost.Diagnostic += (_, message) => Write($"host: {message}");
        Opened += RunSmokeTest;
    }

    private async void RunSmokeTest(object? sender, EventArgs e)
    {
        var exitCode = 1;
        try
        {
            Check(File.Exists(_mediaPath), "测试媒体存在");
            await Task.Delay(750);
            for (var cycle = 1; cycle <= 3; cycle++)
            {
                await RunCycleAsync(cycle);
            }
            Write("PASS: 三轮 Render API 播放回归全部通过");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Write($"FAIL: {ex}");
        }
        finally
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_logPath));
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                await File.WriteAllLinesAsync(_logPath, _log);
            }
            finally
            {
                _desktop.Shutdown(exitCode);
            }
        }
    }

    private async Task RunCycleAsync(int cycle)
    {
        Write($"cycle {cycle}: start");
        await using var player = new MpvPlayerService();
        var errors = new List<string>();
        player.Diagnostic += (_, message) => Write($"player: {message}");
        player.PlaybackError += (_, message) =>
        {
            errors.Add(message);
            Write($"mpv: {message}");
        };
        var initialPosition = cycle == 2 ? 2d : (double?)null;
        await player.StartAsync(_videoHost, _mediaPath, startPositionSeconds: initialPosition);
        await Task.Delay(1000);
        Write($"cycle {cycle}: state context={player.HasRenderContext}, duration={player.DurationSeconds:0.###}, video={player.HasVideo}, position={player.PositionSeconds:0.###}");
        await WaitUntilAsync(() => player.DurationSeconds > 1 && player.HasVideo && player.HasRenderContext,
            TimeSpan.FromSeconds(15), "视频属性与 OpenGL Render Context 就绪");
        Check(player.VideoAspect > 0.1, $"cycle {cycle}: 视频宽高比有效 ({player.VideoAspect:0.###})");
        if (initialPosition.HasValue)
            Check(Math.Abs(player.PositionSeconds - initialPosition.Value) < 1.2, $"cycle {cycle}: 初始播放位置生效");

        if (cycle == 1) await player.SetPauseAsync(false);
        else await player.TogglePauseAsync();
        var before = player.PositionSeconds;
        await WaitUntilAsync(() => player.PositionSeconds >= before + 0.8,
            TimeSpan.FromSeconds(8), "播放时间正常推进");
        Write($"cycle {cycle}: playback advanced {before:0.###} -> {player.PositionSeconds:0.###}");

        if (cycle == 1) await player.SetPauseAsync(true);
        else await player.TogglePauseAsync();
        await WaitUntilAsync(() => player.IsPaused, TimeSpan.FromSeconds(3), "暂停状态生效");
        var pausedAt = player.PositionSeconds;
        await Task.Delay(500);
        Check(Math.Abs(player.PositionSeconds - pausedAt) < 0.35, $"cycle {cycle}: 暂停时位置稳定");

        var seekTarget = Math.Min(Math.Max(1, player.DurationSeconds * 0.1), Math.Max(1, player.DurationSeconds - 1));
        await player.SeekAsync(seekTarget);
        await WaitUntilAsync(() => Math.Abs(player.PositionSeconds - seekTarget) < 1.2,
            TimeSpan.FromSeconds(5), "精确跳转生效");
        var relativeTarget = Math.Min(player.DurationSeconds - 0.5, player.PositionSeconds + 0.75);
        await player.SeekRelativeAsync(0.75);
        await WaitUntilAsync(() => Math.Abs(player.PositionSeconds - relativeTarget) < 1.2,
            TimeSpan.FromSeconds(5), "相对跳转生效");

        if (cycle == 1)
        {
            var assPath = Path.Combine(Path.GetTempPath(), $"astracat_render_smoke_{Guid.NewGuid():N}.ass");
            await File.WriteAllTextAsync(assPath, BuildTestAss());
            try
            {
                await player.LoadSubtitleAsync(assPath);
                await player.ReloadSubtitleAsync(assPath);
                await player.ReloadCurrentSubtitleAsync();
                await player.ApplySubtitleStyleAsync("Arial", 64, "#FFFFFF", "#000000", 4);
                await player.ApplySubtitleStyleAsync(SubtitleStyleDefinition.MainDefault());
                await Task.Delay(250);
                Write("cycle 1: ASS 字幕加载与重载命令已提交");
            }
            finally
            {
                try { File.Delete(assPath); } catch { }
            }

            var screenshot = await player.CaptureFrameAsync();
            Check(screenshot is not null && File.Exists(screenshot) && new FileInfo(screenshot).Length > 1024,
                "截图功能输出有效文件");
            if (screenshot is not null)
            {
                Write($"cycle 1: screenshot {new FileInfo(screenshot).Length} bytes");
                try { File.Delete(screenshot); } catch { }
            }
        }

        Check(errors.All(error => !error.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                  && !error.Contains("error", StringComparison.OrdinalIgnoreCase)),
            $"cycle {cycle}: 无 libmpv 致命错误");
        await player.StopAsync();
        Check(!player.IsRunning && !player.HasRenderContext, $"cycle {cycle}: 播放器与渲染上下文完整释放");
        Write($"cycle {cycle}: complete");
    }

    private async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                Write($"OK: {description}");
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"等待超时：{description}");
    }

    private void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Write($"OK: {description}");
    }

    private void Write(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Write(message));
            return;
        }
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        _log.Add(line);
        _status.Text = line;
    }

    private static string BuildTestAss() => """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 1920
        PlayResY: 1080

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,-1,0,0,0,100,100,0,0,1,4,1,2,40,40,60,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.00,9:59:59.00,Default,,0,0,0,,AstraCat Render API subtitle test
        """;
}
