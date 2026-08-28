using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace AstraCat;

/// <summary>Same-process, same-libmpv WID baseline used only by the benchmark CLI.</summary>
internal sealed class MpvWidBenchmarkWindow : Window
{
    private readonly string _mediaPath;
    private readonly string _resultPath;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly WidHost _host = new();
    private MpvNative? _native;
    private IntPtr _mpv;

    public MpvWidBenchmarkWindow(string mediaPath, string resultPath, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _mediaPath = mediaPath;
        _resultPath = resultPath;
        _desktop = desktop;
        Title = "AstraCat WID benchmark";
        Width = 960;
        Height = 600;
        Content = _host;
        _host.Created += RunAsync;
    }

    private async void RunAsync(object? sender, IntPtr hwnd)
    {
        var exitCode = 1;
        object result;
        try
        {
            var library = MediaToolLocator.FindLibMpv() ?? throw new FileNotFoundException("libmpv not found");
            _native = MpvNative.GetShared(library);
            _mpv = _native.Create();
            if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");
            Set("config", "no");
            Set("terminal", "no");
            Set("wid", hwnd.ToInt64().ToString());
            Set("hwdec", "auto-safe");
            Set("keep-open", "yes");
            Set("pause", "no");
            Set("input-default-bindings", "no");
            Set("osc", "no");
            Set("osd-bar", "no");
            Set("audio-display", "no");
            Check(_native.Initialize(_mpv), "initialize");
            Check(_native.Command(_mpv, "loadfile", _mediaPath, "replace"), "loadfile");
            await Task.Delay(2500);
            var positionStart = _native.GetPropertyDouble(_mpv, "time-pos");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var peakWorkingSet = process.WorkingSet64;
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(250);
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            }
            process.Refresh();
            var elapsed = timer.Elapsed.TotalSeconds;
            var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
            var positionEnd = _native.GetPropertyDouble(_mpv, "time-pos");
            if (positionEnd <= positionStart)
                throw new InvalidOperationException("WID 基准期间播放位置没有前进。");
            result = new
            {
                mode = "libmpv-wid-same-process-native-vo",
                media = Path.GetFileName(_mediaPath),
                sampleSeconds = elapsed,
                playbackSeconds = positionEnd - positionStart,
                cpuSeconds,
                cpuPercentOneCore = cpuSeconds / elapsed * 100,
                workingSetMiB = process.WorkingSet64 / 1048576d,
                peakWorkingSetMiB = peakWorkingSet / 1048576d,
                managedAllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576d
            };
            exitCode = 0;
        }
        catch (Exception ex) { result = new { mode = "libmpv-wid-same-process-native-vo", error = ex.ToString() }; }
        finally
        {
            if (_mpv != IntPtr.Zero) _native?.TerminateDestroy(_mpv);
            _mpv = IntPtr.Zero;
        }

        var path = Path.GetFullPath(_resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        _desktop.Shutdown(exitCode);
    }

    private void Set(string name, string value) => Check(_native!.SetOptionString(_mpv, name, value), name);
    private void Check(int code, string operation)
    {
        if (code < 0) throw new InvalidOperationException($"{operation}: {_native!.Error(code)}");
    }

    private sealed class WidHost : NativeControlHost
    {
        public event EventHandler<IntPtr>? Created;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var hwnd = CreateWindowEx(0, "STATIC", string.Empty, 0x50000000,
                0, 0, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height),
                parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Created?.Invoke(this, hwnd);
            return new PlatformHandle(hwnd, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (control.Handle != IntPtr.Zero) DestroyWindow(control.Handle);
            base.DestroyNativeControlCore(control);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
