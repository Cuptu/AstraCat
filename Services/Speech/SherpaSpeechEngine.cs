using System.Diagnostics;
using System.Text;
using SherpaOnnx;

namespace AstraCat.Services.Speech;

public sealed class SherpaTranscriptionOptions
{
    public string? Language { get; set; }
    public string Provider { get; set; } = "cpu";
    public int NumThreads { get; set; } = 4;
    public bool EnableVad { get; set; } = true;
    public float VadThreshold { get; set; } = 0.5f;
    public float VadMinSilence { get; set; } = 0.25f;
    public string? Hotwords { get; set; }
}

public sealed class SpeechTranscriptionResult
{
    public required IReadOnlyList<SpeechSegmentResult> Segments { get; init; }
}

public sealed class SpeechSegmentResult
{
    public int Index { get; init; }
    public long StartMilliseconds { get; init; }
    public long EndMilliseconds { get; init; }
    public required string Text { get; init; }
}

public sealed class SherpaSpeechEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _cachedRecognizer;
    private string? _cachedModelKey;
    private bool _disposed;

    public async Task<SpeechTranscriptionResult> TranscribeMediaAsync(
        string mediaPath,
        string modelId,
        SherpaTranscriptionOptions options,
        IProgress<(int Percent, string Message, string? LogLine)> progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            throw new FileNotFoundException("未找到待识别的媒体文件", mediaPath);

        if (!SherpaModelRegistry.TryGetProfile(modelId, out var profile) || profile is null)
            throw new NotSupportedException($"未找到模型 '{modelId}' 的配置画像");

        if (!SherpaModelLocator.IsModelInstalled(profile, out var resolvedPaths) || resolvedPaths is null)
            throw new FileNotFoundException($"模型 '{profile.DisplayName}' 尚未下载或权重文件不完整，请先在模型管理中下载。");

        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? tempWav = null;
        try
        {
            progress.Report((5, "正在提取音频...", null));
            string wavPath;
            bool isWav = mediaPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

            if (isWav && TryCheckIf16kMono(mediaPath))
            {
                wavPath = mediaPath;
            }
            else
            {
                tempWav = Path.Combine(Path.GetTempPath(), $"astracat_asr_{Guid.NewGuid():N}.wav");
                var ok = AstraCoreNative.TryExtractAudioWav(
                    mediaPath, tempWav, targetSampleRate: 16000, channels: 1, token, out var error);
                if (!ok || !File.Exists(tempWav))
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                        ? "从媒体中提取 16kHz 音频流失败"
                        : $"音频抽取失败: {error}");
                }
                wavPath = tempWav;
            }

            token.ThrowIfCancellationRequested();
            progress.Report((15, "读取音频数据...", null));

            var samples = ReadWavPcm16FloatSamples(wavPath);
            if (samples.Length == 0)
            {
                return new SpeechTranscriptionResult { Segments = Array.Empty<SpeechSegmentResult>() };
            }

            token.ThrowIfCancellationRequested();
            progress.Report((20, $"初始化推理引擎 ({profile.DisplayName})...", null));

            var recognizer = GetOrCreateRecognizer(profile, resolvedPaths, options);

            // Audio segmentation and transcription
            var segments = new List<SpeechSegmentResult>();
            var vadPath = options.EnableVad ? SherpaModelLocator.FindSileroVadModel() : null;

            if (options.EnableVad && vadPath is not null && File.Exists(vadPath))
            {
                segments = await TranscribeWithVadAsync(recognizer, vadPath, samples, options, progress, token).ConfigureAwait(false);
            }
            else
            {
                segments = await TranscribeWindowedAsync(recognizer, samples, options, progress, token).ConfigureAwait(false);
            }

            progress.Report((100, "识别完成", null));
            return new SpeechTranscriptionResult { Segments = segments };
        }
        finally
        {
            if (tempWav is not null)
            {
                try { File.Delete(tempWav); } catch { }
            }
            _gate.Release();
        }
    }

    private async Task<List<SpeechSegmentResult>> TranscribeWithVadAsync(
        OfflineRecognizer recognizer,
        string vadModelPath,
        float[] samples,
        SherpaTranscriptionOptions options,
        IProgress<(int Percent, string Message, string? LogLine)> progress,
        CancellationToken token)
    {
        progress.Report((25, "正在检测人声断句 (Silero VAD)...", null));

        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = vadModelPath;
        vadConfig.SileroVad.Threshold = options.VadThreshold;
        vadConfig.SileroVad.MinSilenceDuration = options.VadMinSilence;
        vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        vadConfig.SileroVad.WindowSize = 512;
        vadConfig.SampleRate = 16000;
        vadConfig.NumThreads = Math.Clamp(options.NumThreads, 1, 4);
        vadConfig.Provider = options.Provider;

        using var vad = new VoiceActivityDetector(vadConfig, bufferSizeInSeconds: 60.0f);

        // Feed in 512-sample chunks
        int chunkSize = 512;
        var speechSlices = new List<SpeechSegment>();

        await Task.Run(() =>
        {
            for (int i = 0; i < samples.Length; i += chunkSize)
            {
                if (token.IsCancellationRequested) return;
                int len = Math.Min(chunkSize, samples.Length - i);
                var chunk = new float[len];
                Array.Copy(samples, i, chunk, 0, len);
                vad.AcceptWaveform(chunk);

                while (!vad.IsEmpty())
                {
                    speechSlices.Add(vad.Front());
                    vad.Pop();
                }
            }

            vad.Flush();
            while (!vad.IsEmpty())
            {
                speechSlices.Add(vad.Front());
                vad.Pop();
            }
        }, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        if (speechSlices.Count == 0)
        {
            // If VAD detected no segments, try transcribing the whole audio as one segment
            return await TranscribeWindowedAsync(recognizer, samples, options, progress, token).ConfigureAwait(false);
        }

        var results = new List<SpeechSegmentResult>();
        int total = speechSlices.Count;

        for (int i = 0; i < total; i++)
        {
            token.ThrowIfCancellationRequested();
            var slice = speechSlices[i];
            if (slice.Samples.Length < 1600) // Skip slices under 0.1s
                continue;

            var sliceText = await Task.Run(() =>
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, slice.Samples);
                recognizer.Decode(stream);
                return stream.Result.Text?.Trim();
            }, token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(sliceText))
            {
                long startMs = (long)Math.Round(slice.Start / 16000.0 * 1000);
                long durationMs = (long)Math.Round(slice.Samples.Length / 16000.0 * 1000);
                long endMs = startMs + Math.Max(durationMs, 500);

                results.Add(new SpeechSegmentResult
                {
                    Index = results.Count + 1,
                    StartMilliseconds = startMs,
                    EndMilliseconds = endMs,
                    Text = sliceText
                });
            }

            int currentPercent = 30 + (int)((i + 1.0) / total * 68);
            progress.Report((currentPercent, $"正在转录 [{i + 1}/{total} 句]...", sliceText));
        }

        return results;
    }

    private async Task<List<SpeechSegmentResult>> TranscribeWindowedAsync(
        OfflineRecognizer recognizer,
        float[] samples,
        SherpaTranscriptionOptions options,
        IProgress<(int Percent, string Message, string? LogLine)> progress,
        CancellationToken token)
    {
        progress.Report((30, "正在转录音频流...", null));
        var results = new List<SpeechSegmentResult>();

        // Maximum chunk size: 25 seconds (400,000 samples at 16kHz)
        int maxChunkSamples = 16000 * 25;
        int totalChunks = (int)Math.Ceiling(samples.Length / (double)maxChunkSamples);

        for (int c = 0; c < totalChunks; c++)
        {
            token.ThrowIfCancellationRequested();
            int startSample = c * maxChunkSamples;
            int length = Math.Min(maxChunkSamples, samples.Length - startSample);
            var chunk = new float[length];
            Array.Copy(samples, startSample, chunk, 0, length);

            var chunkText = await Task.Run(() =>
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, chunk);
                recognizer.Decode(stream);
                return stream.Result.Text?.Trim();
            }, token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(chunkText))
            {
                long startMs = (long)Math.Round(startSample / 16000.0 * 1000);
                long endMs = startMs + (long)Math.Round(length / 16000.0 * 1000);

                results.Add(new SpeechSegmentResult
                {
                    Index = results.Count + 1,
                    StartMilliseconds = startMs,
                    EndMilliseconds = endMs,
                    Text = chunkText
                });
            }

            int percent = 30 + (int)((c + 1.0) / totalChunks * 68);
            progress.Report((percent, $"正在转录分段 [{c + 1}/{totalChunks}]...", chunkText));
        }

        return results;
    }

    private OfflineRecognizer GetOrCreateRecognizer(
        SherpaModelProfile profile,
        ResolvedModelPaths paths,
        SherpaTranscriptionOptions options)
    {
        var modelKey = $"{profile.Id}_{options.Language}_{options.Provider}_{options.NumThreads}";
        if (_cachedRecognizer is not null && _cachedModelKey == modelKey)
        {
            return _cachedRecognizer;
        }

        _cachedRecognizer?.Dispose();
        _cachedRecognizer = null;

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = profile.SampleRate;
        config.FeatConfig.FeatureDim = profile.FeatureDim;
        config.ModelConfig.NumThreads = Math.Clamp(options.NumThreads, 1, 8);
        config.ModelConfig.Provider = options.Provider;
        config.ModelConfig.Debug = 0;

        if (!string.IsNullOrWhiteSpace(options.Hotwords))
        {
            config.HotwordsFile = options.Hotwords;
        }

        switch (profile.Family)
        {
            case AsrModelFamily.Whisper:
                config.ModelConfig.Whisper.Encoder = paths.EncoderPath!;
                config.ModelConfig.Whisper.Decoder = paths.DecoderPath!;
                config.ModelConfig.Tokens = paths.TokensPath!;
                config.ModelConfig.Whisper.Language = options.Language ?? "";
                config.ModelConfig.Whisper.Task = "transcribe";
                break;

            case AsrModelFamily.Qwen3Asr:
                config.ModelConfig.Qwen3Asr.ConvFrontend = paths.ConvFrontendPath!;
                config.ModelConfig.Qwen3Asr.Encoder = paths.EncoderPath!;
                config.ModelConfig.Qwen3Asr.Decoder = paths.DecoderPath!;
                config.ModelConfig.Qwen3Asr.Tokenizer = paths.TokenizerPath!;
                config.ModelConfig.Qwen3Asr.MaxNewTokens = 128;
                config.ModelConfig.Qwen3Asr.Temperature = 0.0f;
                break;

            case AsrModelFamily.NeMoCtc:
                config.ModelConfig.NeMoCtc.Model = paths.ModelPath!;
                config.ModelConfig.Tokens = paths.TokensPath!;
                break;
        }

        try
        {
            _cachedRecognizer = new OfflineRecognizer(config);
        }
        catch (Exception) when (config.ModelConfig.Provider != "cpu")
        {
            config.ModelConfig.Provider = "cpu";
            _cachedRecognizer = new OfflineRecognizer(config);
        }

        _cachedModelKey = modelKey;
        return _cachedRecognizer;
    }

    private static bool TryCheckIf16kMono(string wavPath)
    {
        try
        {
            using var fs = File.OpenRead(wavPath);
            if (fs.Length < 44) return false;
            using var reader = new BinaryReader(fs);
            var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (riff != "RIFF") return false;
            reader.ReadInt32(); // File size
            var wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (wave != "WAVE") return false;

            while (fs.Position < fs.Length - 8)
            {
                var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var chunkSize = reader.ReadInt32();
                if (chunkId == "fmt ")
                {
                    var audioFormat = reader.ReadInt16();
                    var numChannels = reader.ReadInt16();
                    var sampleRate = reader.ReadInt32();
                    return audioFormat == 1 && numChannels == 1 && sampleRate == 16000;
                }
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        catch { }
        return false;
    }

    private static float[] ReadWavPcm16FloatSamples(string wavPath)
    {
        using var fs = File.OpenRead(wavPath);
        using var reader = new BinaryReader(fs);

        var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (riff != "RIFF") throw new InvalidDataException("非标准 RIFF 文件");
        reader.ReadInt32();
        var wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (wave != "WAVE") throw new InvalidDataException("非标准 WAVE 文件");

        int sampleRate = 16000;
        int channels = 1;
        int bitsPerSample = 16;

        while (fs.Position < fs.Length - 8)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                int extra = chunkSize - 16;
                if (extra > 0) fs.Seek(extra, SeekOrigin.Current);
            }
            else if (chunkId == "data")
            {
                int numSamples = chunkSize / (channels * (bitsPerSample / 8));
                var samples = new float[numSamples];
                if (bitsPerSample == 16)
                {
                    for (int i = 0; i < numSamples; i++)
                    {
                        short s = reader.ReadInt16();
                        samples[i] = s / 32768.0f;
                        if (channels > 1)
                        {
                            for (int ch = 1; ch < channels; ch++)
                                reader.ReadInt16(); // Skip extra channels
                        }
                    }
                }
                else if (bitsPerSample == 32)
                {
                    for (int i = 0; i < numSamples; i++)
                    {
                        float s = reader.ReadSingle();
                        samples[i] = s;
                        if (channels > 1)
                        {
                            for (int ch = 1; ch < channels; ch++)
                                reader.ReadSingle();
                        }
                    }
                }
                return samples;
            }
            else
            {
                fs.Seek(chunkSize, SeekOrigin.Current);
            }
        }

        return Array.Empty<float>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        _cachedRecognizer?.Dispose();
        _cachedRecognizer = null;
    }
}
