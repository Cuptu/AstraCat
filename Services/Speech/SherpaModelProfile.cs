namespace AstraCat.Services.Speech;

public enum AsrModelVendor
{
    OpenAI,
    Alibaba,
    Nvidia
}

public enum AsrModelFamily
{
    Whisper,
    Qwen3Asr,
    NeMoCtc
}

public sealed record SherpaModelProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AsrModelVendor Vendor { get; init; }
    public required AsrModelFamily Family { get; init; }
    public required string Description { get; init; }
    public required string SizeText { get; init; }

    public int FeatureDim { get; init; } = 80;
    public int SampleRate { get; init; } = 16000;

    public string? EncoderFile { get; init; }
    public string? DecoderFile { get; init; }
    public string? ConvFrontendFile { get; init; }
    public string? ModelFile { get; init; }
    public string? TokensFile { get; init; }
    public string? TokenizerDir { get; init; }

    public string? ArchiveName { get; init; }
    public string? ModelScopeUrl { get; init; }
    public string? GitHubReleaseUrl { get; init; }
}

public static class SherpaModelRegistry
{
    private static readonly Dictionary<string, SherpaModelProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // 1. OpenAI Whisper Series
        ["whisper-tiny"] = new()
        {
            Id = "whisper-tiny",
            DisplayName = "Whisper Tiny (极简快速)",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "超轻量多语言模型，适合快速试听和资源受限设备",
            SizeText = "约 102 MB",
            FeatureDim = 80,
            EncoderFile = "tiny-encoder.int8.onnx",
            DecoderFile = "tiny-decoder.int8.onnx",
            TokensFile = "tiny-tokens.txt",
            ArchiveName = "sherpa-onnx-whisper-tiny.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-tiny.tar.bz2"
        },
        ["whisper-base"] = new()
        {
            Id = "whisper-base",
            DisplayName = "Whisper Base (标准平衡)",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "多语言标准模型，在速度与精度之间保持良好平衡",
            SizeText = "约 145 MB",
            FeatureDim = 80,
            EncoderFile = "base-encoder.int8.onnx",
            DecoderFile = "base-decoder.int8.onnx",
            TokensFile = "base-tokens.txt",
            ArchiveName = "sherpa-onnx-whisper-base.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.tar.bz2"
        },
        ["whisper-small"] = new()
        {
            Id = "whisper-small",
            DisplayName = "Whisper Small (高精度多语言)",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "高精度多语言模型，适合外语影视、翻译出海视频字幕提取",
            SizeText = "约 460 MB",
            FeatureDim = 80,
            EncoderFile = "small-encoder.int8.onnx",
            DecoderFile = "small-decoder.int8.onnx",
            TokensFile = "small-tokens.txt",
            ArchiveName = "sherpa-onnx-whisper-small.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-small.tar.bz2"
        },
        ["whisper-medium"] = new()
        {
            Id = "whisper-medium",
            DisplayName = "Whisper Medium (专业级多语言)",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "专业级多语言模型，更强的复杂音频解析与翻译能力",
            SizeText = "约 1.5 GB",
            FeatureDim = 80,
            EncoderFile = "medium-encoder.int8.onnx",
            DecoderFile = "medium-decoder.int8.onnx",
            TokensFile = "medium-tokens.txt",
            ArchiveName = "sherpa-onnx-whisper-medium.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-medium.tar.bz2"
        },
        ["whisper-large-v3"] = new()
        {
            Id = "whisper-large-v3",
            DisplayName = "Whisper Large V3 (旗舰级多语言)",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "Whisper 系列顶级旗舰模型，多语言和专业术语准确率极高",
            SizeText = "约 3.0 GB",
            FeatureDim = 80,
            EncoderFile = "large-v3-encoder.int8.onnx",
            DecoderFile = "large-v3-decoder.int8.onnx",
            TokensFile = "large-v3-tokens.txt",
            ArchiveName = "sherpa-onnx-whisper-large-v3.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-large-v3.tar.bz2"
        },
        ["whisper-v3-turbo"] = new()
        {
            Id = "whisper-v3-turbo",
            DisplayName = "Whisper Large V3 Turbo (极速旗舰)",
            Vendor = AsrModelVendor.OpenAI,
            Family = AsrModelFamily.Whisper,
            Description = "新一代加速旗舰，大幅降低解码层数，速度相比 Large V3 提升数倍",
            SizeText = "约 1.6 GB",
            FeatureDim = 80,
            EncoderFile = "turbo-encoder.int8.onnx",
            DecoderFile = "turbo-decoder.int8.onnx",
            TokensFile = "turbo-tokens.txt",
            ArchiveName = "sherpa-onnx-whisper-large-v3-turbo.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-large-v3-turbo.tar.bz2"
        },

        // 2. Alibaba Qwen Series
        ["qwen-0.6b"] = new()
        {
            Id = "qwen-0.6b",
            DisplayName = "Qwen3-ASR 0.6B (中文旗舰推荐)",
            Vendor = AsrModelVendor.Alibaba,
            Family = AsrModelFamily.Qwen3Asr,
            Description = "中文与中英混杂识别顶尖水平，口语断句极其自然，自动规整标点",
            SizeText = "约 838 MB",
            FeatureDim = 128,
            ConvFrontendFile = "conv_frontend.onnx",
            EncoderFile = "encoder.int8.onnx",
            DecoderFile = "decoder.int8.onnx",
            TokenizerDir = "tokenizer",
            ArchiveName = "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2"
        },
        ["qwen-1.7b"] = new()
        {
            Id = "qwen-1.7b",
            DisplayName = "Qwen3-ASR 1.7B (高精大模型)",
            Vendor = AsrModelVendor.Alibaba,
            Family = AsrModelFamily.Qwen3Asr,
            Description = "通义千问 1.7B 语音大模型，深层语义理解能力更上一层楼",
            SizeText = "约 2.2 GB",
            FeatureDim = 128,
            ConvFrontendFile = "conv_frontend.onnx",
            EncoderFile = "encoder.int8.onnx",
            DecoderFile = "decoder.int8.onnx",
            TokenizerDir = "tokenizer",
            ArchiveName = "sherpa-onnx-qwen3-asr-1.7B-int8.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-1.7B-int8.tar.bz2"
        },

        // 3. NVIDIA NeMo Series
        ["nvidia-parakeet-v3"] = new()
        {
            Id = "nvidia-parakeet-v3",
            DisplayName = "NVIDIA Parakeet TDT 0.6B V3 (极速初剪)",
            Vendor = AsrModelVendor.Nvidia,
            Family = AsrModelFamily.NeMoCtc,
            Description = "CTC 非自回归高速架构，高达 60+ 倍速推理，原生附带毫秒级词时间戳",
            SizeText = "约 465 MB",
            FeatureDim = 80,
            ModelFile = "model.int8.onnx",
            TokensFile = "tokens.txt",
            ArchiveName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
            GitHubReleaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"
        }
    };

    public static bool TryGetProfile(string modelId, out SherpaModelProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            profile = null;
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        if (Profiles.TryGetValue(normalized, out profile))
            return true;

        // Common aliases & variants
        if (normalized.Contains("parakeet", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("nvidia-parakeet-v3", out profile)) return true;
        }
        else if (normalized.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains("1.7b", StringComparison.OrdinalIgnoreCase))
            {
                if (Profiles.TryGetValue("qwen-1.7b", out profile)) return true;
            }
            if (Profiles.TryGetValue("qwen-0.6b", out profile)) return true;
        }
        else if (normalized.Contains("turbo", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("whisper-v3-turbo", out profile)) return true;
        }
        else if (normalized.Contains("large", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("whisper-large-v3", out profile)) return true;
        }
        else if (normalized.Contains("medium", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("whisper-medium", out profile)) return true;
        }
        else if (normalized.Contains("small", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("whisper-small", out profile)) return true;
        }
        else if (normalized.Contains("base", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("whisper-base", out profile)) return true;
        }
        else if (normalized.Contains("tiny", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("whisper", StringComparison.OrdinalIgnoreCase))
        {
            if (Profiles.TryGetValue("whisper-tiny", out profile)) return true;
        }

        profile = null;
        return false;
    }

    public static IReadOnlyCollection<SherpaModelProfile> GetAllProfiles() => Profiles.Values;
}
