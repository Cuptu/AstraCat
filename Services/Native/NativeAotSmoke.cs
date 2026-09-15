using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AstraCat;

/// <summary>发布产物的离线兼容检查；仅写显式报告路径，不访问用户项目或发送请求。</summary>
internal static class NativeAotSmoke
{
    internal static string? ApplicationRoot { get; private set; }

    internal static int Run(string reportPath, bool verifyWindow = false)
    {
        try
        {
            if (RuntimeFeature.IsDynamicCodeSupported)
                throw new InvalidOperationException("This verification requires a Native AOT executable.");
            ApplicationRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!,
                "smoke-runtime-" + Guid.NewGuid().ToString("N"));
            Program.BuildAvaloniaApp().SetupWithoutStarting();
            MainWindow.VerifyJsonContracts();
            ModelCatalogService.VerifyJsonContract();
            var backup = AotJson.Deserialize<AppBackupPayload>(
                "{\"AppSettings\":{\"Language\":\"zh-CN\"},\"AsrSettings\":{\"model\":\"fixture\"}}")!;
            var restored = AotJson.Deserialize<AppBackupPayload>(AotJson.Serialize(backup))!;
            if (restored.AppSettings?.Language != "zh-CN" ||
                ((JsonElement)restored.AsrSettings!).GetProperty("model").GetString() != "fixture")
                throw new InvalidDataException("Backup roundtrip failed.");
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"runtimeIdentifier\":\"win-x64\",\"components\":{\"libMpv\":\"libmpv-2.dll\",\"ffmpeg\":\"ffmpeg.exe\",\"ffprobe\":\"ffprobe.exe\",\"native\":\"AstraCore.Native.dll\"}}"));
            var manifest = AotJson.Deserialize<AstraCoreManifest>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest?.Components.LibMpv != "libmpv-2.dll" || manifest?.Components.Native != "AstraCore.Native.dll")
                throw new InvalidDataException("Runtime manifest contract failed.");
            if (AstraCoreRuntime.Current is not null)
            {
                if (AstraCoreRuntime.Current.Resolve("libmpv") is null)
                    throw new InvalidDataException("AstraCore libmpv resolution failed.");
                if (AstraCoreRuntime.Current.Resolve("ffmpeg") is null)
                    throw new InvalidDataException("AstraCore ffmpeg resolution failed.");
                if (AstraCoreRuntime.Current.Resolve("ffprobe") is null)
                    throw new InvalidDataException("AstraCore ffprobe resolution failed.");
                if (AstraCoreRuntime.Current.Resolve("native") is null)
                    throw new InvalidDataException("AstraCore native C ABI resolution failed.");

                if (AstraCoreNative.TryProbe("smoke-nonexistent-file.mp4", out _))
                    throw new InvalidDataException("AstraCore native probe unexpectedly succeeded on nonexistent file.");

                if (!AstraCoreNative.TryCheckEncoder("libx264", out var has264) || !has264)
                    throw new InvalidDataException("AstraCore native check encoder failed on libx264.");

                var sampleCandidate = FindSampleCandidate();
                if (sampleCandidate is not null)
                {
                    if (!AstraCoreNative.TryProbe(sampleCandidate, out var sampleProbe) || !sampleProbe.HasVideo || sampleProbe.DurationSeconds <= 0)
                        throw new InvalidDataException("AstraCore native probe failed on smoke sample.");
                    VerifyUnicodeProbe(sampleCandidate, sampleProbe);
                }

                var audioSample = FindAudioSampleCandidate();
                if (audioSample is not null)
                {
                    if (!AstraCoreNative.TryExtractWaveformPeaks(audioSample, 4000, 100, 48000, out var duration, out var peaks) ||
                        duration <= 0 || peaks.Length == 0)
                        throw new InvalidDataException("AstraCore native waveform extraction failed on audio sample.");

                    var tempWav = Path.Combine(Path.GetTempPath(), $"astracat_smoke_wav_{Guid.NewGuid():N}.wav");
                    try
                    {
                        if (!AstraCoreNative.TryExtractAudioWav(audioSample, tempWav, 16000, 1) || !File.Exists(tempWav) || new FileInfo(tempWav).Length < 100)
                            throw new InvalidDataException("AstraCore native audio WAV extraction failed on audio sample.");
                    }
                    finally
                    {
                        try { File.Delete(tempWav); } catch { }
                    }
                }
            }
            var mpvPath = MediaToolLocator.FindLibMpv() ??
                Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "mpv", "libmpv-2.dll");
            var mpv = MpvNative.GetShared(mpvPath);
            var handle = mpv.Create();
            if (handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed.");
            try
            {
                foreach (var option in new[] { ("config", "no"), ("terminal", "no"), ("vo", "null"), ("ao", "null") })
                    if (mpv.SetOptionString(handle, option.Item1, option.Item2) < 0)
                        throw new InvalidOperationException("mpv option failed: " + option.Item1);
                if (mpv.ClientApiVersion == 0 || mpv.Initialize(handle) < 0)
                    throw new InvalidOperationException("mpv native initialization failed.");
            }
            finally { mpv.TerminateDestroy(handle); }
            if (verifyWindow) VerifyWindow(reportPath);
            File.WriteAllText(reportPath, "PASS: Native AOT; project/cue/cache/config/provider/manifest JSON contracts; libmpv native initialization/disposal." +
                (AstraCoreRuntime.Current is not null ? " AstraCore runtime detected and verified." : "") +
                (verifyWindow ? " Main window rendered; style/color controls constructed; window closed." : "") +
                " User project directory was not used.");
            return 0;
        }
        catch (Exception exception)
        {
            File.WriteAllText(reportPath, exception.ToString());
            return 1;
        }
    }

    private static void VerifyWindow(string reportPath)
    {
        var style = new SubtitleStyleEditorWindow();
        style.Close();
        var window = new MainWindow();
        window.VerifyCompiledSubtitleBindings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Exception? failure = null;
        var rendered = false;
        window.Closed += (_, _) => timeout.Cancel();
        window.Show();
        using var timer = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            try
            {
                var size = Avalonia.PixelSize.FromSize(window.Bounds.Size, window.RenderScaling);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size,
                    new Avalonia.Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
                bitmap.Render(window);
                bitmap.Save(reportPath + ".png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                rendered = true;
            }
            catch (Exception exception) { failure = exception; }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(3));
        Avalonia.Threading.Dispatcher.UIThread.MainLoop(timeout.Token);
        if (window.IsVisible) window.Close();
        if (failure is not null) throw failure;
        if (!rendered) throw new TimeoutException("Native AOT window did not render.");
    }

    private static string? FindSampleCandidate()
    {
        var relative = Path.Combine("artifacts", "astracore-probe-smoke.mp4");
        var roots = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, AstraCoreRuntime.Current?.Root };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var current = new DirectoryInfo(root);
            for (var i = 0; i < 5 && current is not null; i++)
            {
                var candidate = Path.Combine(current.FullName, relative);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                candidate = Path.Combine(current.FullName, "astracore-probe-smoke.mp4");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                current = current.Parent;
            }
        }
        return null;
    }

    private static void VerifyUnicodeProbe(string sourceSample, MediaProbeInfo expected)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"astracat_unicode_probe_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var unicodePath = Path.Combine(tempDir, "测试 样本 视频.mp4");
            File.Copy(sourceSample, unicodePath, overwrite: true);
            if (!AstraCoreNative.TryProbe(unicodePath, out var probe) || !probe.HasVideo || probe.Width != expected.Width)
                throw new InvalidDataException("AstraCore native probe failed on unicode path: " + unicodePath);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string? FindAudioSampleCandidate()
    {
        var relative = Path.Combine("artifacts", "media-chain-tests", "h264-aac-3s.mp4");
        var roots = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, AstraCoreRuntime.Current?.Root };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var current = new DirectoryInfo(root);
            for (var i = 0; i < 5 && current is not null; i++)
            {
                var candidate = Path.Combine(current.FullName, relative);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                candidate = Path.Combine(current.FullName, "h264-aac-3s.mp4");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                current = current.Parent;
            }
        }
        return null;
    }
}
