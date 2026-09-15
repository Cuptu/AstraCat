using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AstraCat;

/// <summary>Opt-in service-level smoke test for probe, waveform, export, subtitles and cancellation.</summary>
internal sealed class MediaServiceSmokeWindow : Window
{
    private readonly string _inputPath;
    private readonly string _resultPath;
    private readonly string _outputRoot;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    public MediaServiceSmokeWindow(string inputPath, string resultPath, string outputRoot,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        _inputPath = Path.GetFullPath(inputPath);
        _resultPath = Path.GetFullPath(resultPath);
        _outputRoot = Path.GetFullPath(outputRoot);
        _desktop = desktop;
        Title = "AstraCat media service smoke test";
        Width = 480;
        Height = 160;
        Content = new TextBlock { Text = "Testing AstraCore media services…", Margin = new Avalonia.Thickness(16) };
        Opened += RunAsync;
    }

    private async void RunAsync(object? sender, EventArgs e)
    {
        var exitCode = 1;
        object result;
        try
        {
            Directory.CreateDirectory(_outputRoot);
            var service = new MediaExportService();
            var probe = await service.ProbeAsync(_inputPath);
            Check(probe.HasVideo && probe.HasAudio && probe.DurationSeconds > 1, "input probe");

            var waveform = await WaveformService.LoadAsync(_inputPath,
                Path.Combine(_outputRoot, "waveform-cache"), CancellationToken.None);
            Check(waveform.Peaks.Length > 10 && waveform.Peaks.Any(value => value > 0), "waveform");

            var hardware = await MediaExportService.GetHardwareEncodersAsync();
            var subtitlePath = Path.Combine(_outputRoot, "smoke.ass");
            await File.WriteAllTextAsync(subtitlePath, BuildAss());
            var progressFractions = new List<double>();
            var progress = new Progress<MediaExportProgress>(value => progressFractions.Add(value.Fraction));
            var exports = new List<object>();

            await ExportAndVerifyAsync(service, probe, ExportVideoCodec.H264, ExportEncoder.Software,
                "software-h264.mp4", subtitlePath, progress, exports);
            await ExportAndVerifyAsync(service, probe, ExportVideoCodec.Hevc, ExportEncoder.Software,
                "software-hevc.mkv", null, progress, exports);
            await ExportAndVerifyAsync(service, probe, ExportVideoCodec.Av1, ExportEncoder.Software,
                "software-av1.mkv", null, progress, exports);

            if (hardware.NvencH264)
                await ExportAndVerifyAsync(service, probe, ExportVideoCodec.H264, ExportEncoder.NvidiaNvenc,
                    "nvenc-h264.mp4", null, progress, exports);
            if (hardware.NvencHevc)
                await ExportAndVerifyAsync(service, probe, ExportVideoCodec.Hevc, ExportEncoder.NvidiaNvenc,
                    "nvenc-hevc.mp4", null, progress, exports);
            if (hardware.NvencAv1)
                await ExportAndVerifyAsync(service, probe, ExportVideoCodec.Av1, ExportEncoder.NvidiaNvenc,
                    "nvenc-av1.mkv", null, progress, exports);

            var cancelledOutput = Path.Combine(_outputRoot, "cancelled-av1.mkv");
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try
                {
                    await service.ExportAsync(new MediaExportOptions(_inputPath, cancelledOutput,
                            Resolution: ExportResolution.Original, IncludeAudio: false,
                            Format: ExportFormat.Mkv, Encoder: ExportEncoder.Software,
                            VideoCodec: ExportVideoCodec.Av1), probe, null, cancellation.Token);
                    throw new InvalidOperationException("cancelled export unexpectedly completed");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Check(!File.Exists(cancelledOutput), "cancelled output cleanup");
            Check(!Directory.EnumerateFiles(_outputRoot, "*.exporting.*").Any(), "temporary output cleanup");
            Check(progressFractions.Any(value => value >= 1), "export progress completion");

            result = new
            {
                passed = true,
                input = probe,
                waveformPeaks = waveform.Peaks.Length,
                waveformMaximum = waveform.Peaks.Max(),
                hardware,
                exports,
                cancellation = "passed"
            };
            exitCode = 0;
        }
        catch (Exception ex)
        {
            result = new { passed = false, error = ex.ToString() };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_resultPath)!);
        await File.WriteAllTextAsync(_resultPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        _desktop.Shutdown(exitCode);
    }

    private async Task ExportAndVerifyAsync(MediaExportService service, MediaProbeInfo input,
        ExportVideoCodec codec, ExportEncoder encoder, string fileName, string? subtitlePath,
        IProgress<MediaExportProgress> progress, List<object> results)
    {
        var output = Path.Combine(_outputRoot, fileName);
        var format = Path.GetExtension(output).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            ? ExportFormat.Mkv : ExportFormat.Mp4;
        await service.ExportAsync(new MediaExportOptions(_inputPath, output,
            Resolution: ExportResolution.P360, FrameRate: ExportFrameRate.Fps24,
            Quality: ExportQuality.SmallerFile, SubtitlePath: subtitlePath,
            Format: format, Encoder: encoder, VideoCodec: codec,
            AudioBitRateKbps: 96, AudioSampleRate: 44100), input, progress);
        Check(File.Exists(output) && new FileInfo(output).Length > 1024, $"{fileName} output");
        var outputProbe = await service.ProbeAsync(output);
        Check(outputProbe.HasVideo && outputProbe.HasAudio && outputProbe.DurationSeconds > 1,
            $"{fileName} probe");
        results.Add(new
        {
            file = fileName,
            codec = codec.ToString(),
            encoder = encoder.ToString(),
            bytes = new FileInfo(output).Length,
            probe = outputProbe
        });
    }

    private static void Check(bool condition, string operation)
    {
        if (!condition) throw new InvalidOperationException($"Media smoke failed: {operation}");
    }

    private static string BuildAss() => """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 640
        PlayResY: 360

        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
        Style: Default,AstraCatMissingFont,34,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,-1,0,0,0,100,100,0,0,1,3,1,2,20,20,25,1

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:00.00,0:00:10.00,Default,,0,0,0,,AstraCore 字体回退 العربية हिन्दी ไทย 😀
        """;
}
