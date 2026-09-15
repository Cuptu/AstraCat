using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AstraCat;

internal sealed record WaveformData(double DurationSeconds, float[] Peaks);

internal static class WaveformService
{
    private const int SampleRate = 4000;
    private const int TargetPeaksPerSecond = 120;
    private const int MaximumPeaks = 48000;
    private const int CacheMagic = 0x57464341; // ACFW
    private const int CacheVersion = 1;

    public static async Task<WaveformData> LoadAsync(string mediaPath, string cacheRoot, CancellationToken token)
    {
        Directory.CreateDirectory(cacheRoot);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{MediaInput.CacheIdentity(mediaPath)}|v4"))).ToLowerInvariant();
        var cachePath = Path.Combine(cacheRoot, $"{cacheKey}.waveform.bin");
        var cached = await TryReadCacheAsync(cachePath, token).ConfigureAwait(false);
        if (cached is not null) return cached;

        var duration = await ProbeDurationAsync(mediaPath, token).ConfigureAwait(false);
        var samplesPerPeak = duration > 0
            ? Math.Max(1L, (long)Math.Ceiling(duration * SampleRate /
                Math.Min(MaximumPeaks, Math.Max(1d, duration * TargetPeaksPerSecond))))
            : Math.Max(1, SampleRate / TargetPeaksPerSecond);

        // 优先通过 AstraCore Native 内存级 C ABI 高性能提取音频波形峰值（零子进程、零管道、Direct-to-RAM，支持原生协作取消）
        token.ThrowIfCancellationRequested();
        var (extracted, nativeDuration, nativePeaks, nativeErr) = await Task.Run(() =>
        {
            var ok = AstraCoreNative.TryExtractWaveformPeaks(
                mediaPath, SampleRate, (int)samplesPerPeak, MaximumPeaks,
                out var d, out var p, token, out var err);
            return (ok, d, p, err);
        }, token).ConfigureAwait(false);

        if (extracted && nativePeaks.Length > 0)
        {
            var effectiveNativeDuration = nativeDuration > 0 ? nativeDuration : (duration > 0 ? duration : 0.001);
            var waveformData = new WaveformData(effectiveNativeDuration, nativePeaks);
            await WriteCacheAsync(cachePath, waveformData, token).ConfigureAwait(false);
            return waveformData;
        }

        if (!string.IsNullOrWhiteSpace(nativeErr))
        {
            Trace.TraceInformation($"[WaveformService] 原生波形计算回退至 FFmpeg CLI: {nativeErr}");
        }

        var ffmpeg = MediaToolLocator.FindFfmpeg() ??
                      throw new FileNotFoundException("未找到 FFmpeg。请安装 AstraCore 运行时，或准备兼容的 runtime/tools/ffmpeg。");

        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
                 {
                     "-nostdin", "-hide_banner", "-v", "error", "-i", mediaPath,
                     "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "1",
                     "-ar", SampleRate.ToString(), "-f", "s16le", "pipe:1"
                 })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg 启动失败。");
        using var cancellationRegistration = token.Register(() => TryTerminate(process));
        var stderrTask = process.StandardError.ReadToEndAsync();
        var peaks = new List<float>(duration > 0
            ? (int)Math.Min(MaximumPeaks, Math.Ceiling(duration * TargetPeaksPerSecond))
            : 4096);
        var buffer = new byte[65536];
        long samplesInBucket = 0;
        long totalSamples = 0;
        var bucketMaximum = 0f;
        var pendingLowByte = -1;

        void AddSample(short sample)
        {
            totalSamples++;
            bucketMaximum = Math.Max(bucketMaximum, Math.Abs(sample / 32768f));
            if (++samplesInBucket < samplesPerPeak) return;

            peaks.Add(bucketMaximum);
            samplesInBucket = 0;
            bucketMaximum = 0;
            if (peaks.Count < MaximumPeaks) return;

            var compactedCount = 0;
            for (var source = 0; source < peaks.Count; source += 2)
            {
                peaks[compactedCount++] = source + 1 < peaks.Count
                    ? Math.Max(peaks[source], peaks[source + 1])
                    : peaks[source];
            }
            peaks.RemoveRange(compactedCount, peaks.Count - compactedCount);
            samplesPerPeak *= 2;
        }

        try
        {
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                var index = 0;
                if (pendingLowByte >= 0 && read > 0)
                {
                    AddSample((short)(pendingLowByte | buffer[0] << 8));
                    pendingLowByte = -1;
                    index = 1;
                }

                for (; index + 1 < read; index += 2)
                    AddSample((short)(buffer[index] | buffer[index + 1] << 8));

                if (index < read) pendingLowByte = buffer[index];
            }

            if (samplesInBucket > 0) peaks.Add(bucketMaximum);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var error = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg 波形分析失败：{error.Trim()}");
        }
        catch
        {
            TryTerminate(process);
            throw;
        }

        if (peaks.Count == 0) throw new InvalidOperationException("媒体中没有可用的音频轨道。");
        var effectiveDuration = duration > 0 ? duration : totalSamples / (double)SampleRate;
        var result = new WaveformData(Math.Max(effectiveDuration, 0.001), peaks.ToArray());
        await WriteCacheAsync(cachePath, result, token).ConfigureAwait(false);
        return result;
    }

    private static async Task<WaveformData?> TryReadCacheAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != CacheMagic || reader.ReadInt32() != CacheVersion) return null;
            var duration = reader.ReadDouble();
            var count = reader.ReadInt32();
            if (!double.IsFinite(duration) || duration <= 0 || count <= 0 || count > MaximumPeaks ||
                stream.Length - stream.Position != count * sizeof(float)) return null;

            var peaks = new float[count];
            for (var i = 0; i < count; i++)
            {
                peaks[i] = reader.ReadSingle();
                if (!float.IsFinite(peaks[i]) || peaks[i] < 0 || peaks[i] > 1) return null;
            }
            return new WaveformData(duration, peaks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(string path, WaveformData data, CancellationToken token)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var stream = new MemoryStream(20 + data.Peaks.Length * sizeof(float));
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(CacheMagic);
                writer.Write(CacheVersion);
                writer.Write(data.DurationSeconds);
                writer.Write(data.Peaks.Length);
                foreach (var peak in data.Peaks) writer.Write(peak);
            }
            await File.WriteAllBytesAsync(temporaryPath, stream.ToArray(), token).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
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

    private static async Task<double> ProbeDurationAsync(string mediaPath, CancellationToken token)
    {
        if (AstraCoreNative.TryProbe(mediaPath, out var nativeProbe)) return nativeProbe.DurationSeconds;
        var ffprobe = MediaToolLocator.FindFfprobe();
        if (ffprobe is null) return 0;
        var start = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", mediaPath })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return 0;
        using var cancellationRegistration = token.Register(() => TryTerminate(process));
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);
        return double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
