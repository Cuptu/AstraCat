using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AstraCat;

public enum ExportResolution
{
    Original,
    P360,
    P480,
    Hd720,
    FullHd1080,
    Qhd1440,
    Uhd2160,
    Custom
}

public enum ExportFrameRate
{
    Original,
    Fps24,
    Fps25,
    Fps30,
    Fps60
}

public enum ExportQuality
{
    Recommended,
    High,
    SmallerFile
}

public enum ExportFormat
{
    Mp4,
    Mov,
    Mkv
}

public enum ExportEncoder
{
    Auto,
    Software,
    NvidiaNvenc,
    IntelQsv,
    AmdAmf
}

public enum ExportVideoCodec
{
    H264,
    Hevc,
    Av1
}

public enum ExportRateControl
{
    Vbr,
    Cbr
}

public enum ExportSubtitleFormat
{
    Srt,
    Ass,
    Txt
}

/// <summary>Per-codec, per-vendor encoder availability reported by the local FFmpeg build.</summary>
public sealed record HardwareEncoderInfo(
    bool NvencH264, bool NvencHevc, bool NvencAv1,
    bool QsvH264, bool QsvHevc, bool QsvAv1,
    bool AmfH264, bool AmfHevc, bool AmfAv1,
    bool X265, bool SvtAv1)
{
    public static HardwareEncoderInfo None { get; } = new(
        false, false, false, false, false, false, false, false, false, false, false);

    public bool Nvenc => NvencH264 || NvencHevc || NvencAv1;
    public bool Qsv => QsvH264 || QsvHevc || QsvAv1;
    public bool Amf => AmfH264 || AmfHevc || AmfAv1;
    public bool Any => Nvenc || Qsv || Amf;

    public bool SupportsVendor(ExportEncoder vendor, ExportVideoCodec codec) => (vendor, codec) switch
    {
        (ExportEncoder.NvidiaNvenc, ExportVideoCodec.H264) => NvencH264,
        (ExportEncoder.NvidiaNvenc, ExportVideoCodec.Hevc) => NvencHevc,
        (ExportEncoder.NvidiaNvenc, ExportVideoCodec.Av1) => NvencAv1,
        (ExportEncoder.IntelQsv, ExportVideoCodec.H264) => QsvH264,
        (ExportEncoder.IntelQsv, ExportVideoCodec.Hevc) => QsvHevc,
        (ExportEncoder.IntelQsv, ExportVideoCodec.Av1) => QsvAv1,
        (ExportEncoder.AmdAmf, ExportVideoCodec.H264) => AmfH264,
        (ExportEncoder.AmdAmf, ExportVideoCodec.Hevc) => AmfHevc,
        (ExportEncoder.AmdAmf, ExportVideoCodec.Av1) => AmfAv1,
        _ => true
    };

    public bool SupportsSoftware(ExportVideoCodec codec) => codec switch
    {
        ExportVideoCodec.Hevc => X265,
        ExportVideoCodec.Av1 => SvtAv1,
        _ => true
    };

    public ExportEncoder PreferredVendor(ExportVideoCodec codec) =>
        SupportsVendor(ExportEncoder.NvidiaNvenc, codec) ? ExportEncoder.NvidiaNvenc :
        SupportsVendor(ExportEncoder.IntelQsv, codec) ? ExportEncoder.IntelQsv :
        SupportsVendor(ExportEncoder.AmdAmf, codec) ? ExportEncoder.AmdAmf :
        ExportEncoder.Software;
}

public sealed record MediaExportOptions(
    string InputPath,
    string OutputPath,
    ExportResolution Resolution = ExportResolution.Original,
    ExportFrameRate FrameRate = ExportFrameRate.Original,
    ExportQuality Quality = ExportQuality.Recommended,
    bool IncludeVideo = true,
    bool IncludeAudio = true,
    string? SubtitlePath = null,
    ExportFormat Format = ExportFormat.Mp4,
    ExportEncoder Encoder = ExportEncoder.Auto,
    int CustomWidth = 0,
    int CustomHeight = 0,
    double? VideoBitRateMbps = null,
    bool ExportSubtitles = false,
    string? PlainSubtitlePath = null,
    ExportVideoCodec VideoCodec = ExportVideoCodec.H264,
    ExportRateControl RateControl = ExportRateControl.Vbr,
    int AudioBitRateKbps = 192,
    int AudioSampleRate = 0,
    ExportSubtitleFormat SubtitleFormat = ExportSubtitleFormat.Srt);

public sealed record MediaProbeInfo(
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate,
    long BitRate,
    bool HasVideo,
    bool HasAudio)
{
    public static MediaProbeInfo Unknown { get; } = new(0, 0, 0, 0, 0, true, true);
}

public sealed record MediaExportProgress(double Fraction, TimeSpan Processed, TimeSpan Total, string Status);

/// <summary>
/// A small, dependency-free FFmpeg export service used by the workspace export dialog.
/// It deliberately exposes plain records so the UI and future queue can share the same engine.
/// </summary>
public sealed class MediaExportService
{
    private static readonly Lazy<Task<HardwareEncoderInfo>> HardwareEncoders =
        new(DetectHardwareEncodersAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string FormatExtension(ExportFormat format) => format switch
    {
        ExportFormat.Mov => ".mov",
        ExportFormat.Mkv => ".mkv",
        _ => ".mp4"
    };

    /// <summary>Detects GPU encoders by listing the encoders compiled into the local FFmpeg. Cached process-wide.</summary>
    public static Task<HardwareEncoderInfo> GetHardwareEncodersAsync() => HardwareEncoders.Value;

    private static async Task<HardwareEncoderInfo> DetectHardwareEncodersAsync()
    {
        try
        {
            var ffmpeg = MediaToolLocator.FindFfmpeg();
            if (ffmpeg is null) return HardwareEncoderInfo.None;
            var info = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null) return HardwareEncoderInfo.None;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                await WaitForTerminationAsync(process);
                try { await Task.WhenAll(outputTask, errorTask); } catch { }
                return HardwareEncoderInfo.None;
            }
            var output = await outputTask;
            await errorTask;
            if (process.ExitCode != 0 || string.IsNullOrEmpty(output)) return HardwareEncoderInfo.None;
            bool Has(string name) => output.Contains(name, StringComparison.Ordinal);
            return new HardwareEncoderInfo(
                Has("h264_nvenc"), Has("hevc_nvenc"), Has("av1_nvenc"),
                Has("h264_qsv"), Has("hevc_qsv"), Has("av1_qsv"),
                Has("h264_amf"), Has("hevc_amf"), Has("av1_amf"),
                Has("libx265"), Has("libsvtav1") || Has("libsvt_av1"));
        }
        catch
        {
            return HardwareEncoderInfo.None;
        }
    }

    private static ExportEncoder ResolveEncoder(ExportEncoder requested, ExportVideoCodec codec, HardwareEncoderInfo hardware) =>
        requested == ExportEncoder.Auto
            ? hardware.PreferredVendor(codec)
            : hardware.SupportsVendor(requested, codec) ? requested : ExportEncoder.Software;

    private static string EncoderName(ExportVideoCodec codec, ExportEncoder vendor) => (codec, vendor) switch
    {
        (ExportVideoCodec.H264, ExportEncoder.NvidiaNvenc) => "h264_nvenc",
        (ExportVideoCodec.Hevc, ExportEncoder.NvidiaNvenc) => "hevc_nvenc",
        (ExportVideoCodec.Av1, ExportEncoder.NvidiaNvenc) => "av1_nvenc",
        (ExportVideoCodec.H264, ExportEncoder.IntelQsv) => "h264_qsv",
        (ExportVideoCodec.Hevc, ExportEncoder.IntelQsv) => "hevc_qsv",
        (ExportVideoCodec.Av1, ExportEncoder.IntelQsv) => "av1_qsv",
        (ExportVideoCodec.H264, ExportEncoder.AmdAmf) => "h264_amf",
        (ExportVideoCodec.Hevc, ExportEncoder.AmdAmf) => "hevc_amf",
        (ExportVideoCodec.Av1, ExportEncoder.AmdAmf) => "av1_amf",
        (ExportVideoCodec.Hevc, _) => "libx265",
        (ExportVideoCodec.Av1, _) => "libsvtav1",
        _ => "libx264"
    };

    public async Task<MediaProbeInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("媒体文件不存在。", inputPath);
        var executable = MediaToolLocator.FindFfprobe();
        if (executable is null) return MediaProbeInfo.Unknown;

        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-show_entries",
                     "format=duration,bit_rate:stream=codec_type,width,height,r_frame_rate",
                     "-of", "json", inputPath
                 })
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFprobe 启动失败。");
        using var cancellationRegistration = cancellationToken.Register(() => TryTerminate(process));
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        string output;
        string error;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            output = await outputTask;
            error = await errorTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            await WaitForTerminationAsync(process);
            try { await Task.WhenAll(outputTask, errorTask); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "无法读取媒体信息。" : error.Trim());

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var duration = 0d;
        var bitRate = 0L;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var durationElement))
                double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            if (format.TryGetProperty("bit_rate", out var bitRateElement))
                long.TryParse(bitRateElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out bitRate);
        }

        var width = 0;
        var height = 0;
        var frameRate = 0d;
        var hasVideo = false;
        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "audio") hasAudio = true;
                if (type != "video") continue;
                hasVideo = true;
                if (width == 0 && stream.TryGetProperty("width", out var widthElement)) width = widthElement.GetInt32();
                if (height == 0 && stream.TryGetProperty("height", out var heightElement)) height = heightElement.GetInt32();
                if (frameRate == 0 && stream.TryGetProperty("r_frame_rate", out var rateElement))
                    frameRate = ParseFraction(rateElement.GetString());
            }
        }

        return new MediaProbeInfo(duration, width, height, frameRate, bitRate, hasVideo, hasAudio);
    }

    public long EstimateOutputBytes(MediaProbeInfo media, MediaExportOptions options)
    {
        if (media.DurationSeconds <= 0) return 0;
        var bitsPerSecond = 0L;
        if (options.IncludeVideo && media.HasVideo)
        {
            if (options.VideoBitRateMbps is > 0)
            {
                bitsPerSecond += (long)(options.VideoBitRateMbps.Value * 1_000_000);
            }
            else
            {
                var targetHeight = TargetHeight(options, media);
                var baseRate = targetHeight >= 2160 ? 40_000_000L
                    : targetHeight >= 1440 ? 16_000_000L
                    : targetHeight >= 1080 ? 8_000_000L
                    : targetHeight >= 720 ? 4_500_000L
                    : targetHeight >= 480 ? 2_000_000L
                    : 1_000_000L;
                // HEVC/AV1 同等质量所需码率约为 H.264 的 60%/50%
                baseRate = options.VideoCodec switch
                {
                    ExportVideoCodec.Hevc => (long)(baseRate * .6),
                    ExportVideoCodec.Av1 => (long)(baseRate * .5),
                    _ => baseRate
                };
                bitsPerSecond += options.Quality switch
                {
                    ExportQuality.High => (long)(baseRate * 1.45),
                    ExportQuality.SmallerFile => (long)(baseRate * .62),
                    _ => baseRate
                };
            }
        }
        if (options.IncludeAudio && media.HasAudio)
            bitsPerSecond += (options.AudioBitRateKbps > 0 ? options.AudioBitRateKbps : 192) * 1000L;
        return (long)(media.DurationSeconds * bitsPerSecond / 8d);
    }

    private static int TargetHeight(MediaExportOptions options, MediaProbeInfo media) => options.Resolution switch
    {
        ExportResolution.P360 => 360,
        ExportResolution.P480 => 480,
        ExportResolution.Hd720 => 720,
        ExportResolution.FullHd1080 => 1080,
        ExportResolution.Qhd1440 => 1440,
        ExportResolution.Uhd2160 => 2160,
        ExportResolution.Custom when options.CustomHeight > 0 => options.CustomHeight,
        _ => media.Height > 0 ? media.Height : 1080
    };

    public async Task ExportAsync(
        MediaExportOptions options,
        MediaProbeInfo media,
        IProgress<MediaExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hardware = await GetHardwareEncodersAsync();
        var codec = options.VideoCodec;
        var encoder = ResolveEncoder(options.Encoder, codec, hardware);
        if (encoder == ExportEncoder.Software && codec != ExportVideoCodec.H264 && !hardware.SupportsSoftware(codec))
        {
            // 当前 FFmpeg 缺少该编码的软件编码器（如 libx265/libsvtav1），回退到最通用的 H.264
            codec = ExportVideoCodec.H264;
            options = options with { VideoCodec = codec };
            encoder = ResolveEncoder(options.Encoder, codec, hardware);
        }
        try
        {
            await ExportCoreAsync(options, media, encoder, progress, cancellationToken);
        }
        catch (InvalidOperationException) when (encoder != ExportEncoder.Software && !cancellationToken.IsCancellationRequested)
        {
            // 硬件编码器在个别驱动/素材组合下会失败，自动回退到软件编码重试一次
            progress?.Report(new MediaExportProgress(0, TimeSpan.Zero,
                TimeSpan.FromSeconds(Math.Max(0, media.DurationSeconds)), "硬件编码失败，改用软件编码重试"));
            await ExportCoreAsync(options, media, ExportEncoder.Software, progress, cancellationToken);
        }

        if (options.ExportSubtitles)
            await ExportSubtitleFileAsync(options, cancellationToken);
    }

    private async Task ExportCoreAsync(
        MediaExportOptions options,
        MediaProbeInfo media,
        ExportEncoder encoder,
        IProgress<MediaExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.InputPath)) throw new FileNotFoundException("媒体文件不存在。", options.InputPath);
        if (!options.IncludeVideo && !options.IncludeAudio) throw new InvalidOperationException("请至少选择视频或音频中的一项。");
        var ffmpeg = MediaToolLocator.FindFfmpeg() ??
                     throw new FileNotFoundException("未找到 FFmpeg。请将它放入 runtime/tools/ffmpeg，或加入 PATH。");

        var outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory)) outputDirectory = Environment.CurrentDirectory;
        Directory.CreateDirectory(outputDirectory);
        var temporaryOutput = Path.Combine(outputDirectory,
            $".{Path.GetFileNameWithoutExtension(options.OutputPath)}.{Guid.NewGuid():N}.exporting{FormatExtension(options.Format)}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(options, media, temporaryOutput, encoder)) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg 启动失败。");
        var errors = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (errors.Length > 12_000) errors.Remove(0, Math.Min(4_000, errors.Length));
                errors.AppendLine(line);
            }
        }, CancellationToken.None);

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        try
        {
            var processed = TimeSpan.Zero;
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator];
                var value = line[(separator + 1)..];
                if ((key == "out_time_us" || key == "out_time_ms") &&
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                    processed = TimeSpan.FromTicks(microseconds * 10);
                else if (key == "out_time" && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                    processed = parsed;
                else if (key == "progress")
                {
                    var fraction = media.DurationSeconds > 0
                        ? Math.Clamp(processed.TotalSeconds / media.DurationSeconds, 0, 1)
                        : 0;
                    progress?.Report(new MediaExportProgress(
                        value == "end" ? 1 : fraction,
                        processed,
                        TimeSpan.FromSeconds(Math.Max(0, media.DurationSeconds)),
                        value == "end" ? "正在完成文件" : "正在导出"));
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(ReadableFfmpegError(errors.ToString()));

            File.Move(temporaryOutput, options.OutputPath, overwrite: true);
            progress?.Report(new MediaExportProgress(1, TimeSpan.FromSeconds(media.DurationSeconds),
                TimeSpan.FromSeconds(media.DurationSeconds), "导出完成"));
        }
        catch
        {
            TryTerminate(process);
            await WaitForTerminationAsync(process);
            try { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); }
            catch { }
            throw;
        }
    }

    private static IEnumerable<string> BuildArguments(MediaExportOptions options, MediaProbeInfo media, string outputPath, ExportEncoder encoder)
    {
        var arguments = new List<string> { "-hide_banner", "-y", "-i", options.InputPath };

        if (options.IncludeVideo)
        {
            arguments.AddRange(["-map", "0:v:0?"]);
            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.SubtitlePath) && File.Exists(options.SubtitlePath))
                filters.Add($"subtitles=filename='{EscapeFilterPath(options.SubtitlePath)}'");
            if (ScaleFilter(options) is { } scale) filters.Add(scale);
            if (filters.Count > 0) arguments.AddRange(["-vf", string.Join(',', filters)]);
            var frameRate = options.FrameRate switch
            {
                ExportFrameRate.Fps24 => 24,
                ExportFrameRate.Fps25 => 25,
                ExportFrameRate.Fps30 => 30,
                ExportFrameRate.Fps60 => 60,
                _ => 0
            };
            if (frameRate > 0) arguments.AddRange(["-r", frameRate.ToString(CultureInfo.InvariantCulture)]);
            AddVideoEncoderArguments(arguments, options, encoder);
        }
        else
        {
            arguments.Add("-vn");
        }

        if (options.IncludeAudio)
        {
            var audioKbps = options.AudioBitRateKbps > 0 ? options.AudioBitRateKbps : 192;
            arguments.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-b:a", $"{audioKbps}k"]);
            if (options.AudioSampleRate > 0)
                arguments.AddRange(["-ar", options.AudioSampleRate.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            arguments.Add("-an");
        }

        // faststart 仅对 MP4/MOV 容器有效
        if (options.Format != ExportFormat.Mkv) arguments.AddRange(["-movflags", "+faststart"]);
        arguments.AddRange(["-progress", "pipe:1", "-nostats", outputPath]);
        return arguments;
    }

    private static string? ScaleFilter(MediaExportOptions options) => options.Resolution switch
    {
        ExportResolution.P360 => "scale=-2:360",
        ExportResolution.P480 => "scale=-2:480",
        ExportResolution.Hd720 => "scale=-2:720",
        ExportResolution.FullHd1080 => "scale=-2:1080",
        ExportResolution.Qhd1440 => "scale=-2:1440",
        ExportResolution.Uhd2160 => "scale=-2:2160",
        ExportResolution.Custom when options.CustomWidth > 0 && options.CustomHeight > 0 =>
            $"scale={EvenDimension(options.CustomWidth)}:{EvenDimension(options.CustomHeight)}",
        _ => null
    };

    private static int EvenDimension(int value) => Math.Max(2, value - value % 2);

    private static void AddVideoEncoderArguments(List<string> arguments, MediaExportOptions options, ExportEncoder vendor)
    {
        var codec = options.VideoCodec;
        var bitRate = options.VideoBitRateMbps is > 0 and <= 200
            ? (long)Math.Round(options.VideoBitRateMbps.Value * 1_000_000)
            : 0;
        var cbr = options.RateControl == ExportRateControl.Cbr;
        var (high, recommended, smaller) = QualityLadder(codec, vendor == ExportEncoder.Software);
        switch (vendor)
        {
            case ExportEncoder.NvidiaNvenc:
                arguments.AddRange(["-c:v", EncoderName(codec, vendor), "-preset", "p5"]);
                if (bitRate > 0)
                {
                    arguments.AddRange(["-rc", cbr ? "cbr" : "vbr"]);
                    AddBitRateArguments(arguments, bitRate, cbr);
                }
                else
                {
                    arguments.AddRange(["-rc", "vbr", "-cq", QualityValue(options.Quality, high, recommended, smaller)]);
                }
                break;
            case ExportEncoder.IntelQsv:
                arguments.AddRange(["-c:v", EncoderName(codec, vendor), "-preset", "medium"]);
                if (bitRate > 0) AddBitRateArguments(arguments, bitRate, cbr);
                else arguments.AddRange(["-global_quality", QualityValue(options.Quality, high, recommended, smaller)]);
                break;
            case ExportEncoder.AmdAmf:
                arguments.AddRange(["-c:v", EncoderName(codec, vendor), "-quality", "balanced"]);
                if (bitRate > 0)
                {
                    arguments.AddRange(["-rc", cbr ? "cbr" : "vbr_peak"]);
                    AddBitRateArguments(arguments, bitRate, cbr);
                }
                else
                {
                    var qp = QualityValue(options.Quality, high, recommended, smaller);
                    arguments.AddRange(["-rc", "cqp", "-qp_i", qp, "-qp_p", qp]);
                }
                break;
            default:
                switch (codec)
                {
                    case ExportVideoCodec.Hevc:
                        arguments.AddRange(["-c:v", "libx265", "-preset", "medium"]);
                        break;
                    case ExportVideoCodec.Av1:
                        arguments.AddRange(["-c:v", "libsvtav1", "-preset", "6"]);
                        break;
                    default:
                        arguments.AddRange(["-c:v", "libx264", "-preset", "medium"]);
                        break;
                }
                if (bitRate > 0) AddBitRateArguments(arguments, bitRate, cbr);
                else arguments.AddRange(["-crf", QualityValue(options.Quality, high, recommended, smaller)]);
                arguments.AddRange(["-pix_fmt", "yuv420p"]);
                break;
        }
        // Apple 生态识别 MP4/MOV 中的 HEVC 需要 hvc1 标签
        if (codec == ExportVideoCodec.Hevc && options.Format != ExportFormat.Mkv)
            arguments.AddRange(["-tag:v", "hvc1"]);
    }

    /// <summary>各编码在恒定质量模式下的量化参数阶梯（高/推荐/较小文件），AV1 使用 0-63 量程。</summary>
    private static (int High, int Recommended, int Smaller) QualityLadder(ExportVideoCodec codec, bool software) =>
        codec switch
        {
            ExportVideoCodec.Hevc => software ? (18, 22, 26) : (20, 24, 29),
            ExportVideoCodec.Av1 => software ? (26, 32, 40) : (24, 28, 34),
            _ => software ? (17, 20, 24) : (18, 22, 27)
        };

    private static string QualityValue(ExportQuality quality, int high, int recommended, int smaller) =>
        (quality switch
        {
            ExportQuality.High => high,
            ExportQuality.SmallerFile => smaller,
            _ => recommended
        }).ToString(CultureInfo.InvariantCulture);

    private static void AddBitRateArguments(List<string> arguments, long bitsPerSecond, bool cbr)
    {
        arguments.AddRange([
            "-b:v", bitsPerSecond.ToString(CultureInfo.InvariantCulture),
            "-maxrate", (cbr ? bitsPerSecond : bitsPerSecond * 3 / 2).ToString(CultureInfo.InvariantCulture),
            "-bufsize", (cbr ? bitsPerSecond : bitsPerSecond * 2).ToString(CultureInfo.InvariantCulture)
        ]);
    }

    /// <summary>Best-effort sidecar subtitle export (SRT/ASS/TXT), written next to the exported video.</summary>
    private static async Task ExportSubtitleFileAsync(MediaExportOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var extension = options.SubtitleFormat switch
            {
                ExportSubtitleFormat.Ass => ".ass",
                ExportSubtitleFormat.Txt => ".txt",
                _ => ".srt"
            };
            var target = Path.ChangeExtension(options.OutputPath, extension);
            if (string.IsNullOrWhiteSpace(target)) return;

            if (options.SubtitleFormat == ExportSubtitleFormat.Txt)
            {
                WriteTranscript(options, target);
                return;
            }

            var sources = new[] { options.PlainSubtitlePath, options.SubtitlePath };
            var exact = sources.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                File.Copy(exact, target, overwrite: true);
                return;
            }

            var source = sources.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (source is null) return;
            var ffmpeg = MediaToolLocator.FindFfmpeg();
            if (ffmpeg is null) return;
            // 经 FFmpeg 在 SRT/ASS 等字幕格式之间转换
            var info = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-y", "-i", source, target })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return;
            using var cancellationRegistration = cancellationToken.Register(() => TryTerminate(process));
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await errorTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                await WaitForTerminationAsync(process);
                try { await errorTask; } catch { }
                throw;
            }
            if (process.ExitCode != 0)
            {
                try { if (File.Exists(target)) File.Delete(target); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 字幕文件导出是附加能力，失败不影响已完成的视频导出
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the state check and Kill.
        }
    }

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Cancellation still wins if a broken external tool does not exit promptly.
        }
    }

    /// <summary>Writes a plain-text transcript (no timestamps) from the SRT or ASS subtitle source.</summary>
    private static void WriteTranscript(MediaExportOptions options, string target)
    {
        var source = new[] { options.PlainSubtitlePath, options.SubtitlePath }
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (source is null) return;
        var transcript = string.Equals(Path.GetExtension(source), ".ass", StringComparison.OrdinalIgnoreCase)
            ? ExtractTranscriptFromAss(source)
            : ExtractTranscriptFromSrt(source);
        if (transcript.Length == 0) return;
        File.WriteAllText(target, transcript, new UTF8Encoding(false));
    }

    private static string ExtractTranscriptFromSrt(string path)
    {
        var builder = new StringBuilder();
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.Contains("-->")) continue;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) continue;
            builder.AppendLine(text);
        }
        return builder.ToString();
    }

    private static string ExtractTranscriptFromAss(string path)
    {
        var builder = new StringBuilder();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line["Dialogue:".Length..].TrimStart();
            // ASS 对白行文本前有 9 个逗号分隔的字段
            var textStart = -1;
            var commas = 0;
            for (var i = 0; i < payload.Length; i++)
            {
                if (payload[i] != ',') continue;
                if (++commas == 9) { textStart = i + 1; break; }
            }
            var text = textStart >= 0 ? payload[textStart..] : payload;
            text = Regex.Replace(text, @"\{[^}]*\}", string.Empty)
                .Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
            foreach (var part in text.Split('\n'))
                if (part.Trim().Length > 0) builder.AppendLine(part.Trim());
        }
        return builder.ToString();
    }

    private static string EscapeFilterPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    private static double ParseFraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
            return numerator / denominator;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static string ReadableFfmpegError(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "导出失败，FFmpeg 未返回详细信息。";
        var lines = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(Math.Min(8, lines.Length)));
    }
}
