using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace AstraCat;

public partial class MainWindow : Window
{
    private sealed class CaptionProject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "未命名项目";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
        public string? SourceVideoPath { get; set; }
        public string? TranscriptionModelId { get; set; }
        public bool IsPinned { get; set; }
        public string? SubtitlePath { get; set; }
        public string? ProcessedSubtitlePath { get; set; }
        public string WorkflowMode { get; set; } = "video-bilingual";
        public string TranscriptionLanguage { get; set; } = "自动检测";
        public string TranscriptionDevice { get; set; } = "自动选择";
        public string TranscriptionPrecision { get; set; } = "自动";
        public int TranscriptionBeamSize { get; set; } = 5;
        public double TranscriptionTemperature { get; set; } = 0.2;
        public bool EnableVadFilter { get; set; } = true;
    public double VadThreshold { get; set; } = 0.3;
        public int VadMinSilence { get; set; } = 2000;
        public int VadSpeechPad { get; set; } = 400;
        public int TranscriptionMaxTokens { get; set; } = 512;
        public bool EnableWordTimestamps { get; set; } = true;
        public string TranscriptionHotwords { get; set; } = string.Empty;
        public bool EnableDiarization { get; set; } = true;
        public string TranscriptionSpeakerCount { get; set; } = "自动检测";
        public bool EnableEmotion { get; set; } = true;
        public bool EnableAudioEvent { get; set; } = true;
        public int TranscriptionChunkSeconds { get; set; } = 30;
        public bool EnableSubtitleProcessing { get; set; } = true;
        public string SubtitleProcessingProvider { get; set; } = "deepseek";
        public bool EnableLlmSegmentation { get; set; } = true;
        public int EnglishWordLimit { get; set; } = 12;
        public bool EnableSubtitleProofreading { get; set; } = false;
        public bool EnableWebTerminologyResearch { get; set; } = false;
        public string SubtitleProcessingPrompt { get; set; } = string.Empty;
        public string TranslationProvider { get; set; } = "deepseek";
        public string TranslationTargetLanguage { get; set; } = "简体中文";
        public string SubtitleLayout { get; set; } = "译文在上";
        public bool CorrectSubtitles { get; set; } = false;
        public bool ReflectTranslation { get; set; } = true;
        public string TranslationPrompt { get; set; } = string.Empty;
        public string SubtitleFontFamily { get; set; } = "Microsoft YaHei UI";
        public double SubtitleFontSize { get; set; } = 42;
        public string SubtitleTextColor { get; set; } = "#FFFFFF";
        public string SubtitleOutlineColor { get; set; } = "#000000";
        public double SubtitleOutlineWidth { get; set; } = 2.5;
        public int SubtitleTrackCount { get; set; } = 2;
        public SubtitleStyleDefinition MainSubtitleStyle { get; set; } = SubtitleStyleDefinition.MainDefault();
        public SubtitleStyleDefinition SecondarySubtitleStyle { get; set; } = SubtitleStyleDefinition.SecondaryDefault();
        public List<SubtitleStyleDefinition> SubtitleStyles { get; set; } = new();
        public string MainSubtitleStyleId { get; set; } = "main";
        public string SecondarySubtitleStyleId { get; set; } = "secondary";
        public List<string> SubtitleTrackStyleIds { get; set; } = new() { "main", "secondary" };
        public int SubtitleStyleDefaultsVersion { get; set; }
    }

    private sealed class SubtitleSegment : INotifyPropertyChanged
    {
        private string _original = string.Empty;
        private string _translated = string.Empty;

        public int Index { get; set; }
        public long StartMilliseconds { get; set; }
        public long EndMilliseconds { get; set; }
        public string Original
        {
            get => _original;
            set
            {
                if (_original == value) return;
                _original = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Original)));
            }
        }
        public string Translated
        {
            get => _translated;
            set
            {
                if (_translated == value) return;
                _translated = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translated)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class TranslationBatchItem
    {
        public int Id { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private sealed class TranslationProviderProfile
    {
        public string DisplayName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string Protocol { get; set; } = "openai-responses";
        public List<string> SupportedProtocols { get; set; } = new();
        public Dictionary<string, string> EndpointBaseUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public int RegistryOrder { get; set; } = int.MaxValue;
        public string Model { get; set; } = string.Empty;
        public List<string> Models { get; set; } = new();
        public string SystemPrompt { get; set; } = DefaultTranslationPrompt;
        public int CacheTokenThreshold { get; set; } = 1024;
        public int CacheLastNMessages { get; set; } = 2;
        public bool CacheSystemMessage { get; set; } = true;
        public bool DeveloperRole { get; set; }
        public bool StreamOptions { get; set; } = true;
        public bool ReasoningSummary { get; set; }
    }

    private sealed class TranslationModelSettings
    {
        public string ActiveProvider { get; set; } = "deepseek";
        public Dictionary<string, TranslationProviderProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DeletedProviders { get; set; } = new();
    }

    private const string DefaultTranslationPrompt =
        "你是一名专业的字幕翻译助手。请准确理解上下文，保持人物语气与专有名词一致，" +
        "输出自然、简洁且适合屏幕阅读的目标语言字幕，不要添加解释或额外标记。";

    private sealed class DownloadUiTask(long id, string title)
    {
        public long Id { get; } = id;
        public string Title { get; } = title;
        public string Status { get; set; } = "正在准备下载…";
        public double? Fraction { get; set; }
        public long? DownloadedBytes { get; set; }
        public long? TotalBytes { get; set; }
        public double? BytesPerSecond { get; set; }
        public TimeSpan? Remaining { get; set; }
        public bool Running { get; set; } = true;
        public bool Succeeded { get; set; }
        public bool Paused { get; set; }
        public bool PauseRequested { get; set; }
        public CancellationTokenSource UserCancellation { get; } = new();
        public CancellationTokenSource? AttemptCancellation { get; set; }
        public TaskCompletionSource<bool> ResumeSignal { get; set; } =
            NewResumeSignal();
        public Border? Row { get; set; }
        public TextBlock? StatusText { get; set; }
        public TextBlock? PercentText { get; set; }
        public TextBlock? SizeText { get; set; }
        public TextBlock? SpeedText { get; set; }
        public TextBlock? RemainingText { get; set; }
        public ProgressBar? ProgressBar { get; set; }
        public Button? PauseButton { get; set; }
        public Button? CancelButton { get; set; }

        private static TaskCompletionSource<bool> NewResumeSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PreparePause()
        {
            if (Paused) return;
            Paused = true;
            PauseRequested = true;
            ResumeSignal = NewResumeSignal();
        }
    }

    private sealed record ModelDeploymentInfo(
        string Id,
        string Title,
        string Subtitle,
        string WeightDescription,
        string AssetUri,
        string RuntimeId,
        string RuntimeTitle,
        string RuntimeDescription);

    private sealed record ModelConfigurationSnapshot(
        string ModelId,
        string Device,
        string Language,
        string Precision,
        int BeamSize,
        bool Vad,
        double VadThreshold,
        int VadMinSilence,
        int VadSpeechPad,
        int MaxTokens,
        bool Timestamps,
        string Hotwords,
        bool EmotionDetection,
        bool AudioEventDetection,
        string SpeakerCount,
        bool Diarization,
        double Temperature,
        int ChunkSeconds,
        IReadOnlyDictionary<string, object?> Advanced);

    private enum ParameterKind { Boolean, Integer, Decimal, Text, Select }

    private sealed record ParameterDefinition(
        string Key,
        string Section,
        string Label,
        string Description,
        ParameterKind Kind,
        object DefaultValue,
        IReadOnlyList<string>? Options = null);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ParameterDefinition>> AdvancedParameters =
        new Dictionary<string, IReadOnlyList<ParameterDefinition>>(StringComparer.OrdinalIgnoreCase)
        {
            ["whisper-tiny"] = WhisperParameters(),
            ["whisper-base"] = WhisperParameters(),
            ["whisper-small"] = WhisperParameters(),
            ["whisper-medium"] = WhisperParameters(),
            ["whisper-large-v3"] = WhisperParameters(),
            ["whisper-v3-turbo"] = WhisperParameters(),
            ["qwen-0.6b"] = QwenParameters(),
            ["qwen-1.7b"] = QwenParameters(),
            ["nvidia-parakeet-v3"] = NvidiaParakeetParameters(),
            ["nvidia-canary-v2"] = NvidiaCanaryParameters()
        };

    private static IReadOnlyList<ParameterDefinition> WhisperParameters() =>
    [
        new("initialPrompt", "其他设置", "提示词", "可选的专有名词、场景或格式提示", ParameterKind.Text, "")
    ];

    private static IReadOnlyList<ParameterDefinition> QwenParameters() =>
    [
        new("maxInferenceBatchSize", "性能设置", "推理批大小", "较小值更省显存，较大值适合批量音频", ParameterKind.Integer, 1),
        new("lowCpuMemoryUsage", "性能设置", "低内存加载", "加载模型时降低 CPU 内存峰值", ParameterKind.Boolean, true),
        new("context", "识别设置", "上下文提示", "描述音频场景、主题或可能出现的内容", ParameterKind.Text, "")
    ];

    private static IReadOnlyList<ParameterDefinition> NvidiaParakeetParameters() =>
    [
        new("attentionMode", "长音频", "注意力模式", "局部注意力适合长音频，全局注意力精度更高", ParameterKind.Select, "局部注意力", ["局部注意力", "全局注意力"]),
        new("chunkSeconds", "长音频", "分块时长", "按秒拆分长音频，降低显存峰值", ParameterKind.Integer, 600),
        new("batchSize", "性能设置", "推理批大小", "显存充足时可提高吞吐量", ParameterKind.Integer, 1)
    ];

    private static IReadOnlyList<ParameterDefinition> NvidiaCanaryParameters() =>
    [
        new("task", "识别设置", "任务", "转写原语言或翻译为目标语言", ParameterKind.Select, "语音转文字", ["语音转文字", "翻译为英文"]),
        new("targetLanguage", "识别设置", "翻译目标语言", "仅在语音翻译任务中使用", ParameterKind.Select, "English", ["English", "German", "French", "Spanish"]),
        new("numBeams", "解码设置", "搜索宽度", "数值越高通常越准确，但推理更慢", ParameterKind.Integer, 5),
        new("lengthPenalty", "解码设置", "长度惩罚", "控制生成字幕的长度偏好", ParameterKind.Decimal, 1d)
    ];

    private const string WhisperLanguageSpec =
        "zh|中文 / Chinese;en|英语 / English;yue|粤语 / Cantonese;ja|日语 / Japanese;ko|韩语 / Korean;" +
        "fr|法语 / French;de|德语 / German;es|西班牙语 / Spanish;ru|俄语 / Russian;pt|葡萄牙语 / Portuguese;" +
        "it|意大利语 / Italian;ar|阿拉伯语 / Arabic;hi|印地语 / Hindi;vi|越南语 / Vietnamese;th|泰语 / Thai;" +
        "id|印度尼西亚语 / Indonesian;tr|土耳其语 / Turkish;pl|波兰语 / Polish;nl|荷兰语 / Dutch;sv|瑞典语 / Swedish;" +
        "af|Afrikaans;am|Amharic;as|Assamese;az|Azerbaijani;ba|Bashkir;be|Belarusian;bg|Bulgarian;bn|Bengali;" +
        "bo|Tibetan;br|Breton;bs|Bosnian;ca|Catalan;cs|Czech;cy|Welsh;da|Danish;el|Greek;et|Estonian;eu|Basque;" +
        "fa|Persian;fi|Finnish;fo|Faroese;gl|Galician;gu|Gujarati;ha|Hausa;haw|Hawaiian;he|Hebrew;hr|Croatian;" +
        "ht|Haitian Creole;hu|Hungarian;hy|Armenian;is|Icelandic;jw|Javanese;ka|Georgian;kk|Kazakh;km|Khmer;" +
        "kn|Kannada;la|Latin;lb|Luxembourgish;ln|Lingala;lo|Lao;lt|Lithuanian;lv|Latvian;mg|Malagasy;mi|Maori;" +
        "mk|Macedonian;ml|Malayalam;mn|Mongolian;mr|Marathi;ms|Malay;mt|Maltese;my|Myanmar;ne|Nepali;nn|Nynorsk;" +
        "no|Norwegian;oc|Occitan;pa|Punjabi;ps|Pashto;ro|Romanian;sa|Sanskrit;sd|Sindhi;si|Sinhala;sk|Slovak;" +
        "sl|Slovenian;sn|Shona;so|Somali;sq|Albanian;sr|Serbian;su|Sundanese;sw|Swahili;ta|Tamil;te|Telugu;" +
        "tg|Tajik;tk|Turkmen;tl|Tagalog;tt|Tatar;uk|Ukrainian;ur|Urdu;uz|Uzbek;yi|Yiddish;yo|Yoruba";

    private const string QwenLanguageSpec =
        "Chinese|中文 / Chinese;English|英语 / English;Cantonese|粤语 / Cantonese;Arabic|阿拉伯语 / Arabic;" +
        "German|德语 / German;French|法语 / French;Spanish|西班牙语 / Spanish;Portuguese|葡萄牙语 / Portuguese;" +
        "Indonesian|印度尼西亚语 / Indonesian;Italian|意大利语 / Italian;Korean|韩语 / Korean;Russian|俄语 / Russian;" +
        "Thai|泰语 / Thai;Vietnamese|越南语 / Vietnamese;Japanese|日语 / Japanese;Turkish|土耳其语 / Turkish;" +
        "Hindi|印地语 / Hindi;Malay|马来语 / Malay;Dutch|荷兰语 / Dutch;Swedish|瑞典语 / Swedish;" +
        "Danish|丹麦语 / Danish;Finnish|芬兰语 / Finnish;Polish|波兰语 / Polish;Czech|捷克语 / Czech;" +
        "Filipino|菲律宾语 / Filipino;Persian|波斯语 / Persian;Greek|希腊语 / Greek;Romanian|罗马尼亚语 / Romanian;" +
        "Hungarian|匈牙利语 / Hungarian;Macedonian|马其顿语 / Macedonian";

    private const string NvidiaLanguageSpec =
        "bg|保加利亚语 / Bulgarian;hr|克罗地亚语 / Croatian;cs|捷克语 / Czech;da|丹麦语 / Danish;nl|荷兰语 / Dutch;" +
        "en|英语 / English;et|爱沙尼亚语 / Estonian;fi|芬兰语 / Finnish;fr|法语 / French;de|德语 / German;" +
        "el|希腊语 / Greek;hu|匈牙利语 / Hungarian;it|意大利语 / Italian;lv|拉脱维亚语 / Latvian;lt|立陶宛语 / Lithuanian;" +
        "mt|马耳他语 / Maltese;pl|波兰语 / Polish;pt|葡萄牙语 / Portuguese;ro|罗马尼亚语 / Romanian;sk|斯洛伐克语 / Slovak;" +
        "sl|斯洛文尼亚语 / Slovenian;es|西班牙语 / Spanish;sv|瑞典语 / Swedish;ru|俄语 / Russian;uk|乌克兰语 / Ukrainian";

    private const string FunAsrNanoLanguageSpec =
        "zh|中文 / Chinese;en|英语 / English;ja|日语 / Japanese";

    private const string SenseVoiceLanguageSpec =
        "zh|中文 / Chinese;en|英语 / English;yue|粤语 / Cantonese;ja|日语 / Japanese;ko|韩语 / Korean";

    private static readonly IReadOnlyDictionary<string, ModelDeploymentInfo> ModelDeployments =
        new Dictionary<string, ModelDeploymentInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["whisper-tiny"] = new("whisper-tiny", "Whisper Tiny", "99 种语言 · 最快 · 适合低资源设备", "75 MB", "avares://AstraCat/Assets/Models/openai.png", "whisper-runtime", "Faster-Whisper / CTranslate2", "Whisper 推理运行库"),
            ["whisper-base"] = new("whisper-base", "Whisper Base", "速度与精度入门平衡", "145 MB", "avares://AstraCat/Assets/Models/openai.png", "whisper-runtime", "Faster-Whisper / CTranslate2", "Whisper 推理运行库"),
            ["whisper-small"] = new("whisper-small", "Whisper Small", "均衡 · 分段时间戳", "464 MB", "avares://AstraCat/Assets/Models/openai.png", "whisper-runtime", "Faster-Whisper / CTranslate2", "Whisper 推理运行库"),
            ["whisper-medium"] = new("whisper-medium", "Whisper Medium", "高精度 · 中等显存占用", "1.53 GB", "avares://AstraCat/Assets/Models/openai.png", "whisper-runtime", "Faster-Whisper / CTranslate2", "Whisper 推理运行库"),
            ["whisper-large-v3"] = new("whisper-large-v3", "Whisper Large V3", "高精度 · 建议 6 GB 显存", "3.1 GB", "avares://AstraCat/Assets/Models/openai.png", "whisper-runtime", "Faster-Whisper / CTranslate2", "Whisper 推理运行库"),
            ["whisper-v3-turbo"] = new("whisper-v3-turbo", "Whisper Large V3 Turbo", "99 种语言 · Large V3 高速精简版", "1.62 GB", "avares://AstraCat/Assets/Models/openai.png", "nvidia-runtime", "Transformers / OpenAI Whisper", "Whisper Turbo 推理环境"),
            ["qwen-0.6b"] = new("qwen-0.6b", "Qwen3-ASR 0.6B", "30 种语言 + 22 种中文方言", "1.8 GB", "avares://AstraCat/Assets/Models/qwen-hf.jpeg", "qwen-runtime", "PyTorch / Qwen-ASR", "支持 CPU；安装 CUDA 版 PyTorch 后启用 GPU"),
            ["qwen-1.7b"] = new("qwen-1.7b", "Qwen3-ASR 1.7B", "高精度 · 建议 8 GB 显存", "4.7 GB", "avares://AstraCat/Assets/Models/qwen-hf.jpeg", "qwen-runtime", "PyTorch / Qwen-ASR", "支持 CPU；安装 CUDA 版 PyTorch 后启用 GPU"),
            ["funasr-nano"] = new("funasr-nano", "Fun-ASR Nano 2512", "方言、热词、时间戳、实时识别", "1.99 GB", "avares://AstraCat/Assets/Models/funaudiollm.png", "funasr-runtime", "FunASR / PyTorch", "FunASR 隔离推理环境"),
            ["sensevoice-small"] = new("sensevoice-small", "SenseVoice Small", "低延迟 · 情绪与声音事件", "944 MB", "avares://AstraCat/Assets/Models/funaudiollm.png", "funasr-runtime", "FunASR / PyTorch", "SenseVoice 隔离推理环境"),
            ["nvidia-parakeet-v3"] = new("nvidia-parakeet-v3", "NVIDIA Parakeet TDT 0.6B V3", "25 种语言 · 高吞吐 · 词级时间戳", "2.6 GB", "avares://AstraCat/Assets/Models/nvidia.png", "nvidia-runtime", "NVIDIA Transformers", "Parakeet / Canary 推理环境"),
            ["nvidia-canary-v2"] = new("nvidia-canary-v2", "NVIDIA Canary 1B V2", "25 种语言 · 识别与语音翻译", "6.36 GB", "avares://AstraCat/Assets/Models/nvidia.png", "nemo-runtime", "NVIDIA NeMo / PyTorch", "Canary 隔离推理环境"),
            ["moss-0.9b"] = new("moss-0.9b", "MOSS Transcribe-Diarize 0.9B", "长音频 · 时间戳 · 说话人分离", "1.83 GB", "avares://AstraCat/Assets/Models/openmoss.png", "moss-runtime", "MOSS / Transformers", "MOSS 隔离推理环境")
        };

    private readonly MotionService _motion = new();
    private readonly DeploymentManager _deployment = new();
    private readonly ModelCatalogService _modelCatalog;
    private readonly AsrWorkerClient _asrWorker;
    private readonly DispatcherTimer _catalogSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly HashSet<Button> _motionButtons = new();
    private readonly HashSet<Border> _motionModelRows = new();
    private readonly HashSet<Border> _pressedModelRows = new();
    private readonly HashSet<Border> _expandedDeploymentGroups = new();
    private int _navigationEpoch;
    private CancellationTokenSource _navigation = new();
    private CancellationTokenSource _modelTabNavigation = new();
    private CancellationTokenSource _projectSectionNavigation = new();
    private bool _projectSectionTransitioning;
    private string _workspaceReturnSection = "flow";
    private CancellationTokenSource _catalogRefresh = new();
    private CancellationTokenSource _configurationAutoSave = new();
    private CancellationTokenSource _projectSettingsPersistence = new();
    private Control _activePage;
    private bool _modelSettingsActive;
    private bool _isClosing;
    private bool _allowClose;
    private string? _selectedDeploymentModelId;
    private int _cudaStatusGeneration;
    private CudaRuntimeStatus? _lastCudaStatus;
    private bool _loadingModelConfiguration;
    private int _configuredModelIndex = -1;
    private readonly List<(string Id, string Name)> _configurableModels = new();
    private readonly Dictionary<string, Control> _advancedConfigControls = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ParameterDefinition> _activeAdvancedDefinitions = Array.Empty<ParameterDefinition>();
    private readonly Dictionary<long, DownloadUiTask> _downloadUiTasks = new();
    private readonly List<CaptionProject> _projects = new();
    private readonly Dictionary<string, Button> _projectButtons = new(StringComparer.OrdinalIgnoreCase);
    private long _nextDownloadUiTaskId;
    private bool _downloadTaskPanelOpen;
    private int _installedModelCount;
    private string _catalogStatus = string.Empty;
    private double _catalogLoadingTilePhase;
    private string? _activeProjectId;
    private Control _activeProjectSectionView = null!;
    private int _projectSectionIndex;
    private bool _loadingProjectTranscription;
    private bool _projectTranscriptionRunning;
    private bool _projectFlowRunning;
    private CancellationTokenSource _projectTranscriptionCancellation = new();
    private readonly StringBuilder _projectTranscriptionLog = new();
    private readonly DispatcherTimer _projectTranscriptionLogFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _projectTranscriptionLogSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _projectTranscriptionLogDirty;
    private double _projectTranscriptionLogTilePhase;
    private bool _projectTranscriptionLogVisible;
    private bool _loadingProjectProcessing;
    private bool _projectProcessingRunning;
    private CancellationTokenSource _projectProcessingCancellation = new();
    private bool _projectProcessingResearchVisible;
    private string? _forceTerminologyResearchRefreshProjectId;
    private bool _loadingProjectTranslation;
    private readonly List<SubtitleSegment> _projectTranslationSegments = new();
    private readonly DispatcherTimer _projectTranslationCacheTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private CancellationTokenSource _projectTranslationCancellation = new();
    private bool _projectTranslationRunning;
    private static readonly HttpClient TranslationHttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Dictionary<string, TranslationProviderProfile> _translationProfiles = CreateDefaultTranslationProfiles();
    private readonly HashSet<string> _deletedTranslationProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _translationProviderButtons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Bitmap> TranslationLogoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Bitmap> ModelLogoCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _translationProviderSearchDebounce = new();
    private string _activeTranslationProvider = "deepseek";
    private bool _loadingTranslationProfile;
    private bool _translationModelFetchRunning;
    private bool _translationApiDrawerOpen;
    private bool _translationApiDrawerAnimating;
    private bool _secondaryPagesPrewarmed;
    private CancellationTokenSource _translationApiDrawerAnimation = new();

    public MainWindow()
    {
        _modelCatalog = new ModelCatalogService(Path.Combine(_deployment.RuntimeRoot, "cache"));
        _asrWorker = new AsrWorkerClient(_deployment);
        InitializeComponent();
        InitializeWorkspace();
        LoadTranslationSettings();
        InitializeSettings();
        RebuildProjectProviderOptions();
        RebuildTranslationProviderList();
        ApplyTranslationProvider(_activeTranslationProvider);
        _activeProjectSectionView = ProjectFlowView;
        LoadProjects();
        RebuildProjectSidebar();
        ProjectTranslationTableHost.ItemTemplate = new FuncDataTemplate<SubtitleSegment>(
            (segment, _) => CreateSubtitleRow(segment), supportsRecycling: false);
        TaskOverviewListHost.ItemTemplate = new FuncDataTemplate<TaskOverviewItem>(
            (item, _) => item is null ? new Border() : CreateTaskOverviewRow(item), supportsRecycling: false);
        _projectTranslationCacheTimer.Tick += (_, _) =>
        {
            _projectTranslationCacheTimer.Stop();
            SaveActiveProjectTranslationCache();
        };
        ProjectTranscriptionModelCombo.SelectionChanged += ProjectTranscriptionModel_OnSelectionChanged;
        ProjectTranscriptionDeviceCombo.SelectionChanged += ProjectTranscriptionSettings_OnSelectionChanged;
        ProjectTranscriptionLanguageCombo.SelectionChanged += ProjectTranscriptionSettings_OnSelectionChanged;
        ProjectTranscriptionPrecisionCombo.SelectionChanged += ProjectTranscriptionSettings_OnSelectionChanged;
        ProjectTranscriptionSpeakerCountCombo.SelectionChanged += ProjectTranscriptionSettings_OnSelectionChanged;
        ProjectTranscriptionBeamSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionTemperatureSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionVadToggle.PropertyChanged += ProjectTranscriptionToggle_OnPropertyChanged;
        ProjectTranscriptionVadThresholdSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionVadMinSilenceSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionVadSpeechPadSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionMaxTokensSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionWordTimestampsToggle.PropertyChanged += ProjectTranscriptionToggle_OnPropertyChanged;
        ProjectTranscriptionDiarizationToggle.PropertyChanged += ProjectTranscriptionToggle_OnPropertyChanged;
        ProjectTranscriptionEmotionToggle.PropertyChanged += ProjectTranscriptionToggle_OnPropertyChanged;
        ProjectTranscriptionAudioEventToggle.PropertyChanged += ProjectTranscriptionToggle_OnPropertyChanged;
        ProjectTranscriptionChunkSecondsSlider.PropertyChanged += ProjectTranscriptionSlider_OnPropertyChanged;
        ProjectTranscriptionHotwordsBox.LostFocus += (_, _) => SaveProjectTranscriptionSettings();
        ProjectProcessingProviderCombo.SelectionChanged += ProjectProcessingSettings_OnChanged;
        ProjectSegmentationToggle.PropertyChanged += ProjectProcessingToggle_OnPropertyChanged;
        ProjectEnglishWordLimitSlider.PropertyChanged += ProjectProcessingSlider_OnPropertyChanged;
        ProjectProofreadingToggle.PropertyChanged += ProjectProcessingToggle_OnPropertyChanged;
        ProjectWebResearchToggle.PropertyChanged += ProjectProcessingToggle_OnPropertyChanged;
        ProjectProcessingPromptBox.LostFocus += ProjectProcessingPrompt_OnLostFocus;
        ProjectCorrectionToggle.PropertyChanged += ProjectTranslationSettings_OnPropertyChanged;
        ProjectReflectToggle.PropertyChanged += ProjectTranslationSettings_OnPropertyChanged;
        _catalogSpinnerTimer.Tick += CatalogSpinner_OnTick;
        _projectTranscriptionLogSpinnerTimer.Tick += ProjectTranscriptionLogSpinner_OnTick;
        _projectTranscriptionLogFlushTimer.Tick += (_, _) => FlushProjectTranscriptionLog();
        DeviceCombo.SelectionChanged += ConfigurationValue_OnSelectionChanged;
        LanguageCombo.SelectionChanged += ConfigurationValue_OnSelectionChanged;
        PrecisionCombo.SelectionChanged += ConfigurationValue_OnSelectionChanged;
        SpeakerCountCombo.SelectionChanged += ConfigurationValue_OnSelectionChanged;
        BeamSizeSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        VadToggle.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        VadThresholdSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        VadMinSilenceSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        VadSpeechPadSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        MaxTokensSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        TimestampToggle.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        EmotionToggle.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        AudioEventToggle.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        DiarizationToggle.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        TemperatureSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        ChunkSecondsSlider.PropertyChanged += ConfigurationValue_OnPropertyChanged;
        HotwordsBox.TextChanged += ConfigurationText_OnTextChanged;
        _activePage = OverviewPage;
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        Closed += (_, _) =>
        {
            _navigation.Cancel();
            _modelTabNavigation.Cancel();
            _projectSectionNavigation.Cancel();
            _projectTranscriptionCancellation.Cancel();
            _projectProcessingCancellation.Cancel();
            _projectTranslationCancellation.Cancel();
            _translationApiDrawerAnimation.Cancel();
            _translationProviderSearchDebounce.Cancel();
            _settingsCacheRefresh.Cancel();
            _workspaceListScrollAnimation.Cancel();
            _workspaceLoading.Cancel();
            _workspaceWaveformLoading.Cancel();
            _workspaceAutoSaveTimer.Stop();
            _catalogRefresh.Cancel();
            _configurationAutoSave.Cancel();
            _projectSettingsPersistence.Cancel();
            _catalogSpinnerTimer.Stop();
            _projectTranscriptionLogSpinnerTimer.Stop();
            _projectTranscriptionLogFlushTimer.Stop();
            _asrWorker.Dispose();
            _modelCatalog.Dispose();
            foreach (var task in _downloadUiTasks.Values)
            {
                task.UserCancellation.Cancel();
                task.AttemptCancellation?.Cancel();
            }
        };
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshDeploymentStatus();
        SetupButtonMotion(this);
        SetupModelRowMotion(this);
        var sidebarItems = GetMotionItems(SidebarRoot, "sidebar-motion");

        try
        {
            await _motion.WindowEnterAsync(this, WindowChrome, sidebarItems);
            Title = "AstraCaptioner · 小猫做字幕";
            Dispatcher.UIThread.Post(PrewarmSecondaryPages, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // The window is closing or the first navigation replaced the entrance.
        }
        catch (Exception exception)
        {
            // Keep the window usable and expose unexpected animation failures
            // instead of leaving a transparent, untargetable surface.
            Opacity = 1;
            WindowChrome.RenderTransform = null;
            Title = $"AstraCaptioner · 动画错误：{exception.Message}";
        }
    }

    private void PrewarmSecondaryPages()
    {
        if (_secondaryPagesPrewarmed || _isClosing) return;
        _secondaryPagesPrewarmed = true;

        // Realize the two largest trees in separate idle turns. Measuring only
        // the target avoids forcing two full-window layout passes in one frame.
        PrewarmPage(RecognitionPage);
        Dispatcher.UIThread.Post(() => PrewarmPage(SettingsPage), DispatcherPriority.Background);
    }

    private void PrewarmPage(Control page)
    {
        if (_isClosing || ReferenceEquals(_activePage, page)) return;
        var previousOpacity = page.Opacity;
        var previousHitTest = page.IsHitTestVisible;
        try
        {
            page.Opacity = 0;
            page.IsHitTestVisible = false;
            page.IsVisible = true;
            page.Measure(new Size(
                Math.Max(1, WindowChrome.Bounds.Width),
                Math.Max(1, WindowChrome.Bounds.Height)));
        }
        finally
        {
            page.IsVisible = false;
            page.Opacity = previousOpacity;
            page.IsHitTestVisible = previousHitTest;
        }
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _ = CloseWithAnimationAsync();
    }

    private async Task CloseWithAnimationAsync()
    {
        if (_isClosing) return;
        _isClosing = true;
        _navigation.Cancel();
        _modelTabNavigation.Cancel();
        _workspaceAutoSaveTimer.Stop();
        if (_workspaceHasPendingSave || (_workspaceCues.Count > 0 && _activeProjectId is not null))
        {
            var saved = await SaveWorkspaceSubtitleAsync();
            if (!saved)
            {
                var leaveAnyway = await ConfirmComponentUninstallAsync(
                    "字幕尚未保存",
                    $"AstraCat 无法保存最后的字幕修改。\n\n{ShortMessage(_workspaceLastSaveError ?? "未知保存错误")}\n\n继续退出可能丢失本次修改。",
                    "仍然退出");
                if (!leaveAnyway)
                {
                    _isClosing = false;
                    _workspaceAutoSaveTimer.Start();
                    return;
                }
            }
        }
        _projectSettingsPersistence.Cancel();
        SaveProjects();
        try
        {
            // The OpenGL host must still be alive when the render context is
            // detached. Disposing from Closed is too late and can strand it.
            await _workspacePlayer.DisposeAsync();
        }
        catch (Exception exception)
        {
            Title = $"AstraCaptioner · 播放器关闭失败：{ShortMessage(exception.Message)}";
            _isClosing = false;
            return;
        }
        await _motion.WindowExitAsync(this, WindowChrome);
        _allowClose = true;
        Close();
    }

    private async void Nav_OnClick(object? sender, RoutedEventArgs e)
    {
        WorkspaceVideoHost?.UpdateNativeVisibility(false);
        if (WorkspaceVideoHost != null) WorkspaceVideoHost.IsVisible = false;
        if (ProjectWorkspaceView != null) ProjectWorkspaceView.IsVisible = false;
        if (sender is Button { Tag: string page }) await NavigateTo(page);
    }

    private async void RecognitionNav_OnClick(object? sender, RoutedEventArgs e) => await NavigateTo("recognition");
    private async void ModelsNav_OnClick(object? sender, RoutedEventArgs e) => await NavigateTo("models");
    private async void TaskOverviewNav_OnClick(object? sender, RoutedEventArgs e) => await NavigateTo("tasks");

    private async void NewTask_OnClick(object? sender, RoutedEventArgs e)
    {
        var videoPath = await PickProjectVideoAsync();
        if (string.IsNullOrWhiteSpace(videoPath)) return;

        var suggestedName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrWhiteSpace(suggestedName)) suggestedName = $"字幕项目 {_projects.Count + 1}";
        var name = await PromptForProjectNameAsync(suggestedName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var project = new CaptionProject
        {
            Name = name.Trim(),
            SourceVideoPath = videoPath,
            TranscriptionModelId = _appSettings.DefaultTranscriptionModelId,
            TranscriptionLanguage = _appSettings.DefaultSourceLanguage,
            TranscriptionDevice = _appSettings.DefaultComputeDevice,
            EnableVadFilter = _appSettings.VadFilterDefault,
            VadThreshold = _appSettings.VadThreshold,
            EnableWordTimestamps = _appSettings.WordTimestampsDefault,
            EnableSubtitleProcessing = true,
            EnableLlmSegmentation = true,
            EnglishWordLimit = _appSettings.MaxWordsEnglishDefault > 0 ? _appSettings.MaxWordsEnglishDefault : 12,
            EnableSubtitleProofreading = false,
            EnableWebTerminologyResearch = false,
            CorrectSubtitles = false,
            ReflectTranslation = true,
            UpdatedAt = DateTimeOffset.Now
        };
        _projects.Insert(0, project);
        EnsureProjectDirectory(project.Id);
        SaveProjects();
        RebuildProjectSidebar();
        await OpenProjectAsync(project.Id);
    }

    private async void Project_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string projectId }) await OpenProjectAsync(projectId);
    }

    private async void Settings_OnClick(object? sender, RoutedEventArgs e) => await NavigateTo("settings");

    private static Dictionary<string, TranslationProviderProfile> CreateDefaultTranslationProfiles() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek"] = new TranslationProviderProfile
            {
                DisplayName = "深度求索", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "openai-responses"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.deepseek.com",
                    ["openai-responses"] = "https://api.deepseek.com"
                },
                BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat",
                Models = ["deepseek-chat", "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-reasoner"],
                RegistryOrder = 0, SystemPrompt = DefaultTranslationPrompt
            },
            ["qwen"] = new TranslationProviderProfile
            {
                DisplayName = "通义千问", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "openai-responses"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                    ["openai-responses"] = "https://dashscope.aliyuncs.com/compatible-mode/v1"
                },
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Model = "qwen-max",
                Models = ["qwen-max", "qwen-plus", "qwen-turbo", "qwen3-max", "qwen3-7-plus", "qwen3-6-plus", "qwen3-5-plus", "qwen3.5-flash", "qwq-plus"],
                RegistryOrder = 1, SystemPrompt = DefaultTranslationPrompt
            },
            ["bailian"] = new TranslationProviderProfile
            {
                DisplayName = "阿里云百炼", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "openai-responses"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                    ["openai-responses"] = "https://dashscope.aliyuncs.com/compatible-mode/v1"
                },
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Model = "qwen-plus",
                Models = ["qwen-plus", "qwen-max", "qwen-turbo", "deepseek-r1", "deepseek-v3"],
                RegistryOrder = 2, SystemPrompt = DefaultTranslationPrompt
            },
            ["siliconflow"] = new TranslationProviderProfile
            {
                DisplayName = "硅基流动", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.siliconflow.cn/v1"
                },
                BaseUrl = "https://api.siliconflow.cn/v1",
                Model = "deepseek-ai/DeepSeek-V3",
                Models = ["deepseek-ai/DeepSeek-V3", "deepseek-ai/DeepSeek-R1", "Qwen/Qwen2.5-72B-Instruct", "THUDM/glm-4-9b-chat"],
                RegistryOrder = 3, SystemPrompt = DefaultTranslationPrompt
            },
            ["zhipu"] = new TranslationProviderProfile
            {
                DisplayName = "智谱清言", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://open.bigmodel.cn/api/paas/v4"
                },
                BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                Model = "glm-4-flash",
                Models = ["glm-4-flash", "glm-4-plus", "glm-4-air", "glm-4-long", "glm-4-0520"],
                RegistryOrder = 4, SystemPrompt = DefaultTranslationPrompt
            },
            ["moonshot"] = new TranslationProviderProfile
            {
                DisplayName = "月之暗面", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.moonshot.cn/v1"
                },
                BaseUrl = "https://api.moonshot.cn/v1",
                Model = "moonshot-v1-8k",
                Models = ["moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k", "kimi-latest"],
                RegistryOrder = 5, SystemPrompt = DefaultTranslationPrompt
            },
            ["minimax"] = new TranslationProviderProfile
            {
                DisplayName = "MiniMax", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.minimaxi.com/v1"
                },
                BaseUrl = "https://api.minimaxi.com/v1",
                Model = "MiniMax-Text-01",
                Models = ["MiniMax-Text-01", "abab6.5s-chat", "abab6.5g-chat", "abab6.5t-chat"],
                RegistryOrder = 6, SystemPrompt = DefaultTranslationPrompt
            },
            ["volcengine"] = new TranslationProviderProfile
            {
                DisplayName = "火山引擎", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "openai-responses"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://ark.cn-beijing.volces.com/api/v3",
                    ["openai-responses"] = "https://ark.cn-beijing.volces.com/api/v3"
                },
                BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
                Model = "doubao-pro-32k",
                Models = ["doubao-pro-32k", "doubao-lite-32k", "doubao-pro-128k", "doubao-lite-128k"],
                RegistryOrder = 7, SystemPrompt = DefaultTranslationPrompt
            },
            ["huggingface"] = new TranslationProviderProfile
            {
                DisplayName = "Hugging Face", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://router.huggingface.co/v1"
                },
                BaseUrl = "https://router.huggingface.co/v1",
                Model = "Qwen/Qwen2.5-72B-Instruct",
                Models = ["Qwen/Qwen2.5-72B-Instruct", "meta-llama/Meta-Llama-3.1-70B-Instruct", "mistralai/Mistral-7B-Instruct-v0.3"],
                RegistryOrder = 8, SystemPrompt = DefaultTranslationPrompt
            },
            ["openai"] = new TranslationProviderProfile
            {
                DisplayName = "OpenAI", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "openai-responses"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.openai.com/v1",
                    ["openai-responses"] = "https://api.openai.com/v1"
                },
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4o-mini",
                Models = ["gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "gpt-4.1", "o3-mini", "o1"],
                RegistryOrder = 9, SystemPrompt = DefaultTranslationPrompt
            },
            ["claude"] = new TranslationProviderProfile
            {
                DisplayName = "Anthropic", IsEnabled = true, Protocol = "anthropic-messages",
                SupportedProtocols = ["anthropic-messages", "openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["anthropic-messages"] = "https://api.anthropic.com/v1",
                    ["openai-chat"] = "https://api.anthropic.com/v1"
                },
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-3-5-sonnet-20241022",
                Models = ["claude-3-7-sonnet-20250219", "claude-3-5-sonnet-20241022", "claude-3-5-haiku-20241022", "claude-3-opus-20240229"],
                RegistryOrder = 10, SystemPrompt = DefaultTranslationPrompt
            },
            ["gemini"] = new TranslationProviderProfile
            {
                DisplayName = "Google Gemini", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "google"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://generativelanguage.googleapis.com/v1beta/openai",
                    ["google"] = "https://generativelanguage.googleapis.com"
                },
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
                Model = "gemini-2.5-flash",
                Models = ["gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash", "gemini-1.5-flash", "gemini-1.5-pro"],
                RegistryOrder = 11, SystemPrompt = DefaultTranslationPrompt
            },
            ["ollama"] = new TranslationProviderProfile
            {
                DisplayName = "Ollama (本地)", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "ollama"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "http://localhost:11434/v1",
                    ["ollama"] = "http://localhost:11434"
                },
                BaseUrl = "http://localhost:11434/v1",
                Model = "qwen2.5:7b",
                Models = ["qwen2.5:7b", "qwen2.5:14b", "llama3.1:8b", "deepseek-r1:7b", "deepseek-r1:8b", "mistral:7b"],
                RegistryOrder = 12, SystemPrompt = DefaultTranslationPrompt
            },
            ["groq"] = new TranslationProviderProfile
            {
                DisplayName = "Groq", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.groq.com/openai/v1"
                },
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "llama-3.3-70b-versatile",
                Models = ["llama-3.3-70b-versatile", "llama-3.1-8b-instant", "mixtral-8x7b-32768", "gemma2-9b-it"],
                RegistryOrder = 13, SystemPrompt = DefaultTranslationPrompt
            },
            ["openrouter"] = new TranslationProviderProfile
            {
                DisplayName = "OpenRouter", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://openrouter.ai/api/v1"
                },
                BaseUrl = "https://openrouter.ai/api/v1",
                Model = "deepseek/deepseek-chat",
                Models = ["deepseek/deepseek-chat", "deepseek/deepseek-r1", "anthropic/claude-3.5-sonnet", "openai/gpt-4o-mini", "google/gemini-2.5-flash", "meta-llama/llama-3.3-70b-instruct"],
                RegistryOrder = 14, SystemPrompt = DefaultTranslationPrompt
            },
            ["stepfun"] = new TranslationProviderProfile
            {
                DisplayName = "阶跃星辰", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.stepfun.com/v1"
                },
                BaseUrl = "https://api.stepfun.com/v1",
                Model = "step-2-16k",
                Models = ["step-2-16k", "step-1-8k", "step-1-32k", "step-1-flash", "step-1-128k"],
                RegistryOrder = 15, SystemPrompt = DefaultTranslationPrompt
            },
            ["baichuan"] = new TranslationProviderProfile
            {
                DisplayName = "百川智能", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.baichuan-ai.com/v1"
                },
                BaseUrl = "https://api.baichuan-ai.com/v1",
                Model = "Baichuan4",
                Models = ["Baichuan4", "Baichuan3-Turbo", "Baichuan2-Turbo"],
                RegistryOrder = 16, SystemPrompt = DefaultTranslationPrompt
            },
            ["mistral"] = new TranslationProviderProfile
            {
                DisplayName = "Mistral AI", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.mistral.ai/v1"
                },
                BaseUrl = "https://api.mistral.ai/v1",
                Model = "mistral-large-latest",
                Models = ["mistral-large-latest", "mistral-small-latest", "codestral-latest", "open-mistral-nemo", "pixtral-12b"],
                RegistryOrder = 17, SystemPrompt = DefaultTranslationPrompt
            },
            ["together"] = new TranslationProviderProfile
            {
                DisplayName = "Together AI", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://api.together.ai/v1"
                },
                BaseUrl = "https://api.together.ai/v1",
                Model = "meta-llama/Meta-Llama-3.1-70B-Instruct-Turbo",
                Models = ["meta-llama/Meta-Llama-3.1-70B-Instruct-Turbo", "meta-llama/Llama-3.3-70B-Instruct-Turbo", "deepseek-ai/DeepSeek-V3", "Qwen/Qwen2.5-72B-Instruct-Turbo"],
                RegistryOrder = 18, SystemPrompt = DefaultTranslationPrompt
            },
            ["wenxin"] = new TranslationProviderProfile
            {
                DisplayName = "百度千帆", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://qianfan.baidubce.com/v2"
                },
                BaseUrl = "https://qianfan.baidubce.com/v2",
                Model = "ernie-4.0-turbo-8k",
                Models = ["ernie-4.0-turbo-8k", "ernie-speed-128k", "ernie-lite-8k", "deepseek-v3", "deepseek-r1"],
                RegistryOrder = 19, SystemPrompt = DefaultTranslationPrompt
            },
            ["xinghuo"] = new TranslationProviderProfile
            {
                DisplayName = "讯飞星火", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://spark-api-open.xf-yun.com/v1"
                },
                BaseUrl = "https://spark-api-open.xf-yun.com/v1",
                Model = "generalv3.5",
                Models = ["generalv3.5", "4.0Ultra", "generalv3", "general"],
                RegistryOrder = 20, SystemPrompt = DefaultTranslationPrompt
            },
            ["doubao"] = new TranslationProviderProfile
            {
                DisplayName = "豆包", IsEnabled = true, Protocol = "openai-chat",
                SupportedProtocols = ["openai-chat", "openai-responses"],
                EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai-chat"] = "https://ark.cn-beijing.volces.com/api/v3",
                    ["openai-responses"] = "https://ark.cn-beijing.volces.com/api/v3"
                },
                BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
                Model = "doubao-pro-32k",
                Models = ["doubao-pro-32k", "doubao-lite-32k", "doubao-pro-128k", "doubao-lite-128k"],
                RegistryOrder = 21, SystemPrompt = DefaultTranslationPrompt
            }
        };

    private string TranslationSettingsPath =>
        Path.Combine(_deployment.RuntimeRoot, "config", "translation-models.json");

    private void TranslationProvider_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string provider }) SwitchTranslationProvider(provider);
    }

    private void TranslationProviderRow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string provider } row ||
            !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        SwitchTranslationProvider(provider);
    }

    private void SwitchTranslationProvider(string provider)
    {
        if (string.Equals(provider, _activeTranslationProvider, StringComparison.OrdinalIgnoreCase)) return;

        CaptureTranslationProfile();
        ApplyTranslationProvider(provider);
        TranslationSaveStatusText.Text = "已切换服务，当前输入仍保留在各自配置中";
        TranslationSaveStatusText.Foreground = Brush.Parse("#8A919C");
    }

    private void ApplyTranslationProvider(string provider)
    {
        if (!_translationProfiles.TryGetValue(provider, out var profile)) return;

        _loadingTranslationProfile = true;
        try
        {
            _activeTranslationProvider = provider;
            foreach (var pair in _translationProviderButtons)
                pair.Value.Classes.Set("selected", pair.Key.Equals(provider, StringComparison.OrdinalIgnoreCase));
            RebuildTranslationProtocolOptions(profile);
            SelectTranslationProtocol(profile.Protocol);
            TranslationProtocolCombo.IsEnabled = profile.SupportedProtocols.Count > 1;
            TranslationBaseUrlBox.Text = profile.EndpointBaseUrls.GetValueOrDefault(profile.Protocol) ?? profile.BaseUrl;
            TranslationApiKeyBox.Text = profile.ApiKey;
            TranslationModelBox.Text = profile.Model;
            TranslationPromptBox.Text = profile.SystemPrompt;
            TranslationProviderEnableToggle.IsChecked = profile.IsEnabled;
            TranslationApiKeyStateText.Text = string.IsNullOrWhiteSpace(profile.ApiKey)
                ? "尚未填写 · 仅保存在本机"
                : "已填写 · 仅保存在本机";
            TranslationApiKeyBox.PasswordChar = '●';
            TranslationToggleKeyIcon.Kind = Material.Icons.MaterialIconKind.EyeOutline;
            TranslationProviderTitleText.Text = string.IsNullOrWhiteSpace(profile.DisplayName) ? provider : profile.DisplayName;
            RefreshTranslationModelList();
        }
        finally
        {
            _loadingTranslationProfile = false;
        }
    }

    private void SelectTranslationProtocol(string protocol)
    {
        var item = TranslationProtocolCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, protocol, StringComparison.OrdinalIgnoreCase));
        TranslationProtocolCombo.SelectedItem = item ?? TranslationProtocolCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void TranslationProtocol_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingTranslationProfile || !_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile) ||
            TranslationProtocolCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string nextProtocol) return;
        profile.EndpointBaseUrls[profile.Protocol] = TranslationBaseUrlBox.Text?.Trim() ?? profile.BaseUrl;
        profile.Protocol = nextProtocol;
        profile.BaseUrl = profile.EndpointBaseUrls.GetValueOrDefault(nextProtocol) ?? profile.BaseUrl;
        TranslationBaseUrlBox.Text = profile.BaseUrl;
    }

    private void RebuildTranslationProtocolOptions(TranslationProviderProfile profile)
    {
        var protocols = profile.SupportedProtocols.Count > 0
            ? profile.SupportedProtocols
            : [profile.Protocol];
        TranslationProtocolCombo.Items.Clear();
        foreach (var protocol in protocols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TranslationProtocolCombo.Items.Add(new ComboBoxItem
            {
                Tag = protocol,
                Content = protocol switch
                {
                    "anthropic-messages" or "anthropic" => "Anthropic Messages",
                    "google" => "Google GenerateContent",
                    "ollama" => "Ollama Chat",
                    "openai-chat" => "OpenAI Chat Completions",
                    "openai-responses" => "OpenAI Responses",
                    _ => protocol
                }
            });
        }
    }

    private void CaptureTranslationProfile()
    {
        if (_loadingTranslationProfile || !_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile)) return;

        profile.Protocol = (TranslationProtocolCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? profile.Protocol;
        profile.BaseUrl = TranslationBaseUrlBox.Text?.Trim() ?? string.Empty;
        profile.EndpointBaseUrls[profile.Protocol] = profile.BaseUrl;
        profile.ApiKey = TranslationApiKeyBox.Text?.Trim() ?? string.Empty;
        profile.Model = TranslationModelBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(profile.Model) && !profile.Models.Contains(profile.Model, StringComparer.OrdinalIgnoreCase))
            profile.Models.Add(profile.Model);
        profile.SystemPrompt = string.IsNullOrWhiteSpace(TranslationPromptBox.Text)
            ? DefaultTranslationPrompt
            : TranslationPromptBox.Text.Trim();
    }

    private void TranslationToggleKey_OnClick(object? sender, RoutedEventArgs e)
    {
        var hidden = TranslationApiKeyBox.PasswordChar != '\0';
        TranslationApiKeyBox.PasswordChar = hidden ? '\0' : '●';
        TranslationToggleKeyIcon.Kind = hidden
            ? Material.Icons.MaterialIconKind.EyeOffOutline
            : Material.Icons.MaterialIconKind.EyeOutline;
        TranslationApiKeyBox.Focus();
        TranslationApiKeyBox.CaretIndex = TranslationApiKeyBox.Text?.Length ?? 0;
    }

    private async void TranslationApiOptions_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_translationApiDrawerOpen || _translationApiDrawerAnimating ||
            !_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile)) return;
        CaptureTranslationProfile();
        TranslationCacheTokenThresholdBox.Text = profile.CacheTokenThreshold.ToString();
        TranslationCacheLastNBox.Text = profile.CacheLastNMessages.ToString();
        TranslationCacheSystemToggle.IsChecked = profile.CacheSystemMessage;
        TranslationDeveloperRoleRow.IsVisible = profile.Protocol is "openai-chat" or "openai-responses";
        TranslationStreamOptionsRow.IsVisible = profile.Protocol == "openai-chat";
        TranslationReasoningSummaryRow.IsVisible = profile.Protocol == "openai-responses";
        TranslationDeveloperRoleToggle.IsChecked = profile.DeveloperRole;
        TranslationStreamOptionsToggle.IsChecked = profile.StreamOptions;
        TranslationReasoningSummaryToggle.IsChecked = profile.ReasoningSummary;
        _translationApiDrawerOpen = true;
        _translationApiDrawerAnimating = true;
        try
        {
            await _motion.SetTranslationDrawerVisibleAsync(
                TranslationApiDrawerLayer,
                TranslationApiDrawerBackdrop,
                TranslationApiDrawerPanel,
                visible: true,
                _translationApiDrawerAnimation.Token);
            TranslationApiDrawerPanel.Focus();
        }
        finally
        {
            _translationApiDrawerAnimating = false;
        }
    }

    private async void TranslationApiDrawerClose_OnClick(object? sender, RoutedEventArgs e) =>
        await CloseTranslationApiDrawerAsync();

    private async void TranslationApiDrawerBackdrop_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control backdrop || !e.GetCurrentPoint(backdrop).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        await CloseTranslationApiDrawerAsync();
    }

    private async void TranslationApiDrawer_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        await CloseTranslationApiDrawerAsync();
    }

    private async Task CloseTranslationApiDrawerAsync()
    {
        if (!_translationApiDrawerOpen || _translationApiDrawerAnimating) return;
        if (_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile))
        {
            profile.CacheTokenThreshold = DrawerInteger(TranslationCacheTokenThresholdBox.Text, 0, 100000, 1024);
            profile.CacheLastNMessages = DrawerInteger(TranslationCacheLastNBox.Text, 0, 10, 2);
            profile.CacheSystemMessage = TranslationCacheSystemToggle.IsChecked == true;
            profile.DeveloperRole = TranslationDeveloperRoleToggle.IsChecked == true;
            profile.StreamOptions = TranslationStreamOptionsToggle.IsChecked == true;
            profile.ReasoningSummary = TranslationReasoningSummaryToggle.IsChecked == true;
            TranslationCacheTokenThresholdBox.Text = profile.CacheTokenThreshold.ToString();
            TranslationCacheLastNBox.Text = profile.CacheLastNMessages.ToString();
            SaveTranslationSettings();
        }
        _translationApiDrawerOpen = false;
        _translationApiDrawerAnimating = true;
        try
        {
            await _motion.SetTranslationDrawerVisibleAsync(
                TranslationApiDrawerLayer,
                TranslationApiDrawerBackdrop,
                TranslationApiDrawerPanel,
                visible: false,
                _translationApiDrawerAnimation.Token);
            TranslationApiOptionsButton.Focus();
        }
        finally
        {
            _translationApiDrawerAnimating = false;
        }
    }

    private static int DrawerInteger(string? text, int minimum, int maximum, int fallback) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private void TranslationProviderEnable_OnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_loadingTranslationProfile || e.Property != ToggleSwitch.IsCheckedProperty ||
            !_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile)) return;
        profile.IsEnabled = TranslationProviderEnableToggle.IsChecked == true;
        SaveTranslationSettings();
        RebuildProjectProviderOptions();
        RebuildTranslationProviderList();
    }

    private void TranslationSave_OnClick(object? sender, RoutedEventArgs e)
    {
        CaptureTranslationProfile();
        var profile = _translationProfiles[_activeTranslationProvider];

        if (string.IsNullOrWhiteSpace(profile.BaseUrl) ||
            !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            TranslationSaveStatusText.Text = "请输入有效的 HTTP 或 HTTPS API 地址";
            TranslationSaveStatusText.Foreground = Brush.Parse("#E34D59");
            TranslationBaseUrlBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            TranslationSaveStatusText.Text = "请填写请求使用的模型名称";
            TranslationSaveStatusText.Foreground = Brush.Parse("#E34D59");
            TranslationModelBox.Focus();
            return;
        }

        try
        {
            SaveTranslationSettings();
            RebuildProjectProviderOptions();
            RebuildTranslationProviderList();
            TranslationApiKeyStateText.Text = string.IsNullOrWhiteSpace(profile.ApiKey)
                ? "尚未填写 · 仅保存在本机"
                : "已填写 · 仅保存在本机";
            var needsApiKey = ProviderRequiresApiKey(profile);
            TranslationSaveStatusText.Text = needsApiKey && string.IsNullOrWhiteSpace(profile.ApiKey)
                ? "配置已保存；开始翻译前还需要填写 API Key"
                : "配置已保存，下一次字幕翻译任务将使用此服务";
            TranslationSaveStatusText.Foreground = Brush.Parse(needsApiKey && string.IsNullOrWhiteSpace(profile.ApiKey) ? "#D68A19" : "#24956D");
        }
        catch (Exception exception)
        {
            TranslationSaveStatusText.Text = $"保存失败：{exception.Message}";
            TranslationSaveStatusText.Foreground = Brush.Parse("#E34D59");
        }
    }

    private void LoadTranslationSettings()
    {
        try
        {
            _translationProfiles.Clear();
            foreach (var (providerId, profile) in CreateDefaultTranslationProfiles())
                _translationProfiles[providerId] = profile;
            _deletedTranslationProviders.Clear();
            if (!File.Exists(TranslationSettingsPath)) return;
            var settings = JsonSerializer.Deserialize<TranslationModelSettings>(File.ReadAllText(TranslationSettingsPath));
            if (settings?.Profiles is null) return;

            foreach (var provider in settings.DeletedProviders ?? [])
            {
                if (string.IsNullOrWhiteSpace(provider)) continue;
                _deletedTranslationProviders.Add(provider);
                _translationProfiles.Remove(provider);
            }

            foreach (var (provider, saved) in settings.Profiles)
            {
                if (saved is null || _deletedTranslationProviders.Contains(provider)) continue;
                var fallback = _translationProfiles.GetValueOrDefault(provider) ?? new TranslationProviderProfile();
                saved.Protocol = string.IsNullOrWhiteSpace(saved.Protocol) ? fallback.Protocol : saved.Protocol;
                if (provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase) &&
                    saved.Protocol.Equals("openai-responses", StringComparison.OrdinalIgnoreCase))
                    saved.Protocol = "openai-chat";
                if (provider.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
                    (saved.Protocol.Equals("openai-chat", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(saved.Protocol)))
                    saved.Protocol = "anthropic-messages";
                if (provider.Equals("minimax", StringComparison.OrdinalIgnoreCase) &&
                    (saved.BaseUrl.Equals("https://api.minimaxi.chat/v1", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(saved.BaseUrl)))
                {
                    saved.BaseUrl = "https://api.minimaxi.com/v1";
                    saved.EndpointBaseUrls["openai-chat"] = "https://api.minimaxi.com/v1";
                }
                if (provider.Equals("huggingface", StringComparison.OrdinalIgnoreCase) &&
                    (saved.BaseUrl.Equals("https://api-inference.huggingface.co/v1", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(saved.BaseUrl)))
                {
                    saved.BaseUrl = "https://router.huggingface.co/v1";
                    saved.EndpointBaseUrls["openai-chat"] = "https://router.huggingface.co/v1";
                }
                if (provider.Equals("qwen", StringComparison.OrdinalIgnoreCase) &&
                    saved.Protocol.Equals("openai-responses", StringComparison.OrdinalIgnoreCase) &&
                    (saved.Model.Equals("qwen-max", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(saved.Model)))
                {
                    saved.Protocol = "openai-chat";
                }
                saved.BaseUrl = string.IsNullOrWhiteSpace(saved.BaseUrl) ? fallback.BaseUrl : saved.BaseUrl;
                saved.ApiKey ??= string.Empty;
                saved.SupportedProtocols = fallback.SupportedProtocols
                    .Union(saved.SupportedProtocols ?? [], StringComparer.OrdinalIgnoreCase)
                    .ToList();
                saved.EndpointBaseUrls = saved.EndpointBaseUrls is { Count: > 0 }
                    ? new Dictionary<string, string>(saved.EndpointBaseUrls, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(fallback.EndpointBaseUrls, StringComparer.OrdinalIgnoreCase);
                foreach (var (protocol, endpoint) in fallback.EndpointBaseUrls)
                    if (!saved.EndpointBaseUrls.ContainsKey(protocol))
                        saved.EndpointBaseUrls[protocol] = endpoint;
                if (!saved.EndpointBaseUrls.ContainsKey(saved.Protocol)) saved.EndpointBaseUrls[saved.Protocol] = saved.BaseUrl;
                saved.RegistryOrder = fallback.RegistryOrder;
                saved.Model = string.IsNullOrWhiteSpace(saved.Model) ? fallback.Model : saved.Model;
                saved.DisplayName = string.IsNullOrWhiteSpace(saved.DisplayName) || saved.DisplayName == "DeepSeek" || saved.DisplayName == "Qwen / DashScope"
                    ? fallback.DisplayName
                    : saved.DisplayName;
                saved.Models ??= new List<string>(fallback.Models);
                foreach (var fallbackModel in fallback.Models)
                    if (!saved.Models.Contains(fallbackModel, StringComparer.OrdinalIgnoreCase))
                        saved.Models.Add(fallbackModel);
                if (!string.IsNullOrWhiteSpace(saved.Model) && !saved.Models.Contains(saved.Model, StringComparer.OrdinalIgnoreCase))
                    saved.Models.Add(saved.Model);
                saved.SystemPrompt = string.IsNullOrWhiteSpace(saved.SystemPrompt) ? DefaultTranslationPrompt : saved.SystemPrompt;
                _translationProfiles[provider] = saved;
            }

            if (_translationProfiles.ContainsKey(settings.ActiveProvider))
                _activeTranslationProvider = settings.ActiveProvider;
            else if (_translationProfiles.Count > 0)
                _activeTranslationProvider = _translationProfiles
                    .OrderBy(pair => pair.Value.RegistryOrder)
                    .First().Key;
        }
        catch
        {
            // A damaged optional settings file must not stop the main window from opening.
        }
    }

    private void SaveTranslationSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TranslationSettingsPath)!);
        var settings = new TranslationModelSettings
        {
            ActiveProvider = _activeTranslationProvider,
            Profiles = new Dictionary<string, TranslationProviderProfile>(_translationProfiles, StringComparer.OrdinalIgnoreCase),
            DeletedProviders = _deletedTranslationProviders.OrderBy(provider => provider).ToList()
        };
        File.WriteAllText(TranslationSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RebuildProjectProviderOptions()
    {
        if (ProjectProcessingProviderCombo is null || ProjectTranslationProviderCombo is null) return;
        var processingSelection = SelectedComboValue(ProjectProcessingProviderCombo);
        var translationSelection = SelectedComboValue(ProjectTranslationProviderCombo);
        var providers = _translationProfiles
            .Where(pair => pair.Value.IsEnabled)
            .OrderBy(pair => pair.Value.RegistryOrder)
            .ThenBy(pair => pair.Value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        static void Fill(ComboBox combo, IEnumerable<KeyValuePair<string, TranslationProviderProfile>> values,
            string previous)
        {
            combo.Items.Clear();
            foreach (var pair in values)
                combo.Items.Add(new ComboBoxItem { Content = pair.Value.DisplayName, Tag = pair.Key });
            SelectComboText(combo, previous);
            if (combo.SelectedIndex < 0 && combo.ItemCount > 0) combo.SelectedIndex = 0;
        }

        Fill(ProjectProcessingProviderCombo, providers, processingSelection);
        Fill(ProjectTranslationProviderCombo, providers, translationSelection);
    }

    private static bool ProviderRequiresApiKey(TranslationProviderProfile profile)
    {
        if (profile.Protocol.Equals("ollama", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri)) return true;
        return !uri.IsLoopback &&
               !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
               !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderConfigured(TranslationProviderProfile? profile) =>
        profile is { IsEnabled: true } &&
        !string.IsNullOrWhiteSpace(profile.BaseUrl) &&
        !string.IsNullOrWhiteSpace(profile.Model) &&
        (!ProviderRequiresApiKey(profile) || !string.IsNullOrWhiteSpace(profile.ApiKey));

    private async void TranslationProviderSearch_OnChanged(object? sender, TextChangedEventArgs e)
    {
        var previous = _translationProviderSearchDebounce;
        _translationProviderSearchDebounce = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        try
        {
            await Task.Delay(200, _translationProviderSearchDebounce.Token);
            RebuildTranslationProviderList();
        }
        catch (OperationCanceledException) { }
    }

    private void RebuildTranslationProviderList()
    {
        if (TranslationProviderListHost is null) return;
        var query = TranslationProviderSearchBox?.Text?.Trim() ?? string.Empty;
        TranslationProviderListHost.Children.Clear();
        _translationProviderButtons.Clear();
        foreach (var pair in _translationProfiles
                     .Where(pair => string.IsNullOrWhiteSpace(query) ||
                                    pair.Value.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                    pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                    pair.Value.Models.Any(model => model.Contains(query, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(pair => pair.Value.RegistryOrder)
                     .ThenBy(pair => pair.Value.DisplayName))
        {
            var providerId = pair.Key;
            var profile = pair.Value;
            var button = new Border
            {
                Tag = providerId,
                Height = 52,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 2),
                CornerRadius = new CornerRadius(26),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = CreateTranslationProviderIdentity(providerId, profile)
            };
            button.Classes.Add("translationProviderListRow");
            button.Classes.Set("selected", providerId.Equals(_activeTranslationProvider, StringComparison.OrdinalIgnoreCase));
            button.PointerPressed += TranslationProviderRow_OnPointerPressed;
            ToolTip.SetTip(button, "点击切换服务商");
            _translationProviderButtons[providerId] = button;
            TranslationProviderListHost.Children.Add(button);
        }
        TranslationProviderEmptyText.IsVisible = TranslationProviderListHost.Children.Count == 0;
    }

    private static Control GetProviderLogoControl(string providerId, TranslationProviderProfile? profile = null, double width = 26, double height = 26)
    {
        var assetName = providerId.ToLowerInvariant() switch
        {
            "deepseek" => "deepseek.png",
            "qwen" => "qwen.png",
            "bailian" => "bailian.png",
            "siliconflow" or "silicon" => "siliconflow.png",
            "zhipu" or "glm" => "zhipu.png",
            "moonshot" or "kimi" => "moonshot.png",
            "minimax" => "minimax.png",
            "volcengine" => "volcengine.png",
            "doubao" => "doubao.png",
            "huggingface" => "huggingface.png",
            "openai" => "openai.png",
            "claude" or "anthropic" => "claude.png",
            "gemini" or "google" => "gemini.png",
            "ollama" => "ollama.png",
            "groq" => "groq.png",
            "mistral" => "mistral.png",
            "baichuan" => "baichuan.png",
            "stepfun" or "step" => "stepfun.png",
            "together" or "togetherai" => "together.png",
            "openrouter" => "openrouter.png",
            "wenxin" or "baidu" => "wenxin.png",
            "xinghuo" or "spark" => "xinghuo.png",
            _ => null
        };

        var uri = assetName is not null ? new Uri($"avares://AstraCat/Assets/Translation/{assetName}") : null;
        if (uri is not null && AssetLoader.Exists(uri))
        {
            if (!TranslationLogoCache.TryGetValue(uri.AbsoluteUri, out var bitmap))
            {
                using var stream = AssetLoader.Open(uri);
                bitmap = new Bitmap(stream);
                TranslationLogoCache[uri.AbsoluteUri] = bitmap;
            }
            return new Image
            {
                Source = bitmap,
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var text = profile is not null && !string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.DisplayName[..1].ToUpperInvariant()
            : providerId.Length > 0 ? providerId[..1].ToUpperInvariant() : "A";

        return new TextBlock
        {
            Text = text,
            FontSize = width * 0.5,
            FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#4F5969"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Control CreateTranslationProviderIdentity(string providerId, TranslationProviderProfile profile)
    {
        var logoBorder = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brush.Parse("#E5E7EB"),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            BoxShadow = new BoxShadows(BoxShadow.Parse("0 1 3 0 #0D000000")),
            Child = GetProviderLogoControl(providerId, profile, 30, 30)
        };

        var grip = new Material.Icons.Avalonia.MaterialIcon
        {
            Kind = Material.Icons.MaterialIconKind.DotsGrid,
            Width = 16,
            Height = 16,
            Foreground = Brush.Parse("#9CA3AF"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0)
        };

        var displayName = providerId.ToLowerInvariant() switch
        {
            "deepseek" => "深度求索",
            "qwen" => "通义千问",
            "bailian" => "阿里云百炼",
            "siliconflow" => "硅基流动",
            "zhipu" => "智谱清言",
            "moonshot" => "月之暗面",
            "minimax" => "MiniMax",
            "volcengine" => "火山引擎",
            "doubao" => "豆包",
            "huggingface" => "Hugging Face",
            "openai" => "OpenAI",
            "claude" or "anthropic" => "Anthropic",
            "gemini" => "Google Gemini",
            "ollama" => "Ollama (本地)",
            "groq" => "Groq",
            "mistral" => "Mistral AI",
            "stepfun" => "阶跃星辰",
            "baichuan" => "百川智能",
            "openrouter" => "OpenRouter",
            "together" => "Together AI",
            "wenxin" or "baidu" => "百度千帆",
            "xinghuo" or "spark" => "讯飞星火",
            _ => string.IsNullOrWhiteSpace(profile.DisplayName) ? providerId : profile.DisplayName
        };

        var name = new TextBlock
        {
            Text = displayName,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#18181B"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };

        var statusCapsule = CreateTranslationProviderStatusCapsule(profile);
        var identity = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 0);
        identity.Children.Add(name);
        Grid.SetColumn(statusCapsule, 1);
        identity.Children.Add(statusCapsule);

        var moreIcon = new Material.Icons.Avalonia.MaterialIcon
        {
            Kind = Material.Icons.MaterialIconKind.DotsVertical,
            Width = 18,
            Height = 18,
            Foreground = Brush.Parse("#71717A"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var moreButton = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = moreIcon
        };
        moreButton.Classes.Add("translationProviderMoreButton");
        ToolTip.SetTip(moreButton, "更多操作");
        var providerMenu = CreateTranslationProviderMenu(providerId);
        moreButton.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(moreButton).Properties.IsLeftButtonPressed) return;
            args.Handled = true;
            providerMenu.Open(moreButton);
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(grip, 0);
        grid.Children.Add(grip);

        Grid.SetColumn(logoBorder, 1);
        grid.Children.Add(logoBorder);

        Grid.SetColumn(identity, 2);
        grid.Children.Add(identity);

        Grid.SetColumn(moreButton, 3);
        grid.Children.Add(moreButton);

        return grid;
    }

    private ContextMenu CreateTranslationProviderMenu(string providerId)
    {
        var restoreItem = new MenuItem { Header = "恢复默认设置", Tag = providerId };
        restoreItem.Click += TranslationProviderRestoreDefaults_OnClick;
        var deleteItem = new MenuItem { Header = "删除", Tag = providerId };
        deleteItem.Click += TranslationProviderDelete_OnClick;
        return new ContextMenu { Items = { restoreItem, new Separator(), deleteItem } };
    }

    private static TranslationProviderProfile CreateBlankCustomProvider(string displayName, int registryOrder) => new()
    {
        DisplayName = displayName,
        IsEnabled = true,
        Protocol = "openai-chat",
        SupportedProtocols = ["openai-chat", "openai-responses", "anthropic-messages", "google", "ollama"],
        EndpointBaseUrls = new(StringComparer.OrdinalIgnoreCase)
        {
            ["openai-chat"] = string.Empty,
            ["openai-responses"] = string.Empty,
            ["anthropic-messages"] = string.Empty,
            ["google"] = string.Empty,
            ["ollama"] = string.Empty
        },
        RegistryOrder = registryOrder,
        SystemPrompt = DefaultTranslationPrompt
    };

    private async void TranslationProviderAddCustom_OnClick(object? sender, RoutedEventArgs e)
    {
        var displayName = await PromptForProjectNameAsync(string.Empty, "添加自定义供应商", "添加");
        if (string.IsNullOrWhiteSpace(displayName)) return;

        var providerId = $"custom-{Guid.NewGuid():N}";
        var registryOrder = _translationProfiles.Count == 0
            ? 0
            : _translationProfiles.Values.Max(profile => profile.RegistryOrder) + 1;
        _translationProfiles[providerId] = CreateBlankCustomProvider(displayName.Trim(), registryOrder);
        _activeTranslationProvider = providerId;
        SaveTranslationSettings();
        RebuildTranslationProviderList();
        RebuildProjectProviderOptions();
        ApplyTranslationProvider(providerId);
    }

    private async void TranslationProviderRestoreDefaults_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string providerId }) return;
        var defaults = CreateDefaultTranslationProfiles();
        var displayName = _translationProfiles.TryGetValue(providerId, out var current) &&
                          !string.IsNullOrWhiteSpace(current.DisplayName)
            ? current.DisplayName
            : providerId;
        var defaultProfile = defaults.TryGetValue(providerId, out var builtInDefault)
            ? builtInDefault
            : CreateBlankCustomProvider(displayName, current?.RegistryOrder ?? int.MaxValue);
        if (!await ConfirmComponentUninstallAsync(
                $"恢复“{displayName}”默认设置？",
                "接口地址、协议、模型列表、提示词和 API 密钥都会恢复为初始状态。",
                "确认恢复")) return;

        _translationProfiles[providerId] = defaultProfile;
        _deletedTranslationProviders.Remove(providerId);
        _activeTranslationProvider = providerId;
        SaveTranslationSettings();
        RebuildTranslationProviderList();
        RebuildProjectProviderOptions();
        ApplyTranslationProvider(providerId);
    }

    private async void TranslationProviderDelete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string providerId } ||
            !_translationProfiles.TryGetValue(providerId, out var profile)) return;
        if (_translationProfiles.Count <= 1)
        {
            await ConfirmComponentUninstallAsync(
                "无法删除",
                "至少需要保留一个翻译模型服务商。",
                "知道了");
            return;
        }
        if (!await ConfirmComponentUninstallAsync(
                $"删除“{profile.DisplayName}”？",
                "该服务商的接口地址、API 密钥和模型配置将被删除；使用它的项目会切换到其他可用服务商。",
                "确认删除")) return;

        _translationProfiles.Remove(providerId);
        _deletedTranslationProviders.Add(providerId);
        var fallback = _translationProfiles
            .OrderBy(pair => pair.Value.RegistryOrder)
            .ThenBy(pair => pair.Value.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .First().Key;
        foreach (var project in _projects)
        {
            if (string.Equals(project.TranslationProvider, providerId, StringComparison.OrdinalIgnoreCase))
                project.TranslationProvider = fallback;
            if (string.Equals(project.SubtitleProcessingProvider, providerId, StringComparison.OrdinalIgnoreCase))
                project.SubtitleProcessingProvider = fallback;
        }
        _activeTranslationProvider = string.Equals(_activeTranslationProvider, providerId, StringComparison.OrdinalIgnoreCase)
            ? fallback
            : _activeTranslationProvider;
        SaveProjects();
        SaveTranslationSettings();
        RebuildTranslationProviderList();
        RebuildProjectProviderOptions();
        ApplyTranslationProvider(_activeTranslationProvider);
        if (_activeProjectId is { } activeProjectId &&
            _projects.FirstOrDefault(project => project.Id == activeProjectId) is { } activeProject)
        {
            RefreshProjectProcessing(activeProject);
            RefreshProjectTranslation(activeProject);
        }
    }

    private static Border CreateTranslationProviderStatusCapsule(TranslationProviderProfile profile)
    {
        var (text, background, foreground, dot) = !profile.IsEnabled
            ? ("已禁用", "#FFF0F0", "#C93F49", "#E2555F")
            : IsProviderConfigured(profile)
                ? ("已开启", "#EAF8EF", "#27854A", "#35A85B")
                : ("未配置", "#F1F2F4", "#737B86", "#A2A8B0");

        return new Border
        {
            Height = 22,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(11),
            Background = Brush.Parse(background),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = Brush.Parse(dot),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 10.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush.Parse(foreground),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
    }

    private async void TranslationFetchModels_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_translationModelFetchRunning) return;
        CaptureTranslationProfile();
        var profile = _translationProfiles[_activeTranslationProvider];
        _translationModelFetchRunning = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsEndpoint(profile));
            ApplyProviderAuthentication(request, profile);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await TranslationHttpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"接口返回 {(int)response.StatusCode}：{ShortMessage(body)}");
            using var json = JsonDocument.Parse(body);
            var models = new List<string>();
            var root = json.RootElement;
            var source = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data :
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var modelData) ? modelData : root;
            if (source.ValueKind == JsonValueKind.Array)
                foreach (var item in source.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String ? item.GetString() :
                        item.TryGetProperty("id", out var idProperty) ? idProperty.GetString() :
                        item.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() :
                        item.TryGetProperty("model", out var modelProperty) ? modelProperty.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        id = id.Trim();
                        if (profile.Protocol.Equals("google", StringComparison.OrdinalIgnoreCase) &&
                            id.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                            id = id["models/".Length..];
                        models.Add(id);
                    }
                }
            profile.Models = models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
            if (profile.Models.Count == 0) throw new InvalidDataException("接口没有返回可用模型");
            if (string.IsNullOrWhiteSpace(profile.Model) || !profile.Models.Contains(profile.Model, StringComparer.OrdinalIgnoreCase))
                profile.Model = profile.Models[0];
            TranslationModelBox.Text = profile.Model;
            SaveTranslationSettings();
            RefreshTranslationModelList();
            RebuildTranslationProviderList();
            TranslationSaveStatusText.Text = $"已获取 {profile.Models.Count} 个模型";
            TranslationSaveStatusText.Foreground = Brush.Parse("#24956D");
        }
        catch (Exception exception)
        {
            TranslationSaveStatusText.Text = $"获取失败：{ShortMessage(exception.Message)}";
            TranslationSaveStatusText.Foreground = Brush.Parse("#C94444");
        }
        finally
        {
            _translationModelFetchRunning = false;
        }
    }

    private async void TranslationAddModel_OnClick(object? sender, RoutedEventArgs e)
    {
        CaptureTranslationProfile();
        var model = await PromptForProjectNameAsync("model-id", "手动添加模型", "添加");
        if (string.IsNullOrWhiteSpace(model)) return;
        var profile = _translationProfiles[_activeTranslationProvider];
        if (!profile.Models.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase)) profile.Models.Add(model.Trim());
        profile.Model = model.Trim();
        TranslationModelBox.Text = profile.Model;
        SaveTranslationSettings();
        RefreshTranslationModelList();
    }

    private void RefreshTranslationModelList()
    {
        if (TranslationModelListHost is null || !_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile)) return;
        TranslationModelListHost.Children.Clear();
        if (!string.IsNullOrWhiteSpace(profile.Model) && !profile.Models.Contains(profile.Model, StringComparer.OrdinalIgnoreCase))
            profile.Models.Add(profile.Model);
        foreach (var model in profile.Models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value))
        {
            var isSelected = model.Equals(profile.Model, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Tag = model,
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 0),
                CornerRadius = new CornerRadius(10),
                Background = isSelected ? Brush.Parse("#F4F5F7") : Brushes.Transparent
            };
            button.Classes.Add("translationModelRow");
            button.Classes.Add("noButtonMotion");
            button.Classes.Set("selected", isSelected);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*,Auto") };

            var badge = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.White,
                BorderBrush = Brush.Parse("#E5E7EB"),
                BorderThickness = new Thickness(1),
                BoxShadow = new BoxShadows(BoxShadow.Parse("0 1 2 0 #0A000000")),
                Child = GetProviderLogoControl(_activeTranslationProvider, profile, 22, 22)
            };
            grid.Children.Add(badge);

            var label = new TextBlock
            {
                Text = model,
                FontSize = 13.5,
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = Brush.Parse("#18181B"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);

            if (isSelected)
            {
                var check = new Material.Icons.Avalonia.MaterialIcon
                {
                    Kind = Material.Icons.MaterialIconKind.Check,
                    Width = 18,
                    Height = 18,
                    Foreground = Brush.Parse("#2563EB"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(check, 3);
                grid.Children.Add(check);
            }

            button.Content = grid;
            button.ContextMenu = BuildTranslationModelContextMenu(model);
            ToolTip.SetTip(button, "点击设为当前模型，右键管理");
            button.Click += (_, _) =>
            {
                CaptureTranslationProfile();
                profile.Model = model;
                TranslationModelBox.Text = model;
                SaveTranslationSettings();
                RefreshTranslationModelList();
            };
            TranslationModelListHost.Children.Add(button);
        }
        TranslationModelEmptyState.IsVisible = TranslationModelListHost.Children.Count == 0;
        TranslationModelCountText.Text = $"{TranslationModelListHost.Children.Count} 个模型";
    }

    private ContextMenu BuildTranslationModelContextMenu(string model)
    {
        var select = CreateProjectMenuItem("设为当前模型", "M9 16.17L4.83 12L3.41 13.41L9 19L21 7L19.59 5.59L9 16.17Z");
        select.Click += (_, _) => SelectTranslationModel(model);
        var remove = CreateProjectMenuItem("从列表移除", "M6 19C6 20.1 6.9 21 8 21H16C17.1 21 18 20.1 18 19V7H6V19ZM8 9H16V19H8V9ZM15.5 4L14.5 3H9.5L8.5 4H5V6H19V4H15.5Z", destructive: true);
        remove.Click += (_, _) => RemoveTranslationModel(model);
        return new ContextMenu { MinWidth = 180, FontSize = 12.5, ItemsSource = new object[] { select, new Separator(), remove } };
    }

    private void SelectTranslationModel(string model)
    {
        CaptureTranslationProfile();
        if (!_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile)) return;
        profile.Model = model;
        TranslationModelBox.Text = model;
        SaveTranslationSettings();
        RefreshTranslationModelList();
    }

    private void RemoveTranslationModel(string model)
    {
        if (!_translationProfiles.TryGetValue(_activeTranslationProvider, out var profile)) return;
        profile.Models.RemoveAll(value => value.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (profile.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            profile.Model = profile.Models.FirstOrDefault() ?? string.Empty;
            TranslationModelBox.Text = profile.Model;
        }
        SaveTranslationSettings();
        RefreshTranslationModelList();
    }

    private static string ModelsEndpoint(TranslationProviderProfile profile)
    {
        var value = profile.BaseUrl.Trim().TrimEnd('/');
        if (profile.Protocol.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var suffix in new[] { "/v1beta", "/v1" })
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value = value[..^suffix.Length];
            return $"{value}/v1beta/models?key={Uri.EscapeDataString(profile.ApiKey)}";
        }
        if (profile.Protocol.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            return value.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase) ? value : value + "/api/tags";
        foreach (var suffix in new[] { "/v1/chat/completions", "/chat/completions", "/v1/responses", "/responses", "/v1" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length];
                break;
            }
        return NormalizeOpenAiCompatibleBaseUrl(value) + "/models";
    }

    private async Task OpenProjectAsync(string projectId)
    {
        var project = _projects.FirstOrDefault(item =>
            string.Equals(item.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null) return;

        if (_activeProjectId is not null && _projectTranslationCacheTimer.IsEnabled)
        {
            _projectTranslationCacheTimer.Stop();
            SaveActiveProjectTranslationCache();
        }
        _activeProjectId = project.Id;
        ProjectTitleText.Text = project.Name;
        RefreshProjectWorkflow(project);
        RefreshProjectTranscriptionModelSelection(project);
        RefreshProjectTranscriptionSettings(project);
        RefreshProjectTranslation(project);
        RefreshProjectProcessing(project);
        SetProjectSectionImmediate("flow");
        await NavigateTo("project");
    }

    private void ConfigureProjectTranscriptionLayout(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var lower = modelId.ToLowerInvariant();

        if (ProjectTranscriptionDeviceRow != null) ProjectTranscriptionDeviceRow.IsVisible = true;
        if (ProjectTranscriptionPrecisionRow != null) ProjectTranscriptionPrecisionRow.IsVisible = true;

        var isWhisper = lower.StartsWith("whisper");
        var isQwen = lower.StartsWith("qwen");
        var isParakeet = lower.StartsWith("nvidia") || lower.StartsWith("parakeet");
        var isVad = isWhisper && ProjectTranscriptionVadToggle?.IsChecked == true;

        // Parakeet detects its 25 supported European languages automatically.
        // Only expose controls that the current local worker actually consumes.
        if (ProjectTranscriptionLanguageRow != null) ProjectTranscriptionLanguageRow.IsVisible = isWhisper || isQwen;
        if (ProjectTranscriptionTimestampRow != null) ProjectTranscriptionTimestampRow.IsVisible = isWhisper || isParakeet;
        if (ProjectTranscriptionVadRow != null) ProjectTranscriptionVadRow.IsVisible = isWhisper;
        if (ProjectTranscriptionVadThresholdRow != null) ProjectTranscriptionVadThresholdRow.IsVisible = isVad;
        if (ProjectTranscriptionVadMinSilenceRow != null) ProjectTranscriptionVadMinSilenceRow.IsVisible = isVad;
        if (ProjectTranscriptionVadSpeechPadRow != null) ProjectTranscriptionVadSpeechPadRow.IsVisible = isVad;

        if (ProjectTranscriptionBeamRow != null) ProjectTranscriptionBeamRow.IsVisible = isWhisper;
        if (ProjectTranscriptionTemperatureRow != null) ProjectTranscriptionTemperatureRow.IsVisible = isWhisper;
        if (ProjectTranscriptionMaxTokensRow != null) ProjectTranscriptionMaxTokensRow.IsVisible = isQwen;
        if (ProjectTranscriptionHotwordsRow != null) ProjectTranscriptionHotwordsRow.IsVisible = isWhisper || isQwen;
        if (ProjectTranscriptionDiarizationRow != null) ProjectTranscriptionDiarizationRow.IsVisible = false;
        if (ProjectTranscriptionSpeakerCountRow != null) ProjectTranscriptionSpeakerCountRow.IsVisible = false;
        if (ProjectTranscriptionEmotionRow != null) ProjectTranscriptionEmotionRow.IsVisible = false;
        if (ProjectTranscriptionAudioEventRow != null) ProjectTranscriptionAudioEventRow.IsVisible = false;
        if (ProjectTranscriptionChunkSecondsRow != null) ProjectTranscriptionChunkSecondsRow.IsVisible = isWhisper || isQwen || isParakeet;
    }

    private void ConfigureProjectLanguageOptions(string? modelId, string? selectedLanguage)
    {
        ProjectTranscriptionLanguageCombo.Items.Clear();
        ProjectTranscriptionLanguageCombo.Items.Add(new ComboBoxItem { Content = "自动检测", Tag = string.Empty });

        var specification = LanguageSpecificationForModel(modelId);
        foreach (var entry in specification.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|', 2);
            if (parts.Length != 2) continue;
            ProjectTranscriptionLanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{parts[1]}  ({parts[0]})",
                Tag = parts[0]
            });
        }

        SelectComboText(ProjectTranscriptionLanguageCombo, selectedLanguage);
        if (ProjectTranscriptionLanguageCombo.SelectedIndex < 0)
            ProjectTranscriptionLanguageCombo.SelectedIndex = 0;
    }

    private void ConfigureProjectPrecisionOptions(string? modelId, string? selectedPrecision)
    {
        ProjectTranscriptionPrecisionCombo.Items.Clear();
        var usesTorch = modelId?.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase) == true ||
                        modelId?.StartsWith("nvidia-", StringComparison.OrdinalIgnoreCase) == true;
        var options = usesTorch
            ? new[] { "自动", "BFloat16", "Float16", "Float32" }
            : new[] { "自动", "Float16", "Int8", "Int8 Float16", "Float32" };
        foreach (var option in options)
            ProjectTranscriptionPrecisionCombo.Items.Add(new ComboBoxItem { Content = option });

        SelectComboText(ProjectTranscriptionPrecisionCombo, selectedPrecision);
        if (ProjectTranscriptionPrecisionCombo.SelectedIndex < 0)
            ProjectTranscriptionPrecisionCombo.SelectedIndex = 0;
    }

    private void RefreshProjectTranscriptionSettings(CaptionProject project)
    {
        _loadingProjectTranscription = true;
        try
        {
            ConfigureProjectLanguageOptions(project.TranscriptionModelId, project.TranscriptionLanguage);
            ConfigureProjectPrecisionOptions(project.TranscriptionModelId, project.TranscriptionPrecision);
            SelectComboText(ProjectTranscriptionDeviceCombo, project.TranscriptionDevice);
            SelectComboText(ProjectTranscriptionSpeakerCountCombo, project.TranscriptionSpeakerCount);

            if (ProjectTranscriptionBeamSlider != null)
            {
                ProjectTranscriptionBeamSlider.Value = project.TranscriptionBeamSize;
                if (ProjectTranscriptionBeamText != null) ProjectTranscriptionBeamText.Text = project.TranscriptionBeamSize.ToString(CultureInfo.InvariantCulture);
            }
            if (ProjectTranscriptionTemperatureSlider != null)
            {
                ProjectTranscriptionTemperatureSlider.Value = project.TranscriptionTemperature;
                if (ProjectTranscriptionTemperatureText != null) ProjectTranscriptionTemperatureText.Text = project.TranscriptionTemperature.ToString("F1", CultureInfo.InvariantCulture);
            }
            if (ProjectTranscriptionVadToggle != null)
                ProjectTranscriptionVadToggle.IsChecked = project.EnableVadFilter;
            if (ProjectTranscriptionVadThresholdSlider != null)
            {
                ProjectTranscriptionVadThresholdSlider.Value = project.VadThreshold;
                if (ProjectTranscriptionVadThresholdText != null) ProjectTranscriptionVadThresholdText.Text = project.VadThreshold.ToString("F2", CultureInfo.InvariantCulture);
            }
            if (ProjectTranscriptionVadMinSilenceSlider != null)
            {
                ProjectTranscriptionVadMinSilenceSlider.Value = project.VadMinSilence;
                if (ProjectTranscriptionVadMinSilenceText != null) ProjectTranscriptionVadMinSilenceText.Text = $"{project.VadMinSilence}ms";
            }
            if (ProjectTranscriptionVadSpeechPadSlider != null)
            {
                ProjectTranscriptionVadSpeechPadSlider.Value = project.VadSpeechPad;
                if (ProjectTranscriptionVadSpeechPadText != null) ProjectTranscriptionVadSpeechPadText.Text = $"{project.VadSpeechPad}ms";
            }
            if (ProjectTranscriptionMaxTokensSlider != null)
            {
                ProjectTranscriptionMaxTokensSlider.Value = project.TranscriptionMaxTokens;
                if (ProjectTranscriptionMaxTokensText != null) ProjectTranscriptionMaxTokensText.Text = project.TranscriptionMaxTokens.ToString(CultureInfo.InvariantCulture);
            }
            if (ProjectTranscriptionWordTimestampsToggle != null)
                ProjectTranscriptionWordTimestampsToggle.IsChecked = project.EnableWordTimestamps;
            if (ProjectTranscriptionHotwordsBox != null)
                ProjectTranscriptionHotwordsBox.Text = project.TranscriptionHotwords;
            if (ProjectTranscriptionDiarizationToggle != null)
                ProjectTranscriptionDiarizationToggle.IsChecked = project.EnableDiarization;
            if (ProjectTranscriptionEmotionToggle != null)
                ProjectTranscriptionEmotionToggle.IsChecked = project.EnableEmotion;
            if (ProjectTranscriptionAudioEventToggle != null)
                ProjectTranscriptionAudioEventToggle.IsChecked = project.EnableAudioEvent;
            if (ProjectTranscriptionChunkSecondsSlider != null)
            {
                ProjectTranscriptionChunkSecondsSlider.Value = project.TranscriptionChunkSeconds;
                if (ProjectTranscriptionChunkSecondsText != null) ProjectTranscriptionChunkSecondsText.Text = $"{project.TranscriptionChunkSeconds}s";
            }

            ConfigureProjectTranscriptionLayout(project.TranscriptionModelId);
        }
        finally
        {
            _loadingProjectTranscription = false;
        }
    }

    private void SaveProjectTranscriptionSettings(bool persistImmediately = true)
    {
        if (_loadingProjectTranscription || _activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        project.TranscriptionDevice = SelectedComboText(ProjectTranscriptionDeviceCombo);
        project.TranscriptionLanguage = SelectedComboValue(ProjectTranscriptionLanguageCombo);
        project.TranscriptionPrecision = SelectedComboText(ProjectTranscriptionPrecisionCombo);
        project.TranscriptionSpeakerCount = SelectedComboText(ProjectTranscriptionSpeakerCountCombo);
        if (ProjectTranscriptionBeamSlider != null)
            project.TranscriptionBeamSize = (int)Math.Round(ProjectTranscriptionBeamSlider.Value);
        if (ProjectTranscriptionTemperatureSlider != null)
            project.TranscriptionTemperature = Math.Round(ProjectTranscriptionTemperatureSlider.Value, 1);
        if (ProjectTranscriptionVadToggle != null)
            project.EnableVadFilter = ProjectTranscriptionVadToggle.IsChecked == true;
        if (ProjectTranscriptionVadThresholdSlider != null)
            project.VadThreshold = Math.Round(ProjectTranscriptionVadThresholdSlider.Value, 2);
        if (ProjectTranscriptionVadMinSilenceSlider != null)
            project.VadMinSilence = (int)Math.Round(ProjectTranscriptionVadMinSilenceSlider.Value);
        if (ProjectTranscriptionVadSpeechPadSlider != null)
            project.VadSpeechPad = (int)Math.Round(ProjectTranscriptionVadSpeechPadSlider.Value);
        if (ProjectTranscriptionMaxTokensSlider != null)
            project.TranscriptionMaxTokens = (int)Math.Round(ProjectTranscriptionMaxTokensSlider.Value);
        if (ProjectTranscriptionWordTimestampsToggle != null)
            project.EnableWordTimestamps = ProjectTranscriptionWordTimestampsToggle.IsChecked == true;
        if (ProjectTranscriptionHotwordsBox != null)
            project.TranscriptionHotwords = ProjectTranscriptionHotwordsBox.Text?.Trim() ?? string.Empty;
        if (ProjectTranscriptionDiarizationToggle != null)
            project.EnableDiarization = ProjectTranscriptionDiarizationToggle.IsChecked == true;
        if (ProjectTranscriptionEmotionToggle != null)
            project.EnableEmotion = ProjectTranscriptionEmotionToggle.IsChecked == true;
        if (ProjectTranscriptionAudioEventToggle != null)
            project.EnableAudioEvent = ProjectTranscriptionAudioEventToggle.IsChecked == true;
        if (ProjectTranscriptionChunkSecondsSlider != null)
            project.TranscriptionChunkSeconds = (int)Math.Round(ProjectTranscriptionChunkSecondsSlider.Value);

        project.UpdatedAt = DateTimeOffset.Now;
        if (persistImmediately) SaveProjects();
    }

    private void ProjectTranscriptionSettings_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProjectTranscription) return;
        SaveProjectTranscriptionSettings();
    }

    private void ProjectTranscriptionToggle_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_loadingProjectTranscription || e.Property != ToggleSwitch.IsCheckedProperty) return;
        if (sender == ProjectTranscriptionVadToggle)
        {
            var isVad = ProjectTranscriptionVadToggle.IsChecked == true;
            if (ProjectTranscriptionVadThresholdRow != null) ProjectTranscriptionVadThresholdRow.IsVisible = isVad;
            if (ProjectTranscriptionVadMinSilenceRow != null) ProjectTranscriptionVadMinSilenceRow.IsVisible = isVad;
            if (ProjectTranscriptionVadSpeechPadRow != null) ProjectTranscriptionVadSpeechPadRow.IsVisible = isVad;
        }
        else if (sender == ProjectTranscriptionDiarizationToggle)
        {
            if (ProjectTranscriptionSpeakerCountRow != null)
                ProjectTranscriptionSpeakerCountRow.IsVisible = ProjectTranscriptionDiarizationToggle.IsChecked == true;
        }
        SaveProjectTranscriptionSettings();
    }

    private void ProjectTranscriptionSlider_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty) return;

        if (sender == ProjectTranscriptionBeamSlider && ProjectTranscriptionBeamText != null)
            ProjectTranscriptionBeamText.Text = $"{(int)Math.Round(ProjectTranscriptionBeamSlider.Value)}";
        else if (sender == ProjectTranscriptionTemperatureSlider && ProjectTranscriptionTemperatureText != null)
            ProjectTranscriptionTemperatureText.Text = ProjectTranscriptionTemperatureSlider.Value.ToString("F1", CultureInfo.InvariantCulture);
        else if (sender == ProjectTranscriptionVadThresholdSlider && ProjectTranscriptionVadThresholdText != null)
            ProjectTranscriptionVadThresholdText.Text = ProjectTranscriptionVadThresholdSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
        else if (sender == ProjectTranscriptionVadMinSilenceSlider && ProjectTranscriptionVadMinSilenceText != null)
            ProjectTranscriptionVadMinSilenceText.Text = $"{(int)Math.Round(ProjectTranscriptionVadMinSilenceSlider.Value)}ms";
        else if (sender == ProjectTranscriptionVadSpeechPadSlider && ProjectTranscriptionVadSpeechPadText != null)
            ProjectTranscriptionVadSpeechPadText.Text = $"{(int)Math.Round(ProjectTranscriptionVadSpeechPadSlider.Value)}ms";
        else if (sender == ProjectTranscriptionMaxTokensSlider && ProjectTranscriptionMaxTokensText != null)
            ProjectTranscriptionMaxTokensText.Text = $"{(int)Math.Round(ProjectTranscriptionMaxTokensSlider.Value)}";
        else if (sender == ProjectTranscriptionChunkSecondsSlider && ProjectTranscriptionChunkSecondsText != null)
            ProjectTranscriptionChunkSecondsText.Text = $"{(int)Math.Round(ProjectTranscriptionChunkSecondsSlider.Value)}s";

        if (!_loadingProjectTranscription)
        {
            SaveProjectTranscriptionSettings(persistImmediately: false);
            ScheduleProjectSettingsPersistence();
        }
    }

    private void ScheduleProjectSettingsPersistence()
    {
        var previous = _projectSettingsPersistence;
        var request = new CancellationTokenSource();
        _projectSettingsPersistence = request;
        previous.Cancel();
        previous.Dispose();
        _ = PersistProjectSettingsAfterIdleAsync(request);
    }

    private async Task PersistProjectSettingsAfterIdleAsync(CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(350, request.Token);
            if (ReferenceEquals(request, _projectSettingsPersistence) && !_isClosing)
                SaveProjects();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void ProjectResetTranscriptionDefaults_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        var modelId = project.TranscriptionModelId ?? "whisper-base";
        var confirmed = await ConfirmComponentUninstallAsync(
            "恢复默认参数配置？",
            $"确定将当前项目的语音转录参数恢复为【{modelId}】的默认配置模板吗？",
            "确认恢复");
        if (!confirmed) return;

        LoadProjectTranscriptionFromModelDefaults(project, modelId);
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();

        RefreshProjectTranscriptionSettings(project);
        ShowSettingsNotice("↺ 已成功恢复为模型的默认参数配置！");
    }

    private async void ProjectSaveTranscriptionDefaults_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        var modelId = project.TranscriptionModelId ?? "whisper-base";
        var confirmed = await ConfirmComponentUninstallAsync(
            "保存为默认参数配置？",
            $"确定将当前项目的转录参数保存为【{modelId}】的全局默认配置吗？后续新建项目或选择此模型时将默认采用这套参数。",
            "确认保存");
        if (!confirmed) return;

        SaveProjectTranscriptionToModelDefaults(project, modelId);
        ShowSettingsNotice("已成功保存为该模型的全局默认参数配置！");
    }

    private void LoadProjectTranscriptionFromModelDefaults(CaptionProject project, string modelId)
    {
        var path = Path.Combine(_deployment.RuntimeRoot, "config", $"{modelId}.json");
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.TryGetProperty("device", out var device)) project.TranscriptionDevice = device.GetString() ?? "自动选择";
                if (root.TryGetProperty("language", out var lang)) project.TranscriptionLanguage = lang.GetString() ?? "自动检测";
                if (root.TryGetProperty("precision", out var prec)) project.TranscriptionPrecision = prec.GetString() ?? "自动";
                if (root.TryGetProperty("beamSize", out var beam) && beam.TryGetInt32(out var beamVal)) project.TranscriptionBeamSize = beamVal;
                if (root.TryGetProperty("temperature", out var temp) && temp.TryGetDouble(out var tempVal)) project.TranscriptionTemperature = tempVal;
                if (root.TryGetProperty("vad", out var vad) && (vad.ValueKind == JsonValueKind.True || vad.ValueKind == JsonValueKind.False)) project.EnableVadFilter = vad.GetBoolean();
                if (root.TryGetProperty("vadThreshold", out var vadThresh) && vadThresh.TryGetDouble(out var threshVal)) project.VadThreshold = threshVal;
                if (root.TryGetProperty("vadMinSilence", out var minSil) && minSil.TryGetInt32(out var minSilVal)) project.VadMinSilence = minSilVal;
                if (root.TryGetProperty("vadSpeechPad", out var pad) && pad.TryGetInt32(out var padVal)) project.VadSpeechPad = padVal;
                if (root.TryGetProperty("maxTokens", out var maxTok) && maxTok.TryGetInt32(out var maxTokVal)) project.TranscriptionMaxTokens = maxTokVal;
                if (root.TryGetProperty("timestamps", out var ts) && (ts.ValueKind == JsonValueKind.True || ts.ValueKind == JsonValueKind.False)) project.EnableWordTimestamps = ts.GetBoolean();
                if (root.TryGetProperty("hotwords", out var hw)) project.TranscriptionHotwords = hw.GetString() ?? string.Empty;
                if (root.TryGetProperty("diarization", out var diar) && (diar.ValueKind == JsonValueKind.True || diar.ValueKind == JsonValueKind.False)) project.EnableDiarization = diar.GetBoolean();
                if (root.TryGetProperty("speakerCount", out var sc)) project.TranscriptionSpeakerCount = sc.GetString() ?? "自动检测";
                if (root.TryGetProperty("emotionDetection", out var em) && (em.ValueKind == JsonValueKind.True || em.ValueKind == JsonValueKind.False)) project.EnableEmotion = em.GetBoolean();
                if (root.TryGetProperty("audioEventDetection", out var ae) && (ae.ValueKind == JsonValueKind.True || ae.ValueKind == JsonValueKind.False)) project.EnableAudioEvent = ae.GetBoolean();
                if (root.TryGetProperty("chunkSeconds", out var chunk) && chunk.TryGetInt32(out var chunkVal)) project.TranscriptionChunkSeconds = chunkVal;
                return;
            }
            catch
            {
                // Invalid saved parameters are reset to safe defaults below.
            }
        }

        project.TranscriptionDevice = "自动选择";
        project.TranscriptionLanguage = "自动检测";
        project.TranscriptionPrecision = "自动";
        project.TranscriptionBeamSize = 5;
        project.TranscriptionTemperature = 0.2;
        project.EnableVadFilter = true;
        project.VadThreshold = 0.3;
        project.VadMinSilence = 2000;
        project.VadSpeechPad = 400;
        project.TranscriptionMaxTokens = 512;
        project.EnableWordTimestamps = true;
        project.TranscriptionHotwords = string.Empty;
        project.EnableDiarization = true;
        project.TranscriptionSpeakerCount = "自动检测";
        project.EnableEmotion = true;
        project.EnableAudioEvent = true;
        project.TranscriptionChunkSeconds = 30;
    }

    private void SaveProjectTranscriptionToModelDefaults(CaptionProject project, string modelId)
    {
        var directory = Path.Combine(_deployment.RuntimeRoot, "config");
        Directory.CreateDirectory(directory);
        var settings = new
        {
            model = modelId,
            device = project.TranscriptionDevice,
            language = project.TranscriptionLanguage,
            precision = project.TranscriptionPrecision,
            beamSize = project.TranscriptionBeamSize,
            vad = project.EnableVadFilter,
            vadThreshold = project.VadThreshold,
            vadMinSilence = project.VadMinSilence,
            vadSpeechPad = project.VadSpeechPad,
            maxTokens = project.TranscriptionMaxTokens,
            timestamps = project.EnableWordTimestamps,
            hotwords = project.TranscriptionHotwords,
            emotionDetection = project.EnableEmotion,
            audioEventDetection = project.EnableAudioEvent,
            speakerCount = project.TranscriptionSpeakerCount,
            diarization = project.EnableDiarization,
            temperature = project.TranscriptionTemperature,
            chunkSeconds = project.TranscriptionChunkSeconds,
            advanced = new Dictionary<string, object?>()
        };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, $"{modelId}.json"), json);
        File.WriteAllText(Path.Combine(directory, "asr-settings.json"), json);
    }

    private async void ProjectSection_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;
        if (section != "workspace")
        {
            WorkspaceVideoHost?.UpdateNativeVisibility(false);
            if (WorkspaceVideoHost != null) WorkspaceVideoHost.IsVisible = false;
            if (ProjectWorkspaceView != null) ProjectWorkspaceView.IsVisible = false;
        }
        await SwitchProjectSectionAsync(section);
    }

    private async void ProjectStage_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Border { Tag: string section }) return;

        // Let the video import button keep its own file-picker behavior instead
        // of treating that click as a card navigation gesture.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        e.Handled = true;
        await SwitchProjectSectionAsync(section);
    }

    private string _activeProjectSectionName = "flow";

    private List<string> GetActiveProjectSections(CaptionProject? project = null)
    {
        project ??= _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        var mode = project?.WorkflowMode ?? "video-bilingual";
        var enableProcess = project?.EnableSubtitleProcessing ?? false;

        var list = new List<string> { "flow" };
        if (!string.Equals(mode, "subtitle-translation", StringComparison.OrdinalIgnoreCase))
            list.Add("transcribe");
        if (enableProcess)
            list.Add("process");
        if (!string.Equals(mode, "video-transcript", StringComparison.OrdinalIgnoreCase))
            list.Add("translate");
        list.Add("workspace");
        return list;
    }

    private (int Index, Control View) ProjectSection(string section)
    {
        var activeSections = GetActiveProjectSections();
        var visualIndex = activeSections.IndexOf(section);
        if (visualIndex < 0) visualIndex = 0;
        var view = section switch
        {
            "transcribe" => ProjectTranscribeView,
            "process" => ProjectProcessView,
            "translate" => ProjectTranslateView,
            "workspace" => ProjectWorkspaceView,
            _ => (Control)ProjectFlowView
        };
        return (visualIndex, view);
    }

    private void UpdateProjectSectionNavigation(string section, int index)
    {
        _activeProjectSectionName = section;
        var activeSections = GetActiveProjectSections();
        ProjectFlowTab.IsVisible = true;
        ProjectTranscribeTab.IsVisible = activeSections.Contains("transcribe");
        ProjectProcessTab.IsVisible = activeSections.Contains("process");
        ProjectTranslateTab.IsVisible = activeSections.Contains("translate");
        ProjectWorkspaceTab.IsVisible = true;

        ProjectFlowTab.Classes.Set("selected", section == "flow");
        ProjectTranscribeTab.Classes.Set("selected", section == "transcribe");
        ProjectProcessTab.Classes.Set("selected", section == "process");
        ProjectTranslateTab.Classes.Set("selected", section == "translate");
        ProjectWorkspaceTab.Classes.Set("selected", section == "workspace");

        var visualIndex = activeSections.IndexOf(section);
        if (visualIndex < 0) visualIndex = 0;
        ProjectSectionIndicator.RenderTransform = TransformOperations.Parse($"translate({visualIndex * 88}px, 0px)");
    }

    private void SetProjectSectionImmediate(string section)
    {
        if (section != "workspace")
        {
            WorkspaceVideoHost?.UpdateNativeVisibility(false);
        }
        var (index, target) = ProjectSection(section);
        _projectSectionNavigation.Cancel();
        foreach (var view in new[] { ProjectFlowView, ProjectTranscribeView, ProjectProcessView, ProjectTranslateView, ProjectWorkspaceView })
        {
            view.IsVisible = ReferenceEquals(view, target);
            view.IsHitTestVisible = ReferenceEquals(view, target);
            view.Opacity = 1;
            view.RenderTransform = null;
        }
        _projectSectionIndex = index;
        _activeProjectSectionView = target;
        UpdateProjectSectionNavigation(section, index);
        WorkspaceVideoHost?.UpdateNativeVisibility(section == "workspace" && !IsAudioOnlyMedia(_workspaceMediaPath));
        SetWorkspaceMode(section == "workspace");
    }

    private async Task SwitchProjectSectionAsync(string section)
    {
        if (section != "workspace")
        {
            WorkspaceVideoHost?.UpdateNativeVisibility(false);
        }
        if (_projectSectionTransitioning) return;
        var (nextIndex, target) = ProjectSection(section);
        if (ReferenceEquals(target, _activeProjectSectionView))
        {
            if (section == "workspace")
            {
                await ActivateWorkspaceAsync();
            }
            return;
        }

        var current = _activeProjectSectionView;
        var previousIndex = _projectSectionIndex;
        if (section == "workspace")
        {
            _workspaceReturnSection = previousIndex switch
            {
                1 => "transcribe",
                2 => "process",
                3 => "translate",
                _ => "flow"
            };
        }
        _projectSectionTransitioning = true;
        _projectSectionIndex = nextIndex;
        _activeProjectSectionView = target;
        UpdateProjectSectionNavigation(section, nextIndex);

        var previous = _projectSectionNavigation;
        _projectSectionNavigation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var token = _projectSectionNavigation.Token;
        try
        {
            if (section == "workspace" || ReferenceEquals(current, ProjectWorkspaceView))
            {
                // The editor is a regular project page. Switching to or from it
                // is immediate so the video surface is never scaled or slid.
                current.IsVisible = false;
                current.IsHitTestVisible = false;
                current.RenderTransform = null;
                target.IsVisible = true;
                target.IsHitTestVisible = true;
                target.Opacity = 1;
                target.RenderTransform = null;
                WorkspaceVideoHost?.UpdateNativeVisibility(section == "workspace" && !IsAudioOnlyMedia(_workspaceMediaPath));
        SetWorkspaceMode(section == "workspace");
            }
            else
            {
                await _motion.SlideContentTransitionAsync(current, target, nextIndex > previousIndex, token);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer project tab owns the next horizontal transition.
        }
        finally
        {
            if (ReferenceEquals(_activeProjectSectionView, target))
            {
                foreach (var view in new[] { ProjectFlowView, ProjectTranscribeView, ProjectProcessView, ProjectTranslateView, ProjectWorkspaceView })
                {
                    view.IsVisible = ReferenceEquals(view, target);
                    view.IsHitTestVisible = ReferenceEquals(view, target);
                    view.Opacity = 1;
                    view.RenderTransform = null;
                }
                WorkspaceVideoHost?.UpdateNativeVisibility(section == "workspace" && !IsAudioOnlyMedia(_workspaceMediaPath));
        SetWorkspaceMode(section == "workspace");
            }
            _projectSectionTransitioning = false;
        }

        if (section == "workspace")
        {
            await ActivateWorkspaceAsync();
        }
        else if (section == "translate" && _activeProjectId is not null)
        {
            var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
            if (project is not null) RefreshProjectTranslation(project);
            if (_workspacePlayer.IsRunning) await _workspacePlayer.SetPauseAsync(true);
        }
        else
        {
            if (_workspacePlayer.IsRunning) await _workspacePlayer.SetPauseAsync(true);
        }
    }

    private async void ProjectFlowStartAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_projectFlowRunning || _activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        _projectFlowRunning = true;
        ProjectFlowStartAction.IsEnabled = false;
        ProjectFlowStartText.Text = "执行中";
        ProjectFlowStatusBorder.IsVisible = false;
        ProjectFlowStatusText.Text = string.Empty;
        try
        {
            await RunProjectFlowAsync(project);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await SwitchProjectSectionAsync("flow");
            ProjectFlowStatusText.Text = $"流程中止：{ShortMessage(exception.Message)}";
            ProjectFlowStatusBorder.IsVisible = true;
        }
        finally
        {
            _projectFlowRunning = false;
            ProjectFlowStartAction.IsEnabled = true;
            ProjectFlowStartText.Text = "开始";
        }
    }

    private async Task RunProjectFlowAsync(CaptionProject project)
    {
        var mode = string.IsNullOrWhiteSpace(project.WorkflowMode) ? "video-bilingual" : project.WorkflowMode;
        if (mode == "subtitle-translation")
        {
            if (string.IsNullOrWhiteSpace(project.SubtitlePath) || !File.Exists(project.SubtitlePath))
                throw new InvalidOperationException("请先在字幕翻译页加载原字幕");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(project.SourceVideoPath) || !File.Exists(project.SourceVideoPath))
            {
                var mediaPath = await PickProjectVideoAsync();
                if (string.IsNullOrWhiteSpace(mediaPath)) throw new OperationCanceledException();
                project.SourceVideoPath = mediaPath;
                project.UpdatedAt = DateTimeOffset.Now;
                SaveProjects();
                RefreshProjectWorkflow(project);
            }
            if (string.IsNullOrWhiteSpace(project.SubtitlePath) || !File.Exists(project.SubtitlePath))
            {
                await SwitchProjectSectionAsync("transcribe");
                ProjectStartTranscription_OnClick(null, new RoutedEventArgs());
                await WaitForProjectStageAsync(() => _projectTranscriptionRunning);
                if (string.IsNullOrWhiteSpace(project.SubtitlePath) || !File.Exists(project.SubtitlePath))
                    throw new InvalidOperationException("语音转录未生成字幕，请查看转录页错误信息");
            }
        }

        if (project.EnableSubtitleProcessing &&
            (string.IsNullOrWhiteSpace(project.ProcessedSubtitlePath) || !File.Exists(project.ProcessedSubtitlePath)))
        {
            await SwitchProjectSectionAsync("process");
            ProjectStartProcessing_OnClick(null, new RoutedEventArgs());
            await WaitForProjectStageAsync(() => _projectProcessingRunning);
            if (string.IsNullOrWhiteSpace(project.ProcessedSubtitlePath) || !File.Exists(project.ProcessedSubtitlePath))
                throw new InvalidOperationException("字幕处理未完成，请查看字幕处理页错误信息");
        }

        if (mode != "video-transcript")
        {
            var translatedPath = Path.Combine(ProjectDirectory(project.Id), "translated.srt");
            if (!File.Exists(translatedPath))
            {
                await SwitchProjectSectionAsync("translate");
                ProjectStartTranslation_OnClick(null, new RoutedEventArgs());
                await WaitForProjectStageAsync(() => _projectTranslationRunning);
                if (!File.Exists(translatedPath))
                    throw new InvalidOperationException("字幕翻译未完成，请查看字幕翻译页错误信息");
            }
        }

        await SwitchProjectSectionAsync("workspace");
    }

    private static async Task WaitForProjectStageAsync(Func<bool> isRunning)
    {
        // async-void stage handlers set their running flag before their first
        // await. Yield once, then wait for their existing completion path.
        await Task.Yield();
        while (isRunning()) await Task.Delay(120);
    }

    private async void ImportProjectVideo_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        if (string.Equals(project.WorkflowMode, "subtitle-translation", StringComparison.OrdinalIgnoreCase))
        {
            ProjectLoadSubtitle_OnClick(sender, e);
            return;
        }

        var videoPath = await PickProjectVideoAsync();
        if (string.IsNullOrWhiteSpace(videoPath)) return;

        project.SourceVideoPath = videoPath;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RefreshProjectWorkflow(project);
        RebuildProjectSidebar();
        SetProjectSelection(project.Id);
    }

    private async Task<string?> PickProjectVideoAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要制作字幕的视频或音频",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("视频文件")
                {
                    Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v", "*.flv"]
                },
                new FilePickerFileType("音频文件")
                {
                    Patterns = ["*.m4a", "*.mp3", "*.wav", "*.flac", "*.aac", "*.ogg", "*.opus"]
                },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count == 0) return null;
        return files[0].TryGetLocalPath() ?? files[0].Name;
    }

    private string ProjectStorePath =>
        Path.Combine(_deployment.RuntimeRoot, "projects", "projects.json");

    private string ProjectDataRoot => Path.Combine(_deployment.RuntimeRoot, "projects", "data");

    private string ProjectDirectory(string projectId) => Path.Combine(ProjectDataRoot, projectId);

    private void EnsureProjectDirectory(string projectId)
    {
        try
        {
            Directory.CreateDirectory(ProjectDirectory(projectId));
        }
        catch
        {
            // The project index is still usable when its optional workspace cannot be created yet.
        }
    }

    private void LoadProjects()
    {
        var storedIndexLoaded = false;
        try
        {
            if (File.Exists(ProjectStorePath))
            {
                var loaded = JsonSerializer.Deserialize<List<CaptionProject>>(File.ReadAllText(ProjectStorePath));
                if (loaded is not null) _projects.AddRange(loaded);
                storedIndexLoaded = true;
            }
        }
        catch
        {
            // A damaged project index must not keep the application from opening.
        }

        // An intentionally empty saved list must stay empty after restart. Demo
        // projects are seeded only on the very first launch or after a corrupt index.
        if (storedIndexLoaded)
        {
            var migrated = false;
            foreach (var project in _projects)
            {
                if (project.TranslationProvider.Equals("dashscope", StringComparison.OrdinalIgnoreCase))
                {
                    project.TranslationProvider = "qwen";
                    migrated = true;
                }
                else if (!_translationProfiles.ContainsKey(project.TranslationProvider))
                {
                    project.TranslationProvider = "deepseek";
                    migrated = true;
                }
            }
            if (migrated) SaveProjects();
            return;
        }
        _projects.AddRange(
        [
            new CaptionProject { Name = "产品发布会" },
            new CaptionProject { Name = "课程样片" }
        ]);
        SaveProjects();
    }

    private void SaveProjects()
    {
        try
        {
            var directory = Path.GetDirectoryName(ProjectStorePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(ProjectStorePath, JsonSerializer.Serialize(_projects,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Keep the in-memory project usable when the local store is temporarily unavailable.
        }
    }

    private void RebuildProjectSidebar()
    {
        RebuildTaskOverview();
        RefreshHomeDashboard();
    }

    private sealed record TaskOverviewItem(string ProjectId, string Title, string Detail);

    private void RebuildTaskOverview()
    {
        var tasks = _projects
            .Where(project => !string.IsNullOrWhiteSpace(project.SourceVideoPath))
            .OrderByDescending(project => project.IsPinned)
            .ThenByDescending(project => project.UpdatedAt)
            .ToList();

        TaskOverviewCountText.Text = $"{tasks.Count} 个任务";
        TaskOverviewEmptyState.IsVisible = tasks.Count == 0;

        var items = new List<TaskOverviewItem>(tasks.Count);
        foreach (var project in tasks)
        {
            var sourcePath = project.SourceVideoPath!;
            var title = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(title)) title = project.Name;

            var fileDetail = string.Empty;
            try
            {
                if (File.Exists(sourcePath)) fileDetail = FormatByteSize(new FileInfo(sourcePath).Length) + " · ";
            }
            catch
            {
                // A removable or network source can disappear without invalidating the task record.
            }

            items.Add(new TaskOverviewItem(project.Id, title,
                $"{project.Name} · {fileDetail}更新于 {project.UpdatedAt:MM/dd HH:mm}"));
        }
        TaskOverviewListHost.ItemsSource = items;
    }

    private static readonly StreamGeometry TaskVideoGeometry =
        StreamGeometry.Parse("M4 3H15L20 8V21H4V3ZM6 5V19H18V9H14V5H6ZM9 10L15 13L9 16V10Z");
    private static readonly StreamGeometry TaskChevronGeometry =
        StreamGeometry.Parse("M9 5L16 12L9 19L7.6 17.6L13.2 12L7.6 6.4L9 5Z");

    private Control CreateTaskOverviewRow(TaskOverviewItem item)
    {
            var row = new Button
            {
                Tag = item.ProjectId,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            row.Classes.Add("taskOverviewRow");
            row.Click += Project_OnClick;

            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*,Auto,18") };
            content.Children.Add(new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(12),
                Background = Brush.Parse("#EEF6FD"),
                Child = new PathIcon
                {
                    Width = 21,
                    Height = 21,
                    Foreground = Brush.Parse("#3399F3"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Data = TaskVideoGeometry
                }
            });

            var labels = new StackPanel
            {
                Margin = new Thickness(12, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = item.Title,
                        FontSize = 13,
                        FontWeight = FontWeight.Bold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = item.Detail,
                        FontSize = 10.5,
                        Foreground = Brush.Parse("#858B96"),
                        Margin = new Thickness(0, 5, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);

            var status = new Border
            {
                Padding = new Thickness(11, 5),
                CornerRadius = new CornerRadius(12),
                Background = Brush.Parse("#EEF6FD"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "待转录",
                    FontSize = 10.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brush.Parse("#2786D8")
                }
            };
            Grid.SetColumn(status, 2);
            content.Children.Add(status);

            var chevron = new PathIcon
            {
                Width = 15,
                Height = 15,
                Foreground = Brush.Parse("#818894"),
                VerticalAlignment = VerticalAlignment.Center,
                Data = TaskChevronGeometry
            };
            Grid.SetColumn(chevron, 3);
            content.Children.Add(chevron);

            row.Content = content;
            return row;
    }

    private void RefreshHomeDashboard()
    {
        var videoTasks = _projects
            .Where(project => !string.IsNullOrWhiteSpace(project.SourceVideoPath))
            .OrderByDescending(project => project.UpdatedAt)
            .ToList();
        var totalDurationMs = 0L;
        var transcribedCount = 0;
        foreach (var project in _projects)
        {
            var srtPath = project.ProcessedSubtitlePath ?? project.SubtitlePath;
            if (string.IsNullOrWhiteSpace(srtPath) || !File.Exists(srtPath)) continue;
            try
            {
                var segments = ParseSrt(File.ReadAllText(srtPath));
                if (segments.Count > 0)
                {
                    totalDurationMs += segments[^1].EndMilliseconds;
                    transcribedCount++;
                }
            }
            catch
            {
                // Unreadable project subtitles are excluded from aggregate statistics.
            }
        }

        var totalDuration = TimeSpan.FromMilliseconds(totalDurationMs);
        string durationText;
        if (totalDuration.TotalHours >= 1)
            durationText = $"{(int)totalDuration.TotalHours} 小时 {totalDuration.Minutes} 分";
        else if (totalDuration.TotalMinutes >= 1)
            durationText = $"{(int)totalDuration.TotalMinutes} 分钟";
        else if (totalDuration.TotalSeconds > 0)
            durationText = $"{(int)totalDuration.TotalSeconds} 秒";
        else
            durationText = "0 分钟";

        var modelCount = _configurableModels.Count;
        HomeVideoTaskCountText.Text = videoTasks.Count.ToString(CultureInfo.InvariantCulture);
        HomeWeekTaskCountText.Text = durationText;
        HomeLocalModelCountText.Text = modelCount.ToString(CultureInfo.InvariantCulture);

        var activeModelName = ConfiguredModelCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(activeModelName)) activeModelName = _configurableModels.FirstOrDefault().Name;
        var hasModel = modelCount > 0 && !string.IsNullOrWhiteSpace(activeModelName);
        HomeModelNameText.Text = hasModel ? activeModelName! : "尚未安装识别模型";
        HomeModelStatusText.Text = hasModel ? $"本地共有 {modelCount} 个模型可用" : "前往模型管理下载";
        HomeModelStatusDot.Background = Brush.Parse(hasModel ? "#3399F3" : "#A6ABB3");

        HomeRecentTaskHost.Children.Clear();
        var recentTasks = videoTasks.Take(3).ToList();
        HomeRecentTaskEmpty.IsVisible = recentTasks.Count == 0;
        foreach (var project in recentTasks)
        {
            var sourcePath = project.SourceVideoPath!;
            var fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = project.Name;

            var row = new Button
            {
                Tag = project.Id,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            row.Classes.Add("homeRecentRow");
            row.Click += Project_OnClick;

            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*,Auto") };
            content.Children.Add(new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = Brush.Parse("#EEF6FD"),
                Child = new PathIcon
                {
                    Width = 18,
                    Height = 18,
                    Foreground = Brush.Parse("#3399F3"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Data = StreamGeometry.Parse("M4 3H15L20 8V21H4V3ZM6 5V19H18V9H14V5H6ZM9 10L15 13L9 16V10Z")
                }
            });

            var labels = new StackPanel
            {
                Margin = new Thickness(11, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = fileName,
                        FontSize = 12.5,
                        FontWeight = FontWeight.Bold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = $"{project.Name} · 更新于 {project.UpdatedAt:MM/dd HH:mm}",
                        FontSize = 10.5,
                        Foreground = Brush.Parse("#858B96"),
                        Margin = new Thickness(0, 4, 0, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);

            var action = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "继续处理",
                        FontSize = 10.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brush.Parse("#657080"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new PathIcon
                    {
                        Width = 14,
                        Height = 14,
                        Foreground = Brush.Parse("#657080"),
                        Data = StreamGeometry.Parse("M9 5L16 12L9 19L7.6 17.6L13.2 12L7.6 6.4L9 5Z")
                    }
                }
            };
            Grid.SetColumn(action, 2);
            content.Children.Add(action);
            row.Content = content;
            HomeRecentTaskHost.Children.Add(row);
        }
    }

    private ContextMenu BuildProjectContextMenu(CaptionProject project)
    {
        var openFolder = CreateProjectMenuItem(
            "在资源管理器打开",
            "M3 5H9L11 7H21V19H3V5ZM5 9V17H19V9H5Z");
        openFolder.Click += (_, _) => OpenProjectFolder(project.Id);

        var rename = CreateProjectMenuItem(
            "重命名",
            "M4 17.25V20H6.75L17.81 8.94L15.06 6.19L4 17.25ZM20.71 6.04C21.1 5.65 21.1 5.02 20.71 4.63L19.37 3.29C18.98 2.9 18.35 2.9 17.96 3.29L16.91 4.34L19.66 7.09L20.71 6.04Z");
        rename.Click += async (_, _) => await RenameProjectAsync(project.Id);

        var delete = CreateProjectMenuItem(
            "删除项目",
            "M6 19C6 20.1 6.9 21 8 21H16C17.1 21 18 20.1 18 19V7H6V19ZM8 9H16V19H8V9ZM15.5 4L14.5 3H9.5L8.5 4H5V6H19V4H15.5Z",
            destructive: true);
        delete.Click += async (_, _) => await DeleteProjectAsync(project.Id);

        return new ContextMenu
        {
            MinWidth = 210,
            FontSize = 12.5,
            ItemsSource = new object[] { openFolder, rename, new Separator(), delete }
        };
    }

    private static MenuItem CreateProjectMenuItem(string title, string iconData, bool destructive = false)
    {
        var color = Brush.Parse(destructive ? "#EF4444" : "#2D3036");
        return new MenuItem
        {
            Header = title,
            Height = 40,
            Padding = new Thickness(11, 0),
            Foreground = color,
            FontWeight = destructive ? FontWeight.SemiBold : FontWeight.Medium,
            Icon = new PathIcon
            {
                Width = 18,
                Height = 18,
                Foreground = color,
                Data = StreamGeometry.Parse(iconData)
            }
        };
    }

    private void OpenProjectFolder(string projectId)
    {
        EnsureProjectDirectory(projectId);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(ProjectDirectory(projectId));
            Process.Start(startInfo);
        }
        catch
        {
            // Explorer may be unavailable on non-Windows platforms; project actions remain usable.
        }
    }

    private async Task RenameProjectAsync(string projectId)
    {
        var project = _projects.FirstOrDefault(item => item.Id == projectId);
        if (project is null) return;

        var name = await PromptForProjectNameAsync(project.Name, "重命名项目", "保存名称");
        if (string.IsNullOrWhiteSpace(name)) return;

        project = _projects.FirstOrDefault(item => item.Id == projectId);
        if (project is null) return;
        project.Name = name.Trim();
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RebuildProjectSidebar();
        if (_activeProjectId == projectId)
        {
            ProjectTitleText.Text = project.Name;
            SetProjectSelection(projectId);
        }
    }

    private async Task DeleteProjectAsync(string projectId)
    {
        var project = _projects.FirstOrDefault(item => item.Id == projectId);
        if (project is null) return;

        var confirmed = await ConfirmComponentUninstallAsync(
            $"删除“{project.Name}”？",
            "项目记录及其本地工作文件将被删除，此操作无法撤销。",
            "删除项目");
        var deletingActiveProject = string.Equals(_activeProjectId, projectId, StringComparison.OrdinalIgnoreCase);
        _projects.RemoveAll(item => string.Equals(item.Id, projectId, StringComparison.OrdinalIgnoreCase));
        TryDeleteProjectDirectory(projectId);
        if (deletingActiveProject) _activeProjectId = null;
        SaveProjects();
        RebuildProjectSidebar();
        if (deletingActiveProject) await NavigateTo("overview");
    }

    private void TryDeleteProjectDirectory(string projectId)
    {
        try
        {
            var root = Path.GetFullPath(ProjectDataRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(ProjectDirectory(projectId));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target)) return;
            Directory.Delete(target, recursive: true);
        }
        catch
        {
            // The project is removed from the index even when a file is temporarily locked.
        }
    }

    private void SetProjectSelection(string? selectedProjectId)
    {
        foreach (var pair in _projectButtons)
            pair.Value.Classes.Set("selected", string.Equals(pair.Key, selectedProjectId,
                StringComparison.OrdinalIgnoreCase) && ReferenceEquals(_activePage, ProjectPage));
    }

    private bool _loadingProjectWorkflowMode;

    private void ProjectWorkflowModeCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProjectWorkflowMode || _activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null || ProjectWorkflowModeCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string mode) return;

        project.WorkflowMode = mode;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RefreshProjectWorkflow(project);
    }

    private void ProjectSubtitleProcessingToggle_OnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_loadingProjectWorkflowMode || _activeProjectId is null || e.Property.Name != nameof(ToggleSwitch.IsChecked)) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        project.EnableSubtitleProcessing = ProjectSubtitleProcessingToggle.IsChecked == true;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RefreshProjectWorkflow(project);
    }

    private void RefreshProjectWorkflow(CaptionProject project)
    {
        _loadingProjectWorkflowMode = true;
        try
        {
            var mode = string.IsNullOrWhiteSpace(project.WorkflowMode) ? "video-bilingual" : project.WorkflowMode;
            var modeItem = ProjectWorkflowModeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, mode, StringComparison.OrdinalIgnoreCase));
            ProjectWorkflowModeCombo.SelectedItem = modeItem ?? ProjectWorkflowModeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
            ProjectSubtitleProcessingToggle.IsChecked = project.EnableSubtitleProcessing;

            var hasVideo = !string.IsNullOrWhiteSpace(project.SourceVideoPath) && File.Exists(project.SourceVideoPath);
            var hasSubtitle = !string.IsNullOrWhiteSpace(project.SubtitlePath) && File.Exists(project.SubtitlePath);
            var hasProcessedSubtitle = !string.IsNullOrWhiteSpace(project.ProcessedSubtitlePath) && File.Exists(project.ProcessedSubtitlePath);

            ProjectTitleText.Text = project.Name;

            switch (mode)
            {
                case "video-transcript":
                    ProjectFlowModeBadgeText.Text = project.EnableSubtitleProcessing ? "视频 → 原文字幕 (3 步精修)" : "视频 → 原文字幕 (2 步转录)";
                    ProjectFlowModeBadge.Background = Brush.Parse("#F5F3FF");
                    ProjectFlowModeBadge.BorderBrush = Brush.Parse("#DDD6FE");
                    ProjectFlowModeBadgeText.Foreground = Brush.Parse("#7C3AED");

                    ProjectImportStepNumberText.Text = "01";
                    ProjectImportStageIcon.Kind = Material.Icons.MaterialIconKind.FileVideoOutline;
                    ProjectImportTitleText.Text = "视频导入";
                    ProjectImportDescText.Text = "选择本地视频，读取音轨和基础媒体信息。";
                    ProjectVideoNameText.Text = hasVideo ? Path.GetFileName(project.SourceVideoPath) : "尚未导入视频";
                    ProjectVideoNameText.Foreground = Brush.Parse(hasVideo ? "#2B2E35" : "#8B919B");
                    ProjectImportAction.Content = hasVideo ? "更换视频" : "选择视频";
                    ProjectImportStage.Opacity = 1.0;
                    ProjectImportStage.IsHitTestVisible = true;

                    ProjectTranscribeStage.Opacity = 1.0;
                    ProjectTranscribeStage.IsHitTestVisible = true;
                    ProjectTranscribeConnector.Opacity = 1.0;
                    ProjectTranscribeStatusText.Text = hasSubtitle ? "已完成" : hasVideo ? "可以开始" : "等待媒体";
                    ProjectTranscribeStatusText.Foreground = Brush.Parse(hasSubtitle ? "#278A68" : hasVideo ? "#3399F3" : "#A0A5AD");

                    if (!project.EnableSubtitleProcessing)
                    {
                        ProjectProcessStatusText.Text = "已跳过";
                        ProjectProcessStatusText.Foreground = Brush.Parse("#8B919B");
                        ProjectProcessStage.Opacity = 0.55;
                    }
                    else
                    {
                        ProjectProcessStage.Opacity = 1.0;
                        ProjectProcessStatusText.Text = hasProcessedSubtitle ? "已完成" : hasSubtitle ? "可以开始" : "等待转录";
                        ProjectProcessStatusText.Foreground = Brush.Parse(hasProcessedSubtitle ? "#278A68" : hasSubtitle ? "#3399F3" : "#A0A5AD");
                    }
                    ProjectProcessStage.IsHitTestVisible = true;
                    ProjectProcessConnector.Opacity = 1.0;

                    ProjectTranslateStatusText.Text = "无需翻译";
                    ProjectTranslateStatusText.Foreground = Brush.Parse("#A0A5AD");
                    ProjectTranslateStage.Opacity = 0.4;
                    ProjectTranslateStage.IsHitTestVisible = false;
                    ProjectTranslateConnector.Opacity = 0.25;
                    break;

                case "subtitle-translation":
                    ProjectFlowModeBadgeText.Text = project.EnableSubtitleProcessing ? "翻译已有字幕 (3 步校对翻译)" : "翻译已有字幕 (2 步快翻)";
                    ProjectFlowModeBadge.Background = Brush.Parse("#ECFDF5");
                    ProjectFlowModeBadge.BorderBrush = Brush.Parse("#A7F3D0");
                    ProjectFlowModeBadgeText.Foreground = Brush.Parse("#059669");

                    ProjectImportStepNumberText.Text = "01";
                    ProjectImportStageIcon.Kind = Material.Icons.MaterialIconKind.FileDocumentOutline;
                    ProjectImportTitleText.Text = "字幕导入";
                    ProjectImportDescText.Text = "选择本地 SRT / VTT 等原始字幕文件。";
                    ProjectVideoNameText.Text = hasSubtitle ? Path.GetFileName(project.SubtitlePath) : "尚未导入字幕";
                    ProjectVideoNameText.Foreground = Brush.Parse(hasSubtitle ? "#2B2E35" : "#8B919B");
                    ProjectImportAction.Content = hasSubtitle ? "更换字幕" : "选择字幕";
                    ProjectImportStage.Opacity = 1.0;
                    ProjectImportStage.IsHitTestVisible = true;

                    ProjectTranscribeStatusText.Text = "无需转录";
                    ProjectTranscribeStatusText.Foreground = Brush.Parse("#A0A5AD");
                    ProjectTranscribeStage.Opacity = 0.4;
                    ProjectTranscribeStage.IsHitTestVisible = false;
                    ProjectTranscribeConnector.Opacity = 0.25;

                    if (!project.EnableSubtitleProcessing)
                    {
                        ProjectProcessStatusText.Text = "已跳过";
                        ProjectProcessStatusText.Foreground = Brush.Parse("#8B919B");
                        ProjectProcessStage.Opacity = 0.55;
                    }
                    else
                    {
                        ProjectProcessStage.Opacity = 1.0;
                        ProjectProcessStatusText.Text = hasProcessedSubtitle ? "已完成" : hasSubtitle ? "可以开始" : "等待字幕";
                        ProjectProcessStatusText.Foreground = Brush.Parse(hasProcessedSubtitle ? "#278A68" : hasSubtitle ? "#3399F3" : "#A0A5AD");
                    }
                    ProjectProcessStage.IsHitTestVisible = true;
                    ProjectProcessConnector.Opacity = 1.0;

                    ProjectTranslateStage.Opacity = 1.0;
                    ProjectTranslateStage.IsHitTestVisible = true;
                    ProjectTranslateConnector.Opacity = 1.0;
                    ProjectTranslateStatusText.Text = hasProcessedSubtitle ? "可以开始" : hasSubtitle ? (project.EnableSubtitleProcessing ? "可先处理" : "可以开始") : "等待字幕";
                    ProjectTranslateStatusText.Foreground = Brush.Parse(hasSubtitle ? "#3399F3" : "#A0A5AD");
                    break;

                default: // video-bilingual
                    ProjectFlowModeBadgeText.Text = project.EnableSubtitleProcessing ? "视频 → 双语字幕 (4 步全流程)" : "视频 → 双语字幕 (3 步快翻)";
                    ProjectFlowModeBadge.Background = Brush.Parse("#EFF6FF");
                    ProjectFlowModeBadge.BorderBrush = Brush.Parse("#DBEAFE");
                    ProjectFlowModeBadgeText.Foreground = Brush.Parse("#2563EB");

                    ProjectImportStepNumberText.Text = "01";
                    ProjectImportStageIcon.Kind = Material.Icons.MaterialIconKind.FileVideoOutline;
                    ProjectImportTitleText.Text = "视频导入";
                    ProjectImportDescText.Text = "选择本地视频，读取音轨和基础媒体信息。";
                    ProjectVideoNameText.Text = hasVideo ? Path.GetFileName(project.SourceVideoPath) : "尚未导入视频";
                    ProjectVideoNameText.Foreground = Brush.Parse(hasVideo ? "#2B2E35" : "#8B919B");
                    ProjectImportAction.Content = hasVideo ? "更换视频" : "选择视频";
                    ProjectImportStage.Opacity = 1.0;
                    ProjectImportStage.IsHitTestVisible = true;

                    ProjectTranscribeStage.Opacity = 1.0;
                    ProjectTranscribeStage.IsHitTestVisible = true;
                    ProjectTranscribeConnector.Opacity = 1.0;
                    ProjectTranscribeStatusText.Text = hasSubtitle ? "已完成" : hasVideo ? "可以开始" : "等待媒体";
                    ProjectTranscribeStatusText.Foreground = Brush.Parse(hasSubtitle ? "#278A68" : hasVideo ? "#3399F3" : "#A0A5AD");

                    if (!project.EnableSubtitleProcessing)
                    {
                        ProjectProcessStatusText.Text = "已跳过";
                        ProjectProcessStatusText.Foreground = Brush.Parse("#8B919B");
                        ProjectProcessStage.Opacity = 0.55;
                    }
                    else
                    {
                        ProjectProcessStage.Opacity = 1.0;
                        ProjectProcessStatusText.Text = hasProcessedSubtitle ? "已完成" : hasSubtitle ? "可以开始" : "等待转录";
                        ProjectProcessStatusText.Foreground = Brush.Parse(hasProcessedSubtitle ? "#278A68" : hasSubtitle ? "#3399F3" : "#A0A5AD");
                    }
                    ProjectProcessStage.IsHitTestVisible = true;
                    ProjectProcessConnector.Opacity = 1.0;

                    ProjectTranslateStage.Opacity = 1.0;
                    ProjectTranslateStage.IsHitTestVisible = true;
                    ProjectTranslateConnector.Opacity = 1.0;
                    ProjectTranslateStatusText.Text = hasProcessedSubtitle ? "可以开始" : hasSubtitle ? (project.EnableSubtitleProcessing ? "可跳过处理" : "可以开始") : "等待字幕";
                    ProjectTranslateStatusText.Foreground = Brush.Parse(hasSubtitle ? "#3399F3" : "#A0A5AD");
                    break;
            }

            ProjectImportStage.BorderBrush = Brush.Parse((mode == "subtitle-translation" ? hasSubtitle : hasVideo) ? "#8DC6F7" : "#DDE1E7");
            ProjectImportStage.BoxShadow = BoxShadows.Parse((mode == "subtitle-translation" ? hasSubtitle : hasVideo)
                ? "0 7 24 0 #273399F3"
                : "0 4 16 0 #1418212D");
            ProjectTranscriptionSourceText.Text = hasVideo
                ? $"已载入 {Path.GetFileName(project.SourceVideoPath)}"
                : "请先在流程图中导入视频";
            RefreshProjectTranscriptionReadiness();

            var activeSections = GetActiveProjectSections(project);
            if (!activeSections.Contains(_activeProjectSectionName))
            {
                SetProjectSectionImmediate("flow");
            }
            else
            {
                UpdateProjectSectionNavigation(_activeProjectSectionName, _projectSectionIndex);
            }
        }
        finally
        {
            _loadingProjectWorkflowMode = false;
        }
    }

    private void RefreshProjectTranscriptionModelSelection(CaptionProject project)
    {
        _loadingProjectTranscription = true;
        try
        {
            var names = _configurableModels.Select(model => model.Name).ToArray();
            ProjectTranscriptionModelCombo.ItemsSource = names;
            var configuredName = _configurableModels.FirstOrDefault(model =>
                string.Equals(model.Id, project.TranscriptionModelId, StringComparison.OrdinalIgnoreCase)).Name;
            ProjectTranscriptionModelCombo.SelectedItem = names.Contains(configuredName)
                ? configuredName
                : names.FirstOrDefault();
            ProjectTranscriptionModelEmpty.IsVisible = names.Length == 0;
        }
        finally
        {
            _loadingProjectTranscription = false;
        }
        RefreshProjectTranscriptionReadiness();
    }

    private void ProjectTranscriptionModel_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingProjectTranscription || _activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        var selectedName = ProjectTranscriptionModelCombo.SelectedItem as string;
        var selected = _configurableModels.FirstOrDefault(model => model.Name == selectedName);
        project.TranscriptionModelId = string.IsNullOrWhiteSpace(selected.Id) ? null : selected.Id;
        project.TranscriptionLanguage = string.Empty;
        project.TranscriptionPrecision = "自动";
        ConfigureProjectLanguageOptions(project.TranscriptionModelId, project.TranscriptionLanguage);
        ConfigureProjectPrecisionOptions(project.TranscriptionModelId, project.TranscriptionPrecision);
        ConfigureProjectTranscriptionLayout(project.TranscriptionModelId);
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RefreshProjectTranscriptionReadiness();
    }

    private void RefreshProjectTranscriptionReadiness()
    {
        var project = _activeProjectId is null
            ? null
            : _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        var hasVideo = !string.IsNullOrWhiteSpace(project?.SourceVideoPath) && File.Exists(project.SourceVideoPath);
        var hasModel = ProjectTranscriptionModelCombo.SelectedItem is string selected &&
                       !string.IsNullOrWhiteSpace(selected);
        ProjectStartTranscriptionAction.IsEnabled = !_projectTranscriptionRunning && hasVideo && hasModel;
        ProjectTranscriptionReadyText.Text = (hasVideo, hasModel) switch
        {
            (true, true) => "视频与识别模型已就绪",
            (false, true) => "等待导入视频",
            (true, false) => "等待安装识别模型",
            _ => "等待视频和识别模型"
        };
        ProjectTranscriptionReadyText.Foreground = Brush.Parse(hasVideo && hasModel ? "#278A68" : "#7F8792");
        ProjectTranscriptionStatusDot.Background = Brush.Parse(hasVideo && hasModel ? "#37A477" : "#C5C9D0");
    }

    private async void ProjectStartTranscription_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_projectTranscriptionRunning || _activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null || string.IsNullOrWhiteSpace(project.SourceVideoPath) ||
            !File.Exists(project.SourceVideoPath))
        {
            ProjectTranscriptionReadyText.Text = "媒体文件不存在，请重新选择";
            ProjectTranscriptionReadyText.Foreground = Brush.Parse("#C94444");
            return;
        }

        var selectedName = ProjectTranscriptionModelCombo.SelectedItem as string;
        var selected = _configurableModels.FirstOrDefault(model => model.Name == selectedName);
        if (string.IsNullOrWhiteSpace(selected.Id))
        {
            ProjectTranscriptionReadyText.Text = "请先选择已经安装的识别模型";
            ProjectTranscriptionReadyText.Foreground = Brush.Parse("#C94444");
            return;
        }

        string workerEngine;
        try
        {
            workerEngine = WorkerEngineId(selected.Id);
        }
        catch (NotSupportedException exception)
        {
            ProjectTranscriptionReadyText.Text = exception.Message;
            ProjectTranscriptionReadyText.Foreground = Brush.Parse("#C94444");
            return;
        }

        _projectTranscriptionCancellation.Cancel();
        _projectTranscriptionCancellation.Dispose();
        _projectTranscriptionCancellation = new CancellationTokenSource();
        var token = _projectTranscriptionCancellation.Token;
        _projectTranscriptionRunning = true;
        _projectTranscriptionLog.Clear();
        ProjectTranscriptionLogText.Text = string.Empty;
        ProjectTranscriptionLogStateText.Text = "正在转录";
        _projectTranscriptionLogTilePhase = 0;
        UpdateProjectTranscriptionLogTiles();
        if (_projectTranscriptionLogVisible) _projectTranscriptionLogSpinnerTimer.Start();
        AppendProjectTranscriptionLog($"开始转录：{Path.GetFileName(project.SourceVideoPath)}");
        AppendProjectTranscriptionLog($"识别模型：{selected.Name}");
        ProjectStartTranscriptionText.Text = "正在转录";
        ProjectStartTranscriptionAction.IsEnabled = false;
        ProjectCancelTranscriptionAction.IsVisible = true;
        ProjectTranscriptionProgress.Value = 0;
        ProjectTranscriptionProgressText.Text = "0 %";
        ProjectTranscriptionReadyText.Text = $"正在加载 {selected.Name} 并识别音频…";
        ProjectTranscriptionReadyText.Foreground = Brush.Parse("#3399F3");
        ProjectTranscriptionStatusDot.Background = Brush.Parse("#3399F3");

        try
        {
            SaveProjectTranscriptionSettings();
            var progress = new Progress<(int Percent, string Message, string? LogLine)>(update =>
            {
                var percent = Math.Clamp(update.Percent, 0, 100);
                ProjectTranscriptionProgress.Value = percent;
                ProjectTranscriptionProgressText.Text = $"{percent} %";
                if (!string.IsNullOrWhiteSpace(update.Message))
                    ProjectTranscriptionReadyText.Text = update.Message;
                if (!string.IsNullOrWhiteSpace(update.LogLine))
                    AppendProjectTranscriptionLog(update.LogLine);
                else if (!string.IsNullOrWhiteSpace(update.Message))
                    AppendProjectTranscriptionLog(update.Message);
            });
            var segments = await RunLocalTranscriptionAsync(
                workerEngine, project, progress, token);
            if (segments.Count == 0) throw new InvalidDataException("识别完成，但没有生成有效字幕");

            EnsureProjectDirectory(project.Id);
            var subtitlePath = Path.Combine(ProjectDirectory(project.Id), "recognized.srt");
            await File.WriteAllTextAsync(subtitlePath, BuildRecognitionSrt(segments), new UTF8Encoding(false), token);
            project.TranscriptionModelId = selected.Id;
            project.SubtitlePath = subtitlePath;
            project.ProcessedSubtitlePath = null;
            _workspacePreparedProjectId = null;
            var editedPath = Path.Combine(ProjectDirectory(project.Id), "edited.srt");
            if (File.Exists(editedPath)) File.Delete(editedPath);
            var cuesPath = Path.Combine(ProjectDirectory(project.Id), "workspace-cues.json");
            if (File.Exists(cuesPath)) File.Delete(cuesPath);
            project.UpdatedAt = DateTimeOffset.Now;
            SaveProjects();

            _projectTranslationSegments.Clear();
            _projectTranslationSegments.AddRange(segments);
            SaveProjectTranslationCache(project.Id);
            RefreshProjectWorkflow(project);
            RefreshProjectProcessing(project);
            RefreshProjectTranslation(project);
            RebuildProjectSidebar();
            SetProjectSelection(project.Id);
            ProjectTranscriptionReadyText.Text = $"识别完成，共 {segments.Count} 条字幕";
            ProjectTranscriptionReadyText.Foreground = Brush.Parse("#278A68");
            ProjectTranscriptionStatusDot.Background = Brush.Parse("#37A477");
            ProjectTranscriptionProgress.Value = 100;
            ProjectTranscriptionProgressText.Text = "100 %";
            ProjectTranscriptionLogStateText.Text = "转录已完成";
            AppendProjectTranscriptionLog($"转录完成：生成 {segments.Count} 条字幕");
            await SwitchProjectSectionAsync("process");
        }
        catch (OperationCanceledException)
        {
            ProjectTranscriptionReadyText.Text = "转录已取消";
            ProjectTranscriptionReadyText.Foreground = Brush.Parse("#7F8792");
            ProjectTranscriptionStatusDot.Background = Brush.Parse("#C5C9D0");
            ProjectTranscriptionLogStateText.Text = "转录已取消";
            AppendProjectTranscriptionLog("转录已由用户取消");
        }
        catch (Exception exception)
        {
            ProjectTranscriptionReadyText.Text = $"转录失败：{ShortMessage(exception.Message)}";
            ProjectTranscriptionReadyText.Foreground = Brush.Parse("#C94444");
            ProjectTranscriptionStatusDot.Background = Brush.Parse("#E25A5A");
            ProjectTranscriptionLogStateText.Text = "转录失败";
            AppendProjectTranscriptionLog($"转录失败：{exception.Message}");
        }
        finally
        {
            _projectTranscriptionRunning = false;
            _projectTranscriptionLogSpinnerTimer.Stop();
            ProjectStartTranscriptionText.Text = "开始转录";
            ProjectCancelTranscriptionAction.IsVisible = false;
            RefreshProjectTranscriptionReadiness();
        }
    }

    private static string WorkerEngineId(string modelId) => modelId.ToLowerInvariant() switch
    {
        "qwen-0.6b" => "qwen3-asr-0.6b",
        "qwen-1.7b" => "qwen3-asr-1.7b",
        "nvidia-parakeet-v3" => "nvidia-parakeet-tdt-0.6b-v3",
        "whisper-tiny" or "whisper-base" or "whisper-small" or "whisper-medium" or
            "whisper-large-v3" => modelId.ToLowerInvariant(),
        _ => throw new NotSupportedException("当前模型尚未完成本地转录适配，请选择 Whisper、Qwen3-ASR 或 NVIDIA Parakeet")
    };

    private static bool SupportsLocalTranscription(string modelId) => modelId.ToLowerInvariant() switch
    {
        "qwen-0.6b" or "qwen-1.7b" or "nvidia-parakeet-v3" or
        "whisper-tiny" or "whisper-base" or "whisper-small" or "whisper-medium" or
        "whisper-large-v3" => true,
        _ => false
    };

    private async Task<List<SubtitleSegment>> RunLocalTranscriptionAsync(
        string engine, CaptionProject project,
        IProgress<(int Percent, string Message, string? LogLine)> progress, CancellationToken token)
    {
        var workerPath = Path.Combine(_deployment.AppRoot, "engines", "asr_worker.py");
        var runtimeId = RuntimeIdForWorkerEngine(engine);
        var pythonPath = _deployment.GetRuntimePythonExecutable(runtimeId);
        if (!File.Exists(pythonPath) || !File.Exists(workerPath))
            throw new FileNotFoundException("本地识别环境不完整，请在模型配置中修复运行环境");

        var language = string.IsNullOrWhiteSpace(project.TranscriptionLanguage) ||
                       project.TranscriptionLanguage == "自动检测"
            ? null
            : project.TranscriptionLanguage;
        var requestConfig = new Dictionary<string, object?>
        {
            ["device"] = project.TranscriptionDevice,
            ["language"] = language,
            ["precision"] = project.TranscriptionPrecision,
            ["beamSize"] = project.TranscriptionBeamSize,
            ["temperature"] = project.TranscriptionTemperature,
            ["vad"] = project.EnableVadFilter,
            ["vadThreshold"] = project.VadThreshold,
            ["vadMinSilence"] = project.VadMinSilence,
            ["vadSpeechPad"] = project.VadSpeechPad,
            ["maxTokens"] = project.TranscriptionMaxTokens,
            ["timestamps"] = project.EnableWordTimestamps,
            ["hotwords"] = project.TranscriptionHotwords,
            ["diarization"] = project.EnableDiarization,
            ["speakerCount"] = project.TranscriptionSpeakerCount,
            ["emotionDetection"] = project.EnableEmotion,
            ["audioEventDetection"] = project.EnableAudioEvent,
            ["chunkSeconds"] = project.TranscriptionChunkSeconds
        };

        var request = new
        {
            id = Guid.NewGuid().ToString("N"),
            command = "transcribe",
            engine,
            audio = project.SourceVideoPath,
            language,
            device = project.TranscriptionDevice,
            config = requestConfig
        };
        var workerProgress = new Progress<AsrWorkerProgress>(update =>
            progress.Report((update.Percent, update.Message, update.LogLine)));
        var responseLine = await _asrWorker.TranscribeAsync(
            runtimeId, workerPath, JsonSerializer.Serialize(request), workerProgress, token);

        using var document = JsonDocument.Parse(responseLine);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var message = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var errorText)
                ? errorText.GetString()
                : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "本地识别失败" : message);
        }

        var result = root.GetProperty("result");
        var output = new List<SubtitleSegment>();
        long previousEnd = 0;
        foreach (var item in result.GetProperty("segments").EnumerateArray())
        {
            var text = item.TryGetProperty("text", out var textElement) ? textElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var start = item.TryGetProperty("start", out var startElement) && startElement.ValueKind == JsonValueKind.Number
                ? (long)Math.Round(startElement.GetDouble() * 1000d)
                : previousEnd;
            var end = item.TryGetProperty("end", out var endElement) && endElement.ValueKind == JsonValueKind.Number
                ? (long)Math.Round(endElement.GetDouble() * 1000d)
                : start + Math.Clamp(text.Length * 110L, 1800L, 7000L);
            if (end <= start) end = start + 1000;
            output.Add(new SubtitleSegment
            {
                Index = output.Count + 1,
                StartMilliseconds = start,
                EndMilliseconds = end,
                Original = text
            });
            previousEnd = end;
        }
        return output;
    }

    private static string BuildRecognitionSrt(IEnumerable<SubtitleSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            builder.AppendLine(segment.Index.ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTime(segment.StartMilliseconds)).Append(" --> ")
                .AppendLine(FormatSrtTime(segment.EndMilliseconds));
            builder.AppendLine(segment.Original.Trim()).AppendLine();
        }
        return builder.ToString();
    }

    private void ProjectCancelTranscription_OnClick(object? sender, RoutedEventArgs e) =>
        _projectTranscriptionCancellation.Cancel();

    private void AppendProjectTranscriptionLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _projectTranscriptionLog.Append('[')
            .Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Append("] ")
            .AppendLine(message.Trim());
        const int maximumCharacters = 256 * 1024;
        if (_projectTranscriptionLog.Length > maximumCharacters)
        {
            var remove = _projectTranscriptionLog.Length - maximumCharacters;
            var nextLine = _projectTranscriptionLog.ToString(remove, Math.Min(4096, _projectTranscriptionLog.Length - remove))
                .IndexOf('\n');
            _projectTranscriptionLog.Remove(0, Math.Min(_projectTranscriptionLog.Length,
                remove + (nextLine >= 0 ? nextLine + 1 : 0)));
        }
        _projectTranscriptionLogDirty = true;
        if (_projectTranscriptionLogVisible && !_projectTranscriptionLogFlushTimer.IsEnabled)
            _projectTranscriptionLogFlushTimer.Start();
    }

    private void FlushProjectTranscriptionLog()
    {
        _projectTranscriptionLogFlushTimer.Stop();
        if (!_projectTranscriptionLogDirty || !_projectTranscriptionLogVisible) return;
        _projectTranscriptionLogDirty = false;
        ProjectTranscriptionLogText.Text = _projectTranscriptionLog.Length == 0
            ? "尚未开始转录。"
            : _projectTranscriptionLog.ToString();
        ProjectTranscriptionLogText.CaretIndex = ProjectTranscriptionLogText.Text?.Length ?? 0;
    }

    private void ProjectTranscriptionLog_OnClick(object? sender, RoutedEventArgs e)
    {
        _projectTranscriptionLogVisible = !_projectTranscriptionLogVisible;
        ProjectTranscriptionMainPanel.IsVisible = !_projectTranscriptionLogVisible;
        ProjectTranscriptionLogPanel.IsVisible = _projectTranscriptionLogVisible;
        ProjectTranscriptionLogOpenIcon.IsVisible = !_projectTranscriptionLogVisible;
        ProjectTranscriptionLogBackIcon.IsVisible = _projectTranscriptionLogVisible;

        if (_projectTranscriptionLogVisible)
        {
            _projectTranscriptionLogDirty = true;
            FlushProjectTranscriptionLog();
            _projectTranscriptionLogTilePhase = 0;
            UpdateProjectTranscriptionLogTiles();
            if (_projectTranscriptionRunning) _projectTranscriptionLogSpinnerTimer.Start();
        }
        else
        {
            _projectTranscriptionLogSpinnerTimer.Stop();
            _projectTranscriptionLogFlushTimer.Stop();
        }
    }

    private void ProjectTranscriptionLogSpinner_OnTick(object? sender, EventArgs e)
    {
        _projectTranscriptionLogTilePhase = (_projectTranscriptionLogTilePhase + 4d / 72d) % 4d;
        UpdateProjectTranscriptionLogTiles();
    }

    private void UpdateProjectTranscriptionLogTiles() => UpdateLoadingTileOpacities(
        new Control[]
        {
            ProjectTranscriptionLogTileTopLeft,
            ProjectTranscriptionLogTileTopRight,
            ProjectTranscriptionLogTileBottomRight,
            ProjectTranscriptionLogTileBottomLeft
        },
        _projectTranscriptionLogTilePhase);

    private string ProjectTranslationCachePath(string projectId) =>
        Path.Combine(ProjectDirectory(projectId), "translation-editor.json");

    private void RefreshProjectTranslation(CaptionProject project)
    {
        _loadingProjectTranslation = true;
        try
        {
            SelectComboText(ProjectTranslationProviderCombo, project.TranslationProvider);
            SelectComboText(ProjectTranslationTargetCombo, project.TranslationTargetLanguage);
            SelectComboText(ProjectSubtitleLayoutCombo, project.SubtitleLayout);
            ProjectCorrectionToggle.IsChecked = project.CorrectSubtitles;
            ProjectReflectToggle.IsChecked = project.ReflectTranslation;
            RefreshProjectTranslationSettingsSummary(project);
            ProjectTranslationFileText.Text = string.IsNullOrWhiteSpace(project.SubtitlePath)
                ? "请先加载 SRT 字幕文件"
                : Path.GetFileName(project.SubtitlePath);

            _projectTranslationSegments.Clear();
            var cachePath = ProjectTranslationCachePath(project.Id);
            var editedSrt = Path.Combine(ProjectDirectory(project.Id), "edited.srt");
            var translatedSrt = Path.Combine(ProjectDirectory(project.Id), "translated.srt");

            if (File.Exists(editedSrt))
            {
                _projectTranslationSegments.AddRange(ParseSrt(File.ReadAllText(editedSrt)));
            }
            else if (File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<List<SubtitleSegment>>(File.ReadAllText(cachePath));
                if (cached is not null) _projectTranslationSegments.AddRange(cached);
            }
            else if (File.Exists(translatedSrt))
            {
                _projectTranslationSegments.AddRange(ParseSrt(File.ReadAllText(translatedSrt)));
                SaveProjectTranslationCache(project.Id);
            }
            else if (!string.IsNullOrWhiteSpace(project.SubtitlePath) && File.Exists(project.SubtitlePath))
            {
                _projectTranslationSegments.AddRange(ParseSrt(File.ReadAllText(project.SubtitlePath)));
                SaveProjectTranslationCache(project.Id);
            }
        }
        catch
        {
            ProjectTranslationStatusText.Text = "字幕编辑缓存无法读取";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#C94444");
        }
        finally
        {
            _loadingProjectTranslation = false;
        }

        RebuildProjectTranslationRows();
        RefreshProjectTranslationReadiness(project);
    }

    private async void ProjectLoadSubtitle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择原始字幕文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("SRT 字幕") { Patterns = ["*.srt"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ProjectTranslationStatusText.Text = "当前文件无法作为本地字幕读取";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#C94444");
            return;
        }

        try
        {
            var segments = ParseSrt(await File.ReadAllTextAsync(path));
            if (segments.Count == 0)
                throw new InvalidDataException("没有识别到有效的 SRT 字幕段落");
            project.SubtitlePath = path;
            project.ProcessedSubtitlePath = null;
            _workspacePreparedProjectId = null;
            var editedPath = Path.Combine(ProjectDirectory(project.Id), "edited.srt");
            if (File.Exists(editedPath)) File.Delete(editedPath);
            var cuesPath = Path.Combine(ProjectDirectory(project.Id), "workspace-cues.json");
            if (File.Exists(cuesPath)) File.Delete(cuesPath);
            project.UpdatedAt = DateTimeOffset.Now;
            _projectTranslationSegments.Clear();
            _projectTranslationSegments.AddRange(segments);
            SaveProjects();
            SaveProjectTranslationCache(project.Id);
            ProjectTranslationFileText.Text = Path.GetFileName(path);
            ProjectTranslationStatusText.Text = $"已加载 {segments.Count} 条字幕";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#278A68");
            RebuildProjectTranslationRows();
            RefreshProjectTranslationReadiness(project);
            RefreshProjectWorkflow(project);
            RefreshProjectProcessing(project);
        }
        catch (Exception exception)
        {
            ProjectTranslationStatusText.Text = $"加载失败：{ShortMessage(exception.Message)}";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#C94444");
        }
    }

    private static List<SubtitleSegment> ParseSrt(string content)
    {
        var result = new List<SubtitleSegment>();
        var blocks = Regex.Split(content.Trim().Replace("\r\n", "\n"), "\n{2,}");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0) continue;
            var timing = lines[timingIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (timing.Length != 2 || !TryParseSrtTime(timing[0], out var start) ||
                !TryParseSrtTime(timing[1], out var end)) continue;

            var textLines = lines.Skip(timingIndex + 1)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (textLines.Count == 0) continue;

            string original;
            string translated = string.Empty;

            if (textLines.Count == 1)
            {
                original = textLines[0];
            }
            else if (textLines.Count == 2)
            {
                // 默认设定第二行为原文，第一行为译文
                translated = textLines[0];
                original = textLines[1];
            }
            else
            {
                // 多行情况：第一行为译文，第二行及之后作为原文
                translated = textLines[0];
                original = string.Join(Environment.NewLine, textLines.Skip(1));
            }

            result.Add(new SubtitleSegment
            {
                Index = result.Count + 1,
                StartMilliseconds = (long)start.TotalMilliseconds,
                EndMilliseconds = (long)end.TotalMilliseconds,
                Original = original,
                Translated = translated
            });
        }
        return result;
    }

    private static bool TryParseSrtTime(string value, out TimeSpan result) =>
        TimeSpan.TryParseExact(value.Trim().Replace(',', '.'), ["hh\\:mm\\:ss\\.fff", "h\\:mm\\:ss\\.fff"],
            CultureInfo.InvariantCulture, out result);

    private static string FormatSubtitleTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string FormatSrtTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    private void RebuildProjectTranslationRows()
    {
        ProjectTranslationEmpty.IsVisible = _projectTranslationSegments.Count == 0;
        ProjectTranslationTableHost.ItemsSource = null;
        ProjectTranslationTableHost.ItemsSource = _projectTranslationSegments;
    }

    private Control CreateSubtitleRow(SubtitleSegment? segment)
    {
        // Avalonia 12 clears a recycled ListBoxItem by asking its retained
        // ContentPresenter to render a transient null item. FuncDataTemplate
        // is still invoked during that cleanup pass, so return an inert visual
        // instead of treating the placeholder as a real subtitle segment.
        if (segment is null)
            return new Border { Height = 0, IsVisible = false };

        var row = new Grid
        {
            DataContext = segment,
            MinHeight = 52,
            ColumnDefinitions = new ColumnDefinitions("50,120,120,*,*")
        };
        row.Children.Add(new TextBlock
        {
            Text = segment.Index.ToString(CultureInfo.InvariantCulture),
            FontSize = 12.5,
            FontWeight = FontWeight.Normal,
            Foreground = Brush.Parse("#6B7280"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var (column, value) in new[]
                 {
                     (1, FormatSubtitleTime(segment.StartMilliseconds)),
                     (2, FormatSubtitleTime(segment.EndMilliseconds))
                 })
        {
            var time = new TextBlock
            {
                Text = value,
                FontSize = 12.5,
                FontWeight = FontWeight.Normal,
                Foreground = Brush.Parse("#374151"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(time, column);
            row.Children.Add(time);
        }

        var original = CreateSubtitleCell(nameof(SubtitleSegment.Original));
        Grid.SetColumn(original, 3);
        row.Children.Add(original);

        var translated = CreateSubtitleCell(nameof(SubtitleSegment.Translated));
        Grid.SetColumn(translated, 4);
        row.Children.Add(translated);

        return new Border
        {
            BorderBrush = Brush.Parse("#F0F1F4"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private TextBox CreateSubtitleCell(string propertyName)
    {
        var cell = new TextBox
        {
            Classes = { "subtitleCell" },
            MinHeight = 40,
            Margin = new Thickness(6, 3),
            Padding = new Thickness(10, 7),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13.5,
            FontWeight = FontWeight.Normal,
            Foreground = Brush.Parse("#18181B")
        };
        cell.Bind(TextBox.TextProperty, new Binding(propertyName) { Mode = BindingMode.TwoWay });
        cell.TextChanged += (_, _) => ScheduleActiveProjectTranslationCacheSave();
        return cell;
    }


    private void ScheduleActiveProjectTranslationCacheSave()
    {
        if (_loadingProjectTranslation || _activeProjectId is null) return;
        _projectTranslationCacheTimer.Stop();
        _projectTranslationCacheTimer.Start();
    }

    private void SaveActiveProjectTranslationCache()
    {
        if (_loadingProjectTranslation || _activeProjectId is null) return;
        SaveProjectTranslationCache(_activeProjectId);
    }

    private void SaveProjectTranslationCache(string projectId)
    {
        try
        {
            if (string.Equals(projectId, _activeProjectId, StringComparison.OrdinalIgnoreCase))
                _workspacePreparedProjectId = null;
            EnsureProjectDirectory(projectId);
            File.WriteAllText(ProjectTranslationCachePath(projectId), JsonSerializer.Serialize(
                _projectTranslationSegments, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Keep editor input usable when a workspace file is temporarily locked.
        }
    }

    private void ProjectTranslationSettings_OnChanged(object? sender, SelectionChangedEventArgs e) =>
        SaveProjectTranslationSettings();

    private void ProjectTranslationSettings_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToggleSwitch.IsCheckedProperty) SaveProjectTranslationSettings();
    }

    private void SaveProjectTranslationSettings()
    {
        if (_loadingProjectTranslation || _activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        project.TranslationProvider = SelectedComboValue(ProjectTranslationProviderCombo);
        project.TranslationTargetLanguage = SelectedComboText(ProjectTranslationTargetCombo);
        project.SubtitleLayout = SelectedComboText(ProjectSubtitleLayoutCombo);
        project.CorrectSubtitles = ProjectCorrectionToggle.IsChecked == true;
        project.ReflectTranslation = ProjectReflectToggle.IsChecked == true;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RefreshProjectTranslationSettingsSummary(project);
        RefreshProjectTranslationReadiness(project);
    }

    private void RefreshProjectTranslationSettingsSummary(CaptionProject project)
    {
        var provider = _translationProfiles.TryGetValue(project.TranslationProvider, out var profile) &&
                       !string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.DisplayName
            : project.TranslationProvider;
        ProjectTranslationSettingsSummaryText.Text =
            $"{provider} · {project.TranslationTargetLanguage} · {project.SubtitleLayout}" +
            (string.IsNullOrWhiteSpace(project.TranslationPrompt) ? string.Empty : " · 自定义提示词");
    }

    private async void ProjectTranslationSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        static StackPanel Field(string title, Control control, string? description = null)
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#373B43")
            });
            if (!string.IsNullOrWhiteSpace(description))
                panel.Children.Add(new TextBlock { Text = description, FontSize = 9.5, Foreground = Brush.Parse("#9097A2") });
            panel.Children.Add(control);
            return panel;
        }

        static ComboBox DialogCombo(IEnumerable<string> items, string selected)
        {
            var values = items.ToArray();
            var combo = new ComboBox
            {
                ItemsSource = values,
                SelectedItem = values.Contains(selected) ? selected : values.FirstOrDefault(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            combo.Classes.Add("settingSelect");
            return combo;
        }

        var providerNames = _translationProfiles.Where(pair => pair.Value.IsEnabled)
            .ToDictionary(pair => pair.Key,
                pair => string.IsNullOrWhiteSpace(pair.Value.DisplayName) ? pair.Key : pair.Value.DisplayName,
                StringComparer.OrdinalIgnoreCase);
        if (providerNames.Count == 0) providerNames["deepseek"] = "DeepSeek";
        var provider = DialogCombo(providerNames.Values, providerNames.GetValueOrDefault(project.TranslationProvider, "DeepSeek"));
        var target = DialogCombo(
            ["简体中文", "繁体中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语"],
            project.TranslationTargetLanguage);
        var layout = DialogCombo(["译文在上", "原文在上", "仅译文", "仅原文"], project.SubtitleLayout);
        var correction = new ToggleSwitch
        {
            IsChecked = project.CorrectSubtitles,
            OnContent = string.Empty,
            OffContent = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var reflection = new ToggleSwitch
        {
            IsChecked = project.ReflectTranslation,
            OnContent = string.Empty,
            OffContent = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var prompt = new TextBox
        {
            Text = project.TranslationPrompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 112,
            MaxHeight = 180,
            Padding = new Thickness(13, 10),
            PlaceholderText = "例如：Minecraft 专有名词保留英文；译文简短口语化；不要使用句号。",
            Background = Brush.Parse("#F7F8FA"),
            BorderBrush = Brush.Parse("#E1E4E8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var topFields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        topFields.Children.Add(Field("翻译模型", provider, "选择当前项目使用的大模型服务"));
        var targetField = Field("目标语言", target, "译文输出语言");
        Grid.SetColumn(targetField, 2);
        topFields.Children.Add(targetField);

        var optionFields = new Grid { ColumnDefinitions = new ColumnDefinitions("1.4*,16,*,16,*"), Margin = new Thickness(0, 18, 0, 0) };
        optionFields.Children.Add(Field("字幕排布", layout, "保存 SRT 时的双语顺序"));
        var correctionField = Field("字幕校正", correction, "修正明显识别错字");
        Grid.SetColumn(correctionField, 2);
        optionFields.Children.Add(correctionField);
        var reflectionField = Field("反思翻译", reflection, "输出前进行校对");
        Grid.SetColumn(reflectionField, 4);
        optionFields.Children.Add(reflectionField);

        var done = new Button
        {
            Content = new TextBlock
            {
                Text = "完成",
                Foreground = Brush.Parse("#111318"),
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Width = 100,
            Height = 38,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(19),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontWeight = FontWeight.Bold
        };
        done.Classes.Add("primary");

        var scale = new ScaleTransform(0.97, 0.97)
        {
            Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(170), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(170), Easing = new CubicEaseOut() }
            }
        };
        var card = new Border
        {
            Padding = new Thickness(26, 23),
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#E1E4E8"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            BoxShadow = BoxShadows.Parse("0 16 48 0 #30111827"),
            Opacity = 0,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = scale,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = Border.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() }
            },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "字幕翻译设置", FontSize = 20, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = "设置仅应用于当前项目，关闭弹窗后自动保存。", FontSize = 10.5, Foreground = Brush.Parse("#858D98"), Margin = new Thickness(0, 5, 0, 20) },
                    topFields,
                    optionFields,
                    new Border { Height = 1, Background = Brush.Parse("#ECEEF1"), Margin = new Thickness(0, 20, 0, 18) },
                    Field("自定义提示词", prompt, "附加到模型服务的系统提示词之后，可为当前视频指定术语与风格"),
                    new TextBlock { Text = "所有修改会随项目自动保存", FontSize = 9.5, Foreground = Brush.Parse("#9299A3"), Margin = new Thickness(2, 15, 0, 0) },
                    done
                }
            }
        };
        done.Margin = new Thickness(0, 14, 0, 0);

        var dialog = new Window
        {
            Width = 650,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            Content = card,
            Title = "字幕翻译设置"
        };
        done.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) dialog.Close();
        };
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        });

        await dialog.ShowDialog(this);
        project.TranslationProvider = providerNames.First(pair => pair.Value == (provider.SelectedItem as string)).Key;
        project.TranslationTargetLanguage = target.SelectedItem as string ?? "简体中文";
        project.SubtitleLayout = layout.SelectedItem as string ?? "译文在上";
        project.CorrectSubtitles = correction.IsChecked == true;
        project.ReflectTranslation = reflection.IsChecked == true;
        project.TranslationPrompt = prompt.Text?.Trim() ?? string.Empty;
        project.UpdatedAt = DateTimeOffset.Now;

        _loadingProjectTranslation = true;
        SelectComboText(ProjectTranslationProviderCombo, project.TranslationProvider);
        SelectComboText(ProjectTranslationTargetCombo, project.TranslationTargetLanguage);
        SelectComboText(ProjectSubtitleLayoutCombo, project.SubtitleLayout);
        ProjectCorrectionToggle.IsChecked = project.CorrectSubtitles;
        ProjectReflectToggle.IsChecked = project.ReflectTranslation;
        _loadingProjectTranslation = false;
        SaveProjects();
        RefreshProjectTranslationSettingsSummary(project);
        RefreshProjectTranslationReadiness(project);
    }

    private void RefreshProjectTranslationReadiness(CaptionProject project)
    {
        var profileId = string.IsNullOrWhiteSpace(project.TranslationProvider) ? "deepseek" : project.TranslationProvider;
        var hasProfile = _translationProfiles.TryGetValue(profileId, out var profile) &&
                         IsProviderConfigured(profile);
        ProjectSaveSubtitleAction.IsEnabled = _projectTranslationSegments.Count > 0;
        ProjectStartTranslationAction.IsEnabled = _projectTranslationSegments.Count > 0 && hasProfile;
        if (_projectTranslationSegments.Count == 0)
        {
            ProjectTranslationStatusText.Text = "等待加载字幕";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#7E8794");
        }
        else if (!hasProfile)
        {
            ProjectTranslationStatusText.Text = "请先在“翻译模型”中完善接口配置";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#B7791F");
        }
    }

    private async void ProjectSaveSubtitle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null || _projectTranslationSegments.Count == 0) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        var suggested = string.IsNullOrWhiteSpace(project.SubtitlePath)
            ? project.Name + ".translated.srt"
            : Path.GetFileNameWithoutExtension(project.SubtitlePath) + ".translated.srt";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存翻译字幕",
            SuggestedFileName = suggested,
            DefaultExtension = "srt",
            FileTypeChoices = [new FilePickerFileType("SRT 字幕") { Patterns = ["*.srt"] }]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(BuildTranslatedSrt(project.SubtitleLayout));
        ProjectTranslationStatusText.Text = $"已保存 {file.Name}";
        ProjectTranslationStatusText.Foreground = Brush.Parse("#278A68");
    }

    private string BuildTranslatedSrt(string layout)
    {
        var builder = new StringBuilder();
        foreach (var segment in _projectTranslationSegments)
        {
            var translated = string.IsNullOrWhiteSpace(segment.Translated) ? segment.Original : segment.Translated;
            var body = layout switch
            {
                "译文在上" => translated + Environment.NewLine + segment.Original,
                "原文在上" => segment.Original + Environment.NewLine + translated,
                "仅原文" => segment.Original,
                _ => translated
            };
            builder.AppendLine(segment.Index.ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTime(segment.StartMilliseconds)).Append(" --> ")
                .AppendLine(FormatSrtTime(segment.EndMilliseconds));
            builder.AppendLine(body.Trim()).AppendLine();
        }
        return builder.ToString();
    }

    private async void ProjectStartTranslation_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_projectTranslationRunning || _activeProjectId is null || _projectTranslationSegments.Count == 0) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null || !_translationProfiles.TryGetValue(project.TranslationProvider, out var profile)) return;

        _projectTranslationCancellation.Cancel();
        _projectTranslationCancellation.Dispose();
        _projectTranslationCancellation = new CancellationTokenSource();
        var token = _projectTranslationCancellation.Token;
        _projectTranslationRunning = true;
        ProjectStartTranslationAction.IsEnabled = false;
        ProjectStartTranslationText.Text = "正在翻译";
        ProjectTranslationCancelAction.IsVisible = true;
        ProjectTranslationStatusText.Foreground = Brush.Parse("#3399F3");

        try
        {
            const int batchSize = 6;
            var terminology = LoadTerminologyResearch(project.Id);
            var pending = _projectTranslationSegments
                .Where(item => string.IsNullOrWhiteSpace(item.Translated))
                .ToList();
            if (pending.Count == 0 && _projectTranslationSegments.Count > 0)
            {
                pending = _projectTranslationSegments.ToList();
            }
            var completed = _projectTranslationSegments.Count - pending.Count;
            ProjectTranslationProgress.Value = completed * 100d / _projectTranslationSegments.Count;
            for (var start = 0; start < pending.Count; start += batchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = pending.Skip(start).Take(batchSize).ToList();
                var glossary = BuildVerifiedTerminologyGlossary(terminology, batch.Select(item => item.Original));
                ProjectTranslationStatusText.Text = $"正在翻译 {completed + 1}–{completed + batch.Count} / {_projectTranslationSegments.Count}";
                var translated = await RequestTranslationBatchWithRetryAsync(profile, project, batch, glossary, token);
                foreach (var result in translated)
                {
                    var segment = batch.FirstOrDefault(item => item.Index == result.Id);
                    if (segment is null) continue;
                    segment.Translated = result.Text.Trim();
                }
                completed += batch.Count;
                SaveProjectTranslationCache(project.Id);
                ProjectTranslationProgress.Value = completed * 100d / _projectTranslationSegments.Count;
                ProjectTranslationStatusText.Text = $"已翻译 {completed} / {_projectTranslationSegments.Count}";
                await Task.Delay(80, token); // Allow the live cells and progress bar to render between batches.
            }

            var automaticOutput = Path.Combine(ProjectDirectory(project.Id), "translated.srt");
            await File.WriteAllTextAsync(automaticOutput, BuildTranslatedSrt(project.SubtitleLayout), new UTF8Encoding(false), token);
            var editedPath = Path.Combine(ProjectDirectory(project.Id), "edited.srt");
            if (File.Exists(editedPath)) File.Delete(editedPath);
            var cuesPath = Path.Combine(ProjectDirectory(project.Id), "workspace-cues.json");
            if (File.Exists(cuesPath)) File.Delete(cuesPath);
            _workspacePreparedProjectId = null;
            ProjectTranslationStatusText.Text = "翻译完成，已自动保存到项目目录";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#278A68");
        }
        catch (OperationCanceledException)
        {
            ProjectTranslationStatusText.Text = "翻译已取消，已完成内容仍然保留";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#7E8794");
        }
        catch (Exception exception)
        {
            ProjectTranslationStatusText.Text = $"翻译失败：{ShortMessage(exception.Message)}";
            ProjectTranslationStatusText.Foreground = Brush.Parse("#C94444");
        }
        finally
        {
            _projectTranslationRunning = false;
            ProjectStartTranslationText.Text = "开始翻译";
            ProjectTranslationCancelAction.IsVisible = false;
            RefreshProjectTranslationReadiness(project);
        }
    }

    private async Task<IReadOnlyList<TranslationBatchItem>> RequestTranslationBatchWithRetryAsync(
        TranslationProviderProfile profile,
        CaptionProject project,
        IReadOnlyList<SubtitleSegment> batch,
        string terminologyGlossary,
        CancellationToken token)
    {
        var collected = new Dictionary<int, TranslationBatchItem>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var missing = batch.Where(item => !collected.ContainsKey(item.Index)).ToList();
            if (missing.Count == 0) break;
            try
            {
                var response = await RequestTranslationBatchAsync(profile, project, missing, terminologyGlossary, token);
                var validIds = missing.Select(item => item.Index).ToHashSet();
                foreach (var item in response)
                    if (validIds.Contains(item.Id) && !string.IsNullOrWhiteSpace(item.Text))
                        collected[item.Id] = item;
            }
            catch (Exception exception) when (attempt < 3 &&
                                               exception is TimeoutException or JsonException or InvalidDataException)
            {
                // Retry the same missing items below with a short, visible pause.
            }

            if (collected.Count < batch.Count && attempt < 3)
            {
                ProjectTranslationStatusText.Text = $"本批返回不完整，正在重试 {attempt + 1} / 3";
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), token);
            }
        }

        var stillMissing = batch.Where(item => !collected.ContainsKey(item.Index)).Select(item => item.Index).ToArray();
        if (stillMissing.Length > 0)
            throw new InvalidDataException($"模型连续三次漏译字幕：{string.Join(", ", stillMissing)}");
        return batch.Select(item => collected[item.Index]).ToArray();
    }

    private async Task<IReadOnlyList<TranslationBatchItem>> RequestTranslationBatchAsync(
        TranslationProviderProfile profile,
        CaptionProject project,
        IReadOnlyList<SubtitleSegment> batch,
        string terminologyGlossary,
        CancellationToken token)
    {
        var input = batch.Select(item => new { id = item.Index, text = item.Original }).ToArray();
        string instruction;
        if (project.ReflectTranslation)
        {
            instruction =
                $"你是一名精通 {project.TranslationTargetLanguage} 的专业影视字幕翻译专家。你的目标是产出符合母语表达习惯、极其地道自然的影视级字幕，彻底消除机器翻译痕迹。\n" +
                "请严格按照【三阶段反思翻译法】进行翻译与重写：\n" +
                $"1. 初步直译：完整保留原字幕含义，保持编号 1:1 对应。\n" +
                "2. 深度反思与机翻审视：批判性检查初译，识别语序僵硬、生搬硬套、语境生硬或脱节问题，检查上下文字幕的流畅连贯性，思考母语者在真实对话中会如何表达。\n" +
                $"3. 地道重写 (Native-Quality Rewrite)：根据反思结论消除所有机翻痕迹，用最自然地道的 {project.TranslationTargetLanguage}（可适度运用地道成语/习惯用语）重写最终译文。\n" +
                (project.CorrectSubtitles ? "4. 识别校对：同时修正源文本中明显的语音识别错字、口癖词与标点错误。\n" : string.Empty) +
                "【输出格式要求】必须严格输出单个 JSON 对象格式：{\"items\":[{\"id\":数字,\"text\":\"最终地道译文\"}]}，不要改动 id，不要输出 Markdown 或解释。";
        }
        else
        {
            instruction =
                $"你是一名精通 {project.TranslationTargetLanguage} 的专业字幕翻译专家。你的目标是产出通顺、自然且易于屏幕阅读的目标语言字幕。\n" +
                $"遵循 {project.TranslationTargetLanguage} 的表达习惯，专有名词保持标准写法，严格保持 1:1 编号对应，不合并拆分。" +
                (project.CorrectSubtitles ? "同时修正明显的语音识别错别字与口癖词。" : string.Empty) +
                "【输出格式要求】必须严格输出单个 JSON 对象格式：{\"items\":[{\"id\":数字,\"text\":\"译文\"}]}，不要输出 Markdown。";
        }

        if (!string.IsNullOrWhiteSpace(terminologyGlossary))
        {
            instruction += "\n以下是已经联网验证的项目术语表。必须保持标准写法和翻译策略一致；不得采用表外猜测修正专名：\n" + terminologyGlossary;
        }
        if (!string.IsNullOrWhiteSpace(project.TranslationPrompt))
        {
            instruction += "\n当前项目的自定义要求：" + project.TranslationPrompt.Trim();
        }

        var userInput = "待翻译字幕：\n" + JsonSerializer.Serialize(input);
        var endpoint = TranslationEndpoint(profile.BaseUrl, profile.Protocol, profile.Model, profile.ApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ApplyProviderAuthentication(request, profile);
        var payload = BuildProviderTextPayload(profile,
            instruction + "\n" + profile.SystemPrompt, userInput, 0.2, 4096);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        HttpResponseMessage response;
        try
        {
            response = await TranslationHttpClient.SendAsync(request, requestTimeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("翻译接口单批请求超过 45 秒");
        }
        using (response)
        {
        var responseText = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"接口返回 {(int)response.StatusCode}：{ShortMessage(responseText)}");
        var modelText = ExtractTranslationResponseText(responseText, profile.Protocol);
        return ParseTranslationItems(modelText);
        }
    }

    internal static string CleanReasoningThinkingTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = Regex.Replace(text, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static IReadOnlyList<TranslationBatchItem> ParseTranslationItems(string modelText)
    {
        var trimmed = CleanReasoningThinkingTags(modelText);
        var objectStart = trimmed.IndexOf('{');
        var objectEnd = trimmed.LastIndexOf('}');
        var arrayStart = trimmed.IndexOf('[');
        var arrayEnd = trimmed.LastIndexOf(']');
        string json;
        if (arrayStart >= 0 && (objectStart < 0 || arrayStart < objectStart) && arrayEnd > arrayStart)
            json = trimmed[arrayStart..(arrayEnd + 1)];
        else if (objectStart >= 0 && objectEnd > objectStart)
            json = trimmed[objectStart..(objectEnd + 1)];
        else if (arrayStart >= 0 && arrayEnd > arrayStart)
            json = trimmed[arrayStart..(arrayEnd + 1)];
        else
            throw new InvalidDataException("模型没有返回可识别的 JSON 字幕结果");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var property)
            ? property
            : root;
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("翻译 JSON 中缺少 items 数组");
        return JsonSerializer.Deserialize<List<TranslationBatchItem>>(items.GetRawText(),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("翻译结果为空");
    }

    private static string TranslationEndpoint(string baseUrl, string protocol, string model, string apiKey)
    {
        var value = baseUrl.TrimEnd('/');
        if (protocol == "google")
        {
            if (!value.Contains("/v1beta", StringComparison.OrdinalIgnoreCase)) value += "/v1beta";
            var modelId = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? model["models/".Length..]
                : model;
            return $"{value}/models/{Uri.EscapeDataString(modelId)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        }
        if (protocol == "ollama")
            return value.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase) ? value : value + "/api/chat";
        if (protocol == "openai-responses")
        {
            foreach (var s in new[] { "/v1/responses", "/responses", "/v1" })
            {
                if (value.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[..^s.Length].TrimEnd('/');
                    break;
                }
            }
            return value + "/responses";
        }
        var suffix = protocol switch
        {
            "anthropic" or "anthropic-messages" => "/messages",
            _ => "/chat/completions"
        };
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value;
        return NormalizeOpenAiCompatibleBaseUrl(value) + suffix;
    }

    private static string NormalizeOpenAiCompatibleBaseUrl(string baseUrl)
    {
        var value = baseUrl.Trim().TrimEnd('/');
        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimEnd('/')
            : value;
        if (Regex.IsMatch(path, @"/v\d+(?:beta\d*)?(?:/openai)?$", RegexOptions.IgnoreCase) ||
            path.Contains("/compatible-mode/v", StringComparison.OrdinalIgnoreCase))
            return value;
        return value + "/v1";
    }

    private static void ApplyProviderAuthentication(HttpRequestMessage request, TranslationProviderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey)) return;
        if (profile.Protocol.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", profile.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            return;
        }
        if (!profile.Protocol.Equals("google", StringComparison.OrdinalIgnoreCase))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
    }

    private static object BuildProviderTextPayload(
        TranslationProviderProfile profile,
        string instruction,
        string userInput,
        double temperature,
        int maxTokens)
    {
        if (profile.Protocol.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
                ["system"] = instruction,
                ["messages"] = new[] { new { role = "user", content = userInput } }
            };

        if (profile.Protocol.Equals("google", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object?>
            {
                ["systemInstruction"] = new { parts = new[] { new { text = instruction } } },
                ["contents"] = new[] { new { role = "user", parts = new[] { new { text = userInput } } } },
                ["generationConfig"] = new
                {
                    temperature,
                    maxOutputTokens = maxTokens,
                    responseMimeType = "application/json"
                }
            };

        if (profile.Protocol.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["messages"] = new[]
                {
                    new { role = "system", content = instruction },
                    new { role = "user", content = userInput }
                },
                ["stream"] = false,
                ["format"] = "json",
                ["options"] = new { temperature, num_predict = maxTokens }
            };

        if (profile.Protocol.Equals("openai-responses", StringComparison.OrdinalIgnoreCase))
        {
            var responses = new Dictionary<string, object?>
            {
                ["model"] = profile.Model,
                ["instructions"] = instruction,
                ["input"] = userInput,
                ["max_output_tokens"] = maxTokens
            };
            if (profile.ReasoningSummary) responses["reasoning"] = new { summary = "auto" };
            return responses;
        }

        var chat = new Dictionary<string, object?>
        {
            ["model"] = profile.Model,
            ["messages"] = new[]
            {
                new { role = profile.DeveloperRole ? "developer" : "system", content = instruction },
                new { role = "user", content = userInput }
            },
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false
        };
        if (profile.BaseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            chat["thinking"] = new { type = "disabled" };
            chat["response_format"] = new { type = "json_object" };
        }
        return chat;
    }

    private static string ExtractTranslationResponseText(string json, string protocol)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if ((protocol == "anthropic" || protocol == "anthropic-messages") && root.TryGetProperty("content", out var anthropicContent))
        {
            if (anthropicContent.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in anthropicContent.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "thinking") continue;
                    if (item.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                        sb.Append(text.GetString());
                }
                if (sb.Length > 0) return sb.ToString();
            }
            if (anthropicContent.ValueKind == JsonValueKind.String)
                return anthropicContent.GetString() ?? string.Empty;
        }
        if (protocol == "openai-responses")
        {
            if (root.TryGetProperty("output_text", out var outputText) && !string.IsNullOrWhiteSpace(outputText.GetString()))
                return outputText.GetString()!;
            if (root.TryGetProperty("output", out var output))
            {
                var sb = new StringBuilder();
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) && itemType.GetString() == "reasoning") continue;
                    if (item.TryGetProperty("content", out var content))
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                                sb.Append(text.GetString());
                        }
                    }
                }
                if (sb.Length > 0) return sb.ToString();
            }
        }
        if (protocol == "google" && root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var googleContent) &&
            googleContent.TryGetProperty("parts", out var parts))
        {
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.GetBoolean()) continue;
                if (part.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                    sb.Append(text.GetString());
            }
            if (sb.Length > 0) return sb.ToString();
        }
        if (protocol == "ollama" && root.TryGetProperty("message", out var ollamaMessage) &&
            ollamaMessage.TryGetProperty("content", out var ollamaText))
            return ollamaText.GetString() ?? string.Empty;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("content", out var chatContent))
            {
                if (chatContent.ValueKind == JsonValueKind.String)
                    return chatContent.GetString() ?? string.Empty;
                if (chatContent.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var part in chatContent.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out var type) && (type.GetString() == "reasoning" || type.GetString() == "thinking")) continue;
                        if (part.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                            sb.Append(text.GetString());
                    }
                    if (sb.Length > 0) return sb.ToString();
                }
            }
        }
        throw new InvalidDataException("无法读取翻译接口响应");
    }

    private void ProjectCancelTranslation_OnClick(object? sender, RoutedEventArgs e) =>
        _projectTranslationCancellation.Cancel();

    private async Task<string?> PromptForProjectNameAsync(
        string suggestedName,
        string dialogTitle = "新建字幕项目",
        string actionLabel = "创建项目")
    {
        var isProviderDialog = dialogTitle.Contains("供应商", StringComparison.Ordinal);
        var dialog = new Window
        {
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            Title = dialogTitle
        };

        var input = new TextBox
        {
            Text = suggestedName,
            Height = 42,
            Padding = new Thickness(14, 0),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush.Parse("#F5F5F7"),
            BorderBrush = Brush.Parse("#E0E2E6"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };
        var error = new TextBlock
        {
            Text = isProviderDialog ? "请输入供应商名称" : "请输入项目名称",
            Foreground = Brush.Parse("#D94B4B"),
            FontSize = 10.5,
            Margin = new Thickness(2, 6, 0, 0),
            IsVisible = false
        };
        var cancel = new Button
        {
            Content = new TextBlock
            {
                Text = "取消",
                Foreground = Brush.Parse("#111318"),
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Width = 82, Height = 36, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(18), Background = Brush.Parse("#F1F1F3"),
            Foreground = Brush.Parse("#111318"), FontWeight = FontWeight.Bold
        };
        var create = new Button
        {
            Content = new TextBlock
            {
                Text = actionLabel,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Width = 100, Height = 36, Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(18),
            Background = Brush.Parse("#3399F3"), Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
            Children = { cancel, create }
        };
        var scale = new ScaleTransform(0.96, 0.96)
        {
            Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(190), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(190), Easing = new CubicEaseOut() }
            }
        };
        var card = new Border
        {
            Padding = new Thickness(26, 24),
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#E5E7EB"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            BoxShadow = BoxShadows.Parse("0 12 36 0 #2B111827"),
            Opacity = 0,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = scale,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = Border.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() }
            },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = dialogTitle, FontSize = 20, FontWeight = FontWeight.Bold },
                    new TextBlock
                    {
                        Text = dialogTitle == "重命名项目"
                            ? "修改后的名称会立即显示在项目列表中。"
                            : isProviderDialog
                                ? "创建后可配置协议、API 地址、密钥和模型。"
                            : "项目会保存在本地，创建后即可导入视频。",
                        FontSize = 11.5,
                        Foreground = Brush.Parse("#7B838F"),
                        Margin = new Thickness(0, 7, 0, 18)
                    },
                    input, error, actions
                }
            }
        };
        dialog.Content = card;

        var closing = false;
        async Task CloseAsync(string? result)
        {
            if (closing) return;
            if (result is not null && string.IsNullOrWhiteSpace(input.Text))
            {
                error.IsVisible = true;
                input.BorderBrush = Brush.Parse("#E95757");
                input.Focus();
                return;
            }
            closing = true;
            card.Opacity = 0;
            scale.ScaleX = 0.97;
            scale.ScaleY = 0.97;
            await Task.Delay(120);
            dialog.Close(result);
        }

        cancel.Click += async (_, _) => await CloseAsync(null);
        create.Click += async (_, _) => await CloseAsync(input.Text?.Trim());
        dialog.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; await CloseAsync(null); }
            else if (args.Key == Key.Enter) { args.Handled = true; await CloseAsync(input.Text?.Trim()); }
        };
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Render);

        return await dialog.ShowDialog<string?>(this);
    }

    private long BeginDownloadUiTask(string title)
    {
        var wasEmpty = _downloadUiTasks.Count == 0;
        var id = ++_nextDownloadUiTaskId;
        var task = new DownloadUiTask(id, title);
        _downloadUiTasks[id] = task;
        AttachDownloadTaskRow(task);
        if (wasEmpty)
        {
            DownloadTaskButtonHost.IsVisible = true;
            _ = _motion.ShowDownloadTaskButtonAsync(DownloadTaskButton);
        }
        _motion.RibbleDownloadTask(DownloadTaskRipple);
        RefreshDownloadTaskUi();
        return id;
    }

    private void AttachDownloadTaskRow(DownloadUiTask task)
    {
        var title = new TextBlock
        {
            Text = task.Title,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        task.PercentText = new TextBlock
        {
            Text = "--",
            FontSize = 10.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#3399F3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        task.PauseButton = new Button { Content = "暂停", Tag = task.Id };
        task.PauseButton.Classes.Add("taskControl");
        task.PauseButton.Click += DownloadTaskPause_OnClick;
        task.CancelButton = new Button { Content = "取消", Tag = task.Id };
        task.CancelButton.Classes.Add("taskControl");
        task.CancelButton.Classes.Add("taskCancel");
        task.CancelButton.Click += DownloadTaskCancel_OnClick;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(task.PercentText, 1);
        header.Children.Add(task.PercentText);
        Grid.SetColumn(task.PauseButton, 2);
        header.Children.Add(task.PauseButton);
        Grid.SetColumn(task.CancelButton, 3);
        task.CancelButton.Margin = new Thickness(7, 0, 0, 0);
        header.Children.Add(task.CancelButton);

        task.StatusText = new TextBlock
        {
            Text = task.Status,
            FontSize = 10,
            Foreground = Brush.Parse("#858A94"),
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        task.ProgressBar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Foreground = Brush.Parse("#3399F3"),
            Background = Brush.Parse("#E1E5EA"),
            Margin = new Thickness(0, 9, 0, 0)
        };
        task.SizeText = TaskMetricText(HorizontalAlignment.Left);
        task.SpeedText = TaskMetricText(HorizontalAlignment.Center);
        task.SpeedText.Margin = new Thickness(9, 0);
        task.RemainingText = TaskMetricText(HorizontalAlignment.Right);
        var metrics = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        metrics.Children.Add(task.SizeText);
        Grid.SetColumn(task.SpeedText, 1);
        metrics.Children.Add(task.SpeedText);
        Grid.SetColumn(task.RemainingText, 2);
        metrics.Children.Add(task.RemainingText);

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        content.Children.Add(header);
        Grid.SetRow(task.StatusText, 1);
        content.Children.Add(task.StatusText);
        Grid.SetRow(task.ProgressBar, 2);
        content.Children.Add(task.ProgressBar);
        Grid.SetRow(metrics, 3);
        content.Children.Add(metrics);

        task.Row = new Border
        {
            Background = Brush.Parse("#F7F8FA"),
            BorderBrush = Brush.Parse("#E7E9ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = content
        };
        DownloadTaskList.Children.Add(task.Row);
    }

    private static TextBlock TaskMetricText(HorizontalAlignment alignment) => new()
    {
        Text = "计算中",
        FontSize = 9.2,
        Foreground = Brush.Parse("#7F8590"),
        HorizontalAlignment = alignment
    };

    private Progress<DeploymentProgress> CreateDownloadProgress(
        long taskId,
        Action<string>? mirrorStatus = null) => new(update => Dispatcher.UIThread.Post(() =>
    {
        if (!_downloadUiTasks.TryGetValue(taskId, out var task) || !task.Running || task.Paused) return;
        task.Status = update.Message;
        // A deployment task can contain several sequential processes. A new
        // phase without a fraction must return to indeterminate mode instead
        // of retaining the previous process's completed 100% value.
        task.Fraction = update.Fraction;
        if (update.DownloadedBytes.HasValue) task.DownloadedBytes = update.DownloadedBytes;
        if (update.TotalBytes.HasValue) task.TotalBytes = update.TotalBytes;
        if (update.BytesPerSecond.HasValue) task.BytesPerSecond = update.BytesPerSecond;
        if (update.Remaining.HasValue) task.Remaining = update.Remaining;
        mirrorStatus?.Invoke(update.Message);
        RefreshDownloadTaskUi();
    }));

    private async Task InstallDeploymentComponentAsync(
        string componentId,
        long taskId,
        IProgress<DeploymentProgress> progress,
        ModelDownloadSource source = ModelDownloadSource.Auto)
    {
        if (!_downloadUiTasks.TryGetValue(taskId, out var task))
            throw new OperationCanceledException();

        while (true)
        {
            task.UserCancellation.Token.ThrowIfCancellationRequested();
            if (task.Paused)
                await task.ResumeSignal.Task.WaitAsync(task.UserCancellation.Token);

            task.PauseRequested = false;
            using var attemptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(task.UserCancellation.Token);
            task.AttemptCancellation = attemptCancellation;
            try
            {
                await _deployment.InstallAsync(componentId, progress, attemptCancellation.Token, source);
                return;
            }
            catch (OperationCanceledException) when (
                task.PauseRequested && !task.UserCancellation.IsCancellationRequested)
            {
                task.Status = "已暂停，点击继续恢复下载";
                task.BytesPerSecond = null;
                task.Remaining = null;
                RefreshDownloadTaskUi();
                await task.ResumeSignal.Task.WaitAsync(task.UserCancellation.Token);
            }
            finally
            {
                if (ReferenceEquals(task.AttemptCancellation, attemptCancellation))
                    task.AttemptCancellation = null;
            }
        }
    }

    private void CompleteDownloadUiTask(long taskId, bool succeeded, string status)
    {
        if (!_downloadUiTasks.TryGetValue(taskId, out var task)) return;
        task.Running = false;
        task.Succeeded = succeeded;
        task.Status = status;
        task.Fraction = succeeded ? 1 : task.Fraction;
        if (succeeded)
        {
            if (task.TotalBytes.HasValue) task.DownloadedBytes = task.TotalBytes;
            task.BytesPerSecond = null;
            task.Remaining = TimeSpan.Zero;
        }
        RefreshDownloadTaskUi();
        _ = RemoveCompletedDownloadUiTaskAsync(taskId);
    }

    private async Task RemoveCompletedDownloadUiTaskAsync(long taskId)
    {
        await Task.Delay(2200);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_downloadUiTasks.Remove(taskId, out var removedTask))
            {
                if (removedTask.Row is not null) DownloadTaskList.Children.Remove(removedTask.Row);
                removedTask.UserCancellation.Dispose();
            }
            RefreshDownloadTaskUi();
            if (_downloadUiTasks.Count != 0) return;
            _downloadTaskPanelOpen = false;
            await _motion.SetDownloadTaskPanelVisibleAsync(DownloadTaskPanel, visible: false);
            await _motion.HideDownloadTaskButtonAsync(DownloadTaskButton);
            if (_downloadUiTasks.Count == 0) DownloadTaskButtonHost.IsVisible = false;
        });
    }

    private void RefreshDownloadTaskUi()
    {
        if (_downloadUiTasks.Count == 0)
        {
            _motion.AnimateDownloadTaskProgress(DownloadTaskProgressFill, 0);
            return;
        }

        var running = _downloadUiTasks.Values.Where(task => task.Running).ToList();
        DownloadTaskCountText.Text = running.Count > 0
            ? $"{running.Count} 个任务正在运行"
            : "任务已完成";
        foreach (var task in _downloadUiTasks.Values.OrderBy(task => task.Id))
            RefreshDownloadTaskRow(task);

        double? combined = null;
        var progressTasks = running.Count > 0 ? running : _downloadUiTasks.Values.ToList();
        if (progressTasks.Any(task => task.Fraction.HasValue))
            combined = progressTasks.Average(task => task.Fraction ?? 0);

        if (combined is null)
        {
            _motion.AnimateDownloadTaskProgress(DownloadTaskProgressFill, 0);
        }
        else
        {
            _motion.AnimateDownloadTaskProgress(DownloadTaskProgressFill, 40 * combined.Value);
        }
    }

    private static void RefreshDownloadTaskRow(DownloadUiTask task)
    {
        if (task.StatusText is null || task.PercentText is null || task.SizeText is null ||
            task.SpeedText is null || task.RemainingText is null || task.ProgressBar is null ||
            task.PauseButton is null || task.CancelButton is null) return;

        task.StatusText.Text = task.Status;
        task.PauseButton.IsEnabled = task.Running;
        task.PauseButton.Content = task.Paused ? "继续" : "暂停";
        task.CancelButton.IsEnabled = task.Running;
        task.SizeText.Text = task.DownloadedBytes.HasValue && task.TotalBytes.HasValue
            ? $"{FormatByteSize(task.DownloadedBytes.Value)} / {FormatByteSize(task.TotalBytes.Value)}"
            : "大小计算中";
        task.SpeedText.Text = task.Paused
            ? "已暂停"
            : task.BytesPerSecond is > 1
                ? $"{FormatByteSize((long)task.BytesPerSecond.Value)}/s"
                : task.Running ? "速度计算中" : "";
        task.RemainingText.Text = task.Paused
            ? ""
            : task.Remaining.HasValue
                ? task.Remaining.Value <= TimeSpan.Zero
                    ? "即将完成"
                    : $"剩余 {FormatRemainingTime(task.Remaining.Value)}"
                : task.Running ? "剩余时间计算中" : "";

        task.ProgressBar.IsIndeterminate = !task.Fraction.HasValue;
        if (!task.Fraction.HasValue)
        {
            task.ProgressBar.Value = 0;
            task.PercentText.Text = "--";
        }
        else
        {
            var percent = Math.Clamp(task.Fraction.Value * 100, 0, 100);
            task.ProgressBar.Value = percent;
            task.PercentText.Text = $"{percent:0}%";
        }
    }

    private async void DownloadTaskButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadUiTasks.Count == 0) return;
        _downloadTaskPanelOpen = !_downloadTaskPanelOpen;
        await _motion.SetDownloadTaskPanelVisibleAsync(DownloadTaskPanel, _downloadTaskPanelOpen);
    }

    private void DownloadTaskPause_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long taskId } ||
            !_downloadUiTasks.TryGetValue(taskId, out var task) || !task.Running) return;

        if (task.Paused)
        {
            task.Paused = false;
            task.Status = "正在恢复下载…";
            task.ResumeSignal.TrySetResult(true);
        }
        else
        {
            task.PreparePause();
            task.Status = "正在暂停…";
            task.BytesPerSecond = null;
            task.Remaining = null;
            task.AttemptCancellation?.Cancel();
        }
        RefreshDownloadTaskUi();
        e.Handled = true;
    }

    private void DownloadTaskCancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long taskId } ||
            !_downloadUiTasks.TryGetValue(taskId, out var task) || !task.Running) return;
        task.Status = "正在取消下载…";
        task.Paused = false;
        task.UserCancellation.Cancel();
        task.AttemptCancellation?.Cancel();
        task.ResumeSignal.TrySetCanceled(task.UserCancellation.Token);
        RefreshDownloadTaskUi();
        e.Handled = true;
    }

    private async void WindowRoot_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_downloadTaskPanelOpen || e.Source is not Visual source) return;
        var insidePanel = ReferenceEquals(source, DownloadTaskPanel) ||
                          DownloadTaskPanel.GetVisualDescendants().Contains(source);
        var insideButton = ReferenceEquals(source, DownloadTaskButton) ||
                           DownloadTaskButton.GetVisualDescendants().Contains(source);
        if (insidePanel || insideButton) return;

        _downloadTaskPanelOpen = false;
        await _motion.SetDownloadTaskPanelVisibleAsync(DownloadTaskPanel, visible: false);
    }

    private static string DownloadTaskTitle(string id) => id switch
    {
        "python-runtime" => "Python 3.12 基础环境",
        "whisper-runtime" => "Faster-Whisper / CTranslate2",
        "qwen-runtime" => "PyTorch / Qwen-ASR",
        "nvidia-runtime" => "NVIDIA Transformers",
        "funasr-runtime" => "FunASR / PyTorch",
        "nemo-runtime" => "NVIDIA NeMo / PyTorch",
        "moss-runtime" => "MOSS / Transformers",
        "whisper-tiny" => "Whisper Tiny",
        "whisper-base" => "Whisper Base",
        "whisper-small" => "Whisper Small",
        "whisper-medium" => "Whisper Medium",
        "whisper-large-v3" => "Whisper Large V3",
        "whisper-v3-turbo" => "Whisper Large V3 Turbo",
        "qwen-0.6b" => "Qwen3-ASR 0.6B",
        "qwen-1.7b" => "Qwen3-ASR 1.7B",
        "funasr-nano" => "Fun-ASR Nano 2512",
        "sensevoice-small" => "SenseVoice Small",
        "nvidia-parakeet-v3" => "NVIDIA Parakeet TDT 0.6B V3",
        "nvidia-canary-v2" => "NVIDIA Canary 1B V2",
        "moss-0.9b" => "MOSS Transcribe-Diarize 0.9B",
        _ => "运行环境"
    };

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var scaled = (double)value;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }
        return unit == 0 ? $"{scaled:0} {units[unit]}" : $"{scaled:0.##} {units[unit]}";
    }

    private static string FormatRemainingTime(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分";
        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes} 分 {remaining.Seconds} 秒";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒";
    }

    private async void DeploymentAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        if (ModelDeployments.ContainsKey(id))
        {
            await OpenModelAsync(id);
            return;
        }

        var states = _deployment.Inspect();
        if (states.TryGetValue(id, out var current) && current.Installed)
        {
            DeploymentManager.OpenFolder(current.Path);
            return;
        }

        button.IsEnabled = false;
        button.Content = id.Contains("runtime", StringComparison.Ordinal) ? "安装中…" : "下载中…";
        DeploymentSummaryText.Text = "正在部署组件，请保持应用开启";
        var downloadTaskId = BeginDownloadUiTask(DownloadTaskTitle(id));
        var progress = CreateDownloadProgress(downloadTaskId,
            message => DeploymentSummaryText.Text = message);
        var downloadSucceeded = false;
        var completionStatus = "部署已停止";

        try
        {
            await InstallDeploymentComponentAsync(id, downloadTaskId, progress);
            RefreshDeploymentStatus();
            downloadSucceeded = true;
            completionStatus = "部署完成";
        }
        catch (OperationCanceledException)
        {
            DeploymentSummaryText.Text = "部署已取消";
            completionStatus = "部署已取消";
        }
        catch (Exception exception)
        {
            DeploymentSummaryText.Text = $"部署失败：{ShortMessage(exception.Message)}";
            completionStatus = DeploymentSummaryText.Text;
        }
        finally
        {
            CompleteDownloadUiTask(downloadTaskId, downloadSucceeded, completionStatus);
            button.IsEnabled = true;
            RefreshDeploymentStatus(keepSummaryOnError: DeploymentSummaryText.Text.StartsWith("部署失败", StringComparison.Ordinal));
        }
    }

    private async void ModelRow_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Control { Tag: string id }) return;
        if (!ModelDeployments.ContainsKey(id)) return;
        if (sender is Border row)
        {
            _pressedModelRows.Remove(row);
            _motion.AnimateModelListItem(row, hovered: row.IsPointerOver, pressed: false);
        }
        e.Handled = true;
        await OpenModelAsync(id);
    }

    private static string RuntimeIdForWorkerEngine(string engine) => engine switch
    {
        "nvidia-parakeet-tdt-0.6b-v3" => "nvidia-runtime",
        _ => engine.StartsWith("qwen3-asr-", StringComparison.OrdinalIgnoreCase)
            ? "qwen-runtime"
            : "whisper-runtime"
    };

    private async Task OpenModelAsync(string id)
    {
        var states = _deployment.Inspect();
        if (states.TryGetValue(id, out var state) && state.Installed)
        {
            await ShowModelConfigurationAsync(id, states);
            return;
        }

        await ShowModelDeploymentDetailAsync(id);
    }

    private async Task ShowModelConfigurationAsync(
        string id,
        IReadOnlyDictionary<string, DeploymentState>? states = null)
    {
        if (!ModelDeployments.ContainsKey(id)) return;
        states ??= _deployment.Inspect();
        if (!states.TryGetValue(id, out var state) || !state.Installed)
        {
            await ShowModelDeploymentDetailAsync(id);
            return;
        }

        RefreshConfigurableModels(states);
        var selected = _configurableModels.FirstOrDefault(model =>
            string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(selected.Id)) return;

        var selectedIndex = _configurableModels.FindIndex(model =>
            string.Equals(model.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        if (Equals(ConfiguredModelCombo.SelectedItem, selected.Name))
        {
            _configuredModelIndex = selectedIndex;
            UpdateConfiguredModelIndicator(selectedIndex, animate: false);
            ApplyConfiguredModelSelection(selected.Id, selected.Name);
        }
        else
        {
            ConfiguredModelCombo.SelectedItem = selected.Name;
        }
        await SwitchModelTabAsync(settings: true);
    }

    private async Task ShowModelDeploymentDetailAsync(string id)
    {
        if (!ModelDeployments.TryGetValue(id, out var info) || !ModelDownloadView.IsVisible) return;
        _selectedDeploymentModelId = id;
        ModelDetailTitle.Text = info.Title;
        ModelDetailSubtitle.Text = info.Subtitle;
        ModelWeightDescription.Text = info.WeightDescription;
        ModelRuntimeTitle.Text = info.RuntimeTitle;
        ModelRuntimeDescription.Text = info.RuntimeDescription;
        if (!ModelLogoCache.TryGetValue(info.AssetUri, out var modelLogo))
        {
            using var stream = AssetLoader.Open(new Uri(info.AssetUri));
            modelLogo = new Bitmap(stream);
            ModelLogoCache[info.AssetUri] = modelLogo;
        }
        ModelDetailIcon.Source = modelLogo;
        RefreshModelDeploymentDetail();
        _ = RefreshCudaDetailAsync();

        ModelsPageHeader.IsHitTestVisible = false;
        SetupButtonMotion(ModelDeploymentDetailView);
        _modelTabNavigation.Cancel();
        _modelTabNavigation.Dispose();
        _modelTabNavigation = new CancellationTokenSource();
        try
        {
            await _motion.SlideContentTransitionAsync(
                ModelDownloadView, ModelDeploymentDetailView, forward: true, _modelTabNavigation.Token,
                afterExit: () => ModelsPageHeader.IsVisible = false);
        }
        catch (OperationCanceledException)
        {
            // Navigation owns the visible page after cancellation.
        }
    }

    private async void ModelDetailBack_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!ModelDeploymentDetailView.IsVisible) return;
        _modelTabNavigation.Cancel();
        _modelTabNavigation.Dispose();
        _modelTabNavigation = new CancellationTokenSource();
        try
        {
            await _motion.SlideContentTransitionAsync(
                ModelDeploymentDetailView, ModelDownloadView, forward: false, _modelTabNavigation.Token,
                afterExit: () => ModelsPageHeader.IsVisible = true);
            ModelsPageHeader.IsHitTestVisible = true;
        }
        catch (OperationCanceledException)
        {
            // Navigation owns the visible page after cancellation.
        }
    }

    private async void ModelDetailPrimary_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedDeploymentModelId is null ||
            !ModelDeployments.TryGetValue(_selectedDeploymentModelId, out var info)) return;

        var states = _deployment.Inspect();
        var repairQwenAcceleration = false;
        if (states[info.Id].Installed)
        {
            if (info.Id.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase) &&
                SelectedCudaRuntimeVersion() != "cpu")
            {
                var cudaStatus = await _deployment.GetCudaRuntimeStatusAsync(CancellationToken.None);
                repairQwenAcceleration = cudaStatus.HasNvidiaGpu &&
                    (!cudaStatus.Ready || !cudaStatus.TorchReady ||
                     !string.Equals(cudaStatus.InstalledVersion, SelectedCudaRuntimeVersion(), StringComparison.OrdinalIgnoreCase));
            }
            if (!repairQwenAcceleration)
            {
                ModelDetailProgress.Text = "模型已经可以使用；识别参数请在“模型管理”中调整";
                return;
            }
        }

        ModelDetailPrimaryAction.IsEnabled = false;
        var downloadTaskId = BeginDownloadUiTask(info.Title);
        var progress = CreateDownloadProgress(downloadTaskId, message =>
        {
            ModelDetailProgress.Text = message;
            DeploymentSummaryText.Text = message;
        });
        var downloadSucceeded = false;
        var completionStatus = "部署已停止";

        try
        {
            var downloadSource = SelectedModelDownloadSource();
            var taskToken = _downloadUiTasks[downloadTaskId].UserCancellation.Token;
            var cudaVersion = SelectedCudaRuntimeVersion();
            var usesTorch = info.Id.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase);
            if ((info.Id.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase) || usesTorch) && cudaVersion != "cpu")
            {
                var cudaStatus = await _deployment.GetCudaRuntimeStatusAsync(taskToken);
                if (cudaStatus.HasNvidiaGpu &&
                    (!cudaStatus.Ready || !string.Equals(cudaStatus.InstalledVersion, cudaVersion, StringComparison.OrdinalIgnoreCase)))
                    await _deployment.InstallCudaRuntimeAsync(cudaVersion, progress, taskToken);
                else if (!cudaStatus.HasNvidiaGpu)
                    ((IProgress<DeploymentProgress>)progress).Report(
                        new DeploymentProgress("未检测到 NVIDIA 显卡，将使用 CPU 识别"));

                if (usesTorch && cudaStatus.HasNvidiaGpu &&
                    (!cudaStatus.TorchReady || !string.Equals(cudaStatus.InstalledVersion, cudaVersion, StringComparison.OrdinalIgnoreCase)))
                    await _deployment.InstallTorchCudaAsync(cudaVersion, progress, taskToken);
            }
            foreach (var componentId in new[] { "python-runtime", info.RuntimeId, info.Id })
            {
                states = _deployment.Inspect();
                if (states[componentId].Installed) continue;

                ModelDetailProgress.Text = componentId == "python-runtime"
                    ? "正在准备 Python 3.12 基础环境…"
                    : componentId == info.Id
                    ? $"正在下载 {info.Title}…"
                    : $"正在安装 {info.RuntimeTitle}…";
                await InstallDeploymentComponentAsync(
                    componentId, downloadTaskId, progress, downloadSource);
                RefreshDeploymentStatus();
                RefreshModelDeploymentDetail();
            }

            var accelerationWarning = info.Id.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase)
                ? await _deployment.GetWhisperCudaWarningAsync(
                    _downloadUiTasks[downloadTaskId].UserCancellation.Token)
                : null;
            ModelDetailProgress.Text = string.IsNullOrWhiteSpace(accelerationWarning)
                ? "部署完成，模型已经可以使用"
                : $"部署完成；{accelerationWarning}";
            downloadSucceeded = true;
            completionStatus = ModelDetailProgress.Text;
        }
        catch (OperationCanceledException)
        {
            ModelDetailProgress.Text = "部署已取消";
            completionStatus = ModelDetailProgress.Text;
        }
        catch (Exception exception)
        {
            ModelDetailProgress.Text = $"部署失败：{ShortMessage(exception.Message)}";
            completionStatus = ModelDetailProgress.Text;
        }
        finally
        {
            CompleteDownloadUiTask(downloadTaskId, downloadSucceeded, completionStatus);
            ModelDetailPrimaryAction.IsEnabled = true;
            RefreshDeploymentStatus(keepSummaryOnError:
                ModelDetailProgress.Text?.StartsWith("部署失败", StringComparison.Ordinal) == true);
            RefreshModelDeploymentDetail();
            _ = RefreshCudaDetailAsync();
        }
    }

    private ModelDownloadSource SelectedModelDownloadSource() => ModelMirrorCombo.SelectedIndex switch
    {
        1 => ModelDownloadSource.HfMirror,
        2 => ModelDownloadSource.HuggingFace,
        _ => ModelDownloadSource.Auto
    };

    private void RefreshModelDeploymentDetail()
    {
        if (_selectedDeploymentModelId is null ||
            !ModelDeployments.TryGetValue(_selectedDeploymentModelId, out var info)) return;

        var states = _deployment.Inspect();
        SetDetailComponentState(ModelWeightStatus, states[info.Id], "可以下载");
        SetDetailComponentState(ModelPythonStatus, states["python-runtime"], "将自动安装");
        SetDetailComponentState(ModelRuntimeStatus, states[info.RuntimeId], "将自动安装");
        SetComponentAction(ModelWeightUninstallAction, states[info.Id].Installed, "卸载该模型");
        SetComponentAction(ModelPythonProtectedAction, states["python-runtime"].Installed, "受保护", isProtected: true);
        SetComponentAction(ModelRuntimeUninstallAction, states[info.RuntimeId].Installed, "卸载");

        var installed = states[info.Id].Installed;
        ModelDetailMode.Text = installed ? "模型管理" : "模型下载";
        ModelDetailPrimaryText.Text = installed ? "模型已就绪" : "下载并应用";
        ModelDetailPrimaryIcon.IsVisible = !installed;
        ModelDetailManageIcon.IsVisible = installed;
        if (!ModelDetailPrimaryAction.IsEnabled) return;
        ModelDetailProgress.Text = installed
            ? "模型已加载；识别参数可在“模型管理”中调整"
            : "将自动补齐缺失的附属运行环境";
    }

    private string SelectedCudaRuntimeVersion()
    {
        var tag = (CudaVersionCombo?.SelectedItem as ComboBoxItem)?.Tag as string
            ?? (SettingsCudaVersionCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag ?? "12.8";
    }

    private async Task RefreshCudaDetailAsync()
    {
        var generation = ++_cudaStatusGeneration;
        if (CudaStatusTitle != null) CudaStatusTitle.Text = "正在检测 GPU 与 CUDA…";
        if (CudaGpuInfoText != null) CudaGpuInfoText.Text = "读取 NVIDIA 显卡与驱动信息";
        if (CudaInstallAction != null) CudaInstallAction.IsEnabled = false;

        if (SettingsCudaStatusTitle != null) SettingsCudaStatusTitle.Text = "正在检测 GPU 与 CUDA…";
        if (SettingsCudaGpuInfoText != null) SettingsCudaGpuInfoText.Text = "读取 NVIDIA 显卡与驱动信息";
        if (SettingsCudaInstallAction != null) SettingsCudaInstallAction.IsEnabled = false;

        try
        {
            var status = await _deployment.GetCudaRuntimeStatusAsync(CancellationToken.None);
            if (generation != _cudaStatusGeneration) return;
            _lastCudaStatus = status;
            if (!status.HasNvidiaGpu)
            {
                var bg = Brush.Parse("#F4F6F8");
                var border = Brush.Parse("#D8DEE6");

                if (CudaStatusCard != null)
                {
                    CudaStatusCard.Background = bg;
                    CudaStatusCard.BorderBrush = border;
                }
                if (CudaHeaderCheckIcon != null) CudaHeaderCheckIcon.IsVisible = false;
                if (CudaStatusTitle != null) CudaStatusTitle.Text = "未检测到 NVIDIA GPU";
                if (CudaGpuInfoText != null) CudaGpuInfoText.Text = "当前模型将使用 CPU 运行";
                if (CudaStatusDetailText != null) CudaStatusDetailText.Text = "CUDA 自动安装仅适用于 NVIDIA 显卡";

                if (SettingsCudaStatusCard != null)
                {
                    SettingsCudaStatusCard.Background = bg;
                    SettingsCudaStatusCard.BorderBrush = border;
                }
                if (SettingsCudaHeaderCheckIcon != null) SettingsCudaHeaderCheckIcon.IsVisible = false;
                if (SettingsCudaStatusTitle != null) SettingsCudaStatusTitle.Text = "未检测到 NVIDIA GPU";
                if (SettingsCudaGpuInfoText != null) SettingsCudaGpuInfoText.Text = "当前模型将使用 CPU 运行";
                if (SettingsCudaStatusDetailText != null) SettingsCudaStatusDetailText.Text = "CUDA 自动安装仅适用于 NVIDIA 显卡";
            }
            else
            {
                var gpuInfo = $"{status.GpuName} · 驱动版本 {status.DriverVersion ?? "未知"}";
                if (CudaGpuInfoText != null) CudaGpuInfoText.Text = gpuInfo;
                if (SettingsCudaGpuInfoText != null) SettingsCudaGpuInfoText.Text = gpuInfo;

                if (status.Ready)
                {
                    var bg = Brush.Parse("#EAF7F0");
                    var border = Brush.Parse("#83C9A2");
                    var title = status.TorchReady
                        ? $"GPU 加速已就绪 · CUDA {status.InstalledVersion}"
                        : $"CUDA {status.InstalledVersion} 已安装 · PyTorch 当前仅支持 CPU";
                    var detail = status.TorchReady
                        ? "AstraCat 私有 CUDA 运行库与 PyTorch GPU 自检均已通过"
                        : "Qwen 将回退 CPU；重新部署 Qwen 可自动安装 CUDA 版 PyTorch";
                    var greenText = Brush.Parse("#16944A");

                    if (CudaStatusCard != null)
                    {
                        CudaStatusCard.Background = bg;
                        CudaStatusCard.BorderBrush = border;
                    }
                    if (CudaHeaderCheckIcon != null) CudaHeaderCheckIcon.IsVisible = true;
                    if (CudaStatusTitle != null)
                    {
                        CudaStatusTitle.Text = title;
                        CudaStatusTitle.Foreground = greenText;
                    }
                    if (CudaStatusDetailText != null) CudaStatusDetailText.Text = detail;

                    if (SettingsCudaStatusCard != null)
                    {
                        SettingsCudaStatusCard.Background = bg;
                        SettingsCudaStatusCard.BorderBrush = border;
                    }
                    if (SettingsCudaHeaderCheckIcon != null) SettingsCudaHeaderCheckIcon.IsVisible = true;
                    if (SettingsCudaStatusTitle != null)
                    {
                        SettingsCudaStatusTitle.Text = title;
                        SettingsCudaStatusTitle.Foreground = greenText;
                    }
                    if (SettingsCudaStatusDetailText != null) SettingsCudaStatusDetailText.Text = detail;

                    SelectCudaVersion(status.InstalledVersion);
                }
                else
                {
                    var bg = Brush.Parse("#FFF8E8");
                    var border = Brush.Parse("#E8C66A");
                    var title = "检测到 NVIDIA GPU · CUDA 尚未安装";
                    var detail = "下载模型时可自动安装所选 CUDA 运行库，也可以现在安装";
                    var amberText = Brush.Parse("#111827");

                    if (CudaStatusCard != null)
                    {
                        CudaStatusCard.Background = bg;
                        CudaStatusCard.BorderBrush = border;
                    }
                    if (CudaHeaderCheckIcon != null) CudaHeaderCheckIcon.IsVisible = false;
                    if (CudaStatusTitle != null)
                    {
                        CudaStatusTitle.Text = title;
                        CudaStatusTitle.Foreground = amberText;
                    }
                    if (CudaStatusDetailText != null) CudaStatusDetailText.Text = detail;

                    if (SettingsCudaStatusCard != null)
                    {
                        SettingsCudaStatusCard.Background = bg;
                        SettingsCudaStatusCard.BorderBrush = border;
                    }
                    if (SettingsCudaHeaderCheckIcon != null) SettingsCudaHeaderCheckIcon.IsVisible = false;
                    if (SettingsCudaStatusTitle != null)
                    {
                        SettingsCudaStatusTitle.Text = title;
                        SettingsCudaStatusTitle.Foreground = amberText;
                    }
                    if (SettingsCudaStatusDetailText != null) SettingsCudaStatusDetailText.Text = detail;
                }
            }
            UpdateCudaInstallAction(status);
        }
        catch (Exception exception)
        {
            if (generation != _cudaStatusGeneration) return;
            _lastCudaStatus = null;
            var err = ShortMessage(exception.Message);

            if (CudaHeaderCheckIcon != null) CudaHeaderCheckIcon.IsVisible = false;
            if (CudaStatusTitle != null) CudaStatusTitle.Text = "GPU 检测失败";
            if (CudaGpuInfoText != null) CudaGpuInfoText.Text = err;
            if (CudaStatusDetailText != null) CudaStatusDetailText.Text = "仍可选择仅 CPU 模式继续使用模型";
            if (CudaInstallAction != null) CudaInstallAction.IsEnabled = false;

            if (SettingsCudaHeaderCheckIcon != null) SettingsCudaHeaderCheckIcon.IsVisible = false;
            if (SettingsCudaStatusTitle != null) SettingsCudaStatusTitle.Text = "GPU 检测失败";
            if (SettingsCudaGpuInfoText != null) SettingsCudaGpuInfoText.Text = err;
            if (SettingsCudaStatusDetailText != null) SettingsCudaStatusDetailText.Text = "仍可选择仅 CPU 模式继续使用模型";
            if (SettingsCudaInstallAction != null) SettingsCudaInstallAction.IsEnabled = false;
        }
    }

    private bool _isSyncingCudaVersion;

    private void SelectCudaVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        _isSyncingCudaVersion = true;
        try
        {
            if (CudaVersionCombo?.Items != null)
            {
                var item = CudaVersionCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, version, StringComparison.OrdinalIgnoreCase));
                if (item is not null) CudaVersionCombo.SelectedItem = item;
            }
            if (SettingsCudaVersionCombo?.Items != null)
            {
                var item = SettingsCudaVersionCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, version, StringComparison.OrdinalIgnoreCase));
                if (item is not null) SettingsCudaVersionCombo.SelectedItem = item;
            }
        }
        finally
        {
            _isSyncingCudaVersion = false;
        }
    }

    private void UpdateCudaInstallAction(CudaRuntimeStatus? status = null)
    {
        var version = SelectedCudaRuntimeVersion();
        if (version == "cpu")
        {
            if (CudaInstallActionText != null) CudaInstallActionText.Text = "使用 CPU";
            if (CudaInstalledIcon != null) CudaInstalledIcon.IsVisible = false;
            if (CudaInstallAction != null)
            {
                CudaInstallAction.Classes.Set("cudaInstall", false);
                CudaInstallAction.Classes.Set("cudaInstalled", false);
                CudaInstallAction.IsEnabled = false;
                CudaInstallAction.IsHitTestVisible = false;
            }

            if (SettingsCudaInstallActionText != null) SettingsCudaInstallActionText.Text = "使用 CPU";
            if (SettingsCudaInstalledIcon != null) SettingsCudaInstalledIcon.IsVisible = false;
            if (SettingsCudaInstallAction != null)
            {
                SettingsCudaInstallAction.Classes.Set("cudaInstall", false);
                SettingsCudaInstallAction.Classes.Set("cudaInstalled", false);
                SettingsCudaInstallAction.IsEnabled = false;
                SettingsCudaInstallAction.IsHitTestVisible = false;
            }
            return;
        }

        var installed = status?.Ready == true &&
                        string.Equals(status.InstalledVersion, version, StringComparison.OrdinalIgnoreCase);
        var label = installed ? $"CUDA {version} 已安装" : $"下载安装 CUDA {version}";
        var canInstall = status?.HasNvidiaGpu != false && !installed;

        if (CudaInstallActionText != null) CudaInstallActionText.Text = label;
        if (CudaInstalledIcon != null) CudaInstalledIcon.IsVisible = installed;
        if (CudaInstallAction != null)
        {
            CudaInstallAction.Classes.Set("cudaInstall", !installed);
            CudaInstallAction.Classes.Set("cudaInstalled", installed);
            CudaInstallAction.IsEnabled = true;
            CudaInstallAction.IsHitTestVisible = canInstall;
        }

        if (SettingsCudaInstallActionText != null) SettingsCudaInstallActionText.Text = label;
        if (SettingsCudaInstalledIcon != null) SettingsCudaInstalledIcon.IsVisible = installed;
        if (SettingsCudaInstallAction != null)
        {
            SettingsCudaInstallAction.Classes.Set("cudaInstall", !installed);
            SettingsCudaInstallAction.Classes.Set("cudaInstalled", installed);
            SettingsCudaInstallAction.IsEnabled = true;
            SettingsCudaInstallAction.IsHitTestVisible = canInstall;
        }
    }

    private async void CudaRefresh_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshCudaDetailAsync();

    private void CudaVersion_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isSyncingCudaVersion && CudaVersionCombo?.SelectedItem is ComboBoxItem item && SettingsCudaVersionCombo != null)
        {
            _isSyncingCudaVersion = true;
            try
            {
                var tag = item.Tag as string;
                var matching = SettingsCudaVersionCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(c => string.Equals(c.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
                if (matching != null && SettingsCudaVersionCombo.SelectedItem != matching)
                    SettingsCudaVersionCombo.SelectedItem = matching;
            }
            finally
            {
                _isSyncingCudaVersion = false;
            }
        }
        UpdateCudaInstallAction(_lastCudaStatus);
    }

    private async void SettingsCudaRefresh_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshCudaDetailAsync();

    private void SettingsCudaVersion_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isSyncingCudaVersion && SettingsCudaVersionCombo?.SelectedItem is ComboBoxItem item && CudaVersionCombo != null)
        {
            _isSyncingCudaVersion = true;
            try
            {
                var tag = item.Tag as string;
                var matching = CudaVersionCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(c => string.Equals(c.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
                if (matching != null && CudaVersionCombo.SelectedItem != matching)
                    CudaVersionCombo.SelectedItem = matching;
            }
            finally
            {
                _isSyncingCudaVersion = false;
            }
        }
        UpdateCudaInstallAction(_lastCudaStatus);
    }

    private void SettingsCudaInstall_OnClick(object? sender, RoutedEventArgs e) =>
        CudaInstall_OnClick(sender, e);

    private async void CudaInstall_OnClick(object? sender, RoutedEventArgs e)
    {
        var version = SelectedCudaRuntimeVersion();
        if (version == "cpu") return;
        if (CudaInstallAction != null) CudaInstallAction.IsEnabled = false;
        if (SettingsCudaInstallAction != null) SettingsCudaInstallAction.IsEnabled = false;

        var taskId = BeginDownloadUiTask($"CUDA {version} GPU 加速");
        var progress = CreateDownloadProgress(taskId, message =>
        {
            ModelDetailProgress.Text = message;
            if (CudaStatusDetailText != null) CudaStatusDetailText.Text = message;
            if (SettingsCudaStatusDetailText != null) SettingsCudaStatusDetailText.Text = message;
        });
        var succeeded = false;
        var completion = "CUDA 安装已停止";
        try
        {
            await _deployment.InstallCudaRuntimeAsync(
                version, progress, _downloadUiTasks[taskId].UserCancellation.Token);
            succeeded = true;
            completion = $"CUDA {version} GPU 加速安装完成";
            ModelDetailProgress.Text = completion;
        }
        catch (OperationCanceledException)
        {
            completion = "CUDA 安装已取消";
            ModelDetailProgress.Text = completion;
        }
        catch (Exception exception)
        {
            completion = $"CUDA 安装失败：{ShortMessage(exception.Message)}";
            ModelDetailProgress.Text = completion;
        }
        finally
        {
            CompleteDownloadUiTask(taskId, succeeded, completion);
            await RefreshCudaDetailAsync();
            RefreshDeploymentStatus();
        }
    }

    private static void SetComponentAction(
        Button action,
        bool installed,
        string installedText,
        bool isProtected = false)
    {
        var protectedState = installed && isProtected;
        action.Classes.Set("componentInstallAction", !installed);
        action.Classes.Set("componentUninstallAction", installed && !isProtected);
        action.Classes.Set("componentProtectedAction", protectedState);
        action.IsHitTestVisible = !protectedState;
        action.Content = protectedState
            ? new TextBlock { Text = installedText, Foreground = Brush.Parse("#89919D"), FontWeight = FontWeight.Bold }
            : UninstallButtonLabel(installed ? installedText : "安装");
    }

    private async void ModelComponentAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string componentKind } action ||
            _selectedDeploymentModelId is null ||
            !ModelDeployments.TryGetValue(_selectedDeploymentModelId, out var info)) return;

        var componentId = componentKind switch
        {
            "model" => info.Id,
            "python" => "python-runtime",
            _ => info.RuntimeId
        };
        var states = _deployment.Inspect();
        if (!states.TryGetValue(componentId, out var state)) return;
        if (!state.Installed)
        {
            await InstallModelComponentAsync(action, componentKind, info);
            return;
        }

        string title;
        string message;
        if (componentKind == "model")
        {
            title = $"卸载 {info.Title}？";
            message = "将删除本地模型权重，但保留该模型的参数配置。之后重新下载时可以继续使用原有配置。";
        }
        else
        {
            var affectedModels = ModelDeployments.Values
                .Where(model => model.RuntimeId == info.RuntimeId && states[model.Id].Installed)
                .Select(model => model.Title)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var affectedText = affectedModels.Length == 0
                ? "当前没有已安装模型依赖它。"
                : $"卸载后这些模型将暂时无法运行：{string.Join("、", affectedModels)}。";
            title = $"卸载 {info.RuntimeTitle}？";
            message = $"这是多个模型共用的运行环境。{affectedText}模型权重和参数配置不会被删除。";
        }

        if (!await ConfirmComponentUninstallAsync(title, message)) return;

        action.IsEnabled = false;
        var originalContent = action.Content;
        action.Content = UninstallButtonLabel("卸载中");
        var progress = new Progress<DeploymentProgress>(status => Dispatcher.UIThread.Post(() =>
        {
            ModelDetailProgress.Text = status.Message;
            DeploymentSummaryText.Text = status.Message;
        }));

        try
        {
            await _deployment.UninstallAsync(componentId, progress, _navigation.Token);
            ModelDetailProgress.Text = componentKind == "model"
                ? "模型权重已卸载，参数配置已保留"
                : "共享运行环境已卸载，模型权重和参数配置已保留";
        }
        catch (OperationCanceledException)
        {
            ModelDetailProgress.Text = "卸载已取消";
        }
        catch (Exception exception)
        {
            ModelDetailProgress.Text = $"卸载失败：{ShortMessage(exception.Message)}";
        }
        finally
        {
            action.Content = originalContent;
            action.IsEnabled = true;
            RefreshDeploymentStatus(keepSummaryOnError:
                ModelDetailProgress.Text?.StartsWith("卸载失败", StringComparison.Ordinal) == true);
            RefreshModelDeploymentDetail();
        }
    }

    private async Task InstallModelComponentAsync(Button action, string componentKind, ModelDeploymentInfo info)
    {
        action.IsEnabled = false;
        action.Content = UninstallButtonLabel("安装中");
        var downloadTaskId = BeginDownloadUiTask(componentKind == "model" ? info.Title : info.RuntimeTitle);
        var progress = CreateDownloadProgress(downloadTaskId, status =>
        {
            ModelDetailProgress.Text = status;
            DeploymentSummaryText.Text = status;
        });
        var downloadSucceeded = false;
        var completionStatus = "安装已停止";

        var components = componentKind switch
        {
            "model" => new[] { "python-runtime", info.RuntimeId, info.Id },
            "python" => new[] { "python-runtime" },
            _ => new[] { "python-runtime", info.RuntimeId }
        };

        try
        {
            foreach (var componentId in components)
            {
                if (_deployment.Inspect()[componentId].Installed) continue;
                await InstallDeploymentComponentAsync(
                    componentId,
                    downloadTaskId,
                    progress,
                    SelectedModelDownloadSource());
            }
            var accelerationWarning = info.Id.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase)
                ? await _deployment.GetWhisperCudaWarningAsync(
                    _downloadUiTasks[downloadTaskId].UserCancellation.Token)
                : null;
            ModelDetailProgress.Text = componentKind == "model"
                ? string.IsNullOrWhiteSpace(accelerationWarning) ? "模型安装完成" : $"模型安装完成；{accelerationWarning}"
                : string.IsNullOrWhiteSpace(accelerationWarning) ? "运行环境安装完成" : $"运行环境安装完成；{accelerationWarning}";
            downloadSucceeded = true;
            completionStatus = ModelDetailProgress.Text;
        }
        catch (OperationCanceledException)
        {
            ModelDetailProgress.Text = "安装已取消";
            completionStatus = ModelDetailProgress.Text;
        }
        catch (Exception exception)
        {
            ModelDetailProgress.Text = $"安装失败：{ShortMessage(exception.Message)}";
            completionStatus = ModelDetailProgress.Text;
        }
        finally
        {
            CompleteDownloadUiTask(downloadTaskId, downloadSucceeded, completionStatus);
            action.IsEnabled = true;
            RefreshDeploymentStatus(keepSummaryOnError:
                ModelDetailProgress.Text?.StartsWith("安装失败", StringComparison.Ordinal) == true);
            RefreshModelDeploymentDetail();
        }
    }

    private async void ConfiguredModelUninstall_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button action) return;
        var selectedName = ConfiguredModelCombo.SelectedItem as string;
        var selected = _configurableModels.FirstOrDefault(model => model.Name == selectedName);
        if (string.IsNullOrWhiteSpace(selected.Id)) return;

        var confirmed = await ConfirmComponentUninstallAsync(
            $"卸载 {selected.Name}？",
            "将删除本地模型权重，但保留这个模型的独立参数配置。以后重新下载模型后会自动载入原有配置。");
        if (!confirmed) return;

        action.IsEnabled = false;
        var originalContent = action.Content;
        action.Content = UninstallButtonLabel("卸载中");
        var progress = new Progress<DeploymentProgress>(_ => { });

        try
        {
            await _deployment.UninstallAsync(selected.Id, progress, _navigation.Token);
            DeploymentSummaryText.Text = $"{selected.Name} 已卸载，参数配置已保留";
            RefreshDeploymentStatus();
            RefreshConfigurableModels(_deployment.Inspect());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeploymentSummaryText.Text = $"卸载失败：{ShortMessage(exception.Message)}";
        }
        finally
        {
            action.Content = originalContent;
            action.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmComponentUninstallAsync(
        string title,
        string message,
        string confirmLabel = "确认卸载")
    {
        var dialog = new Window
        {
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = Avalonia.Controls.WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            Title = title
        };

        var cancel = new Button
        {
            Content = new TextBlock
            {
                Text = "取消",
                Foreground = Brush.Parse("#111318"),
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Width = 82,
            Height = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Background = Brush.Parse("#F1F1F3"),
            Foreground = Brush.Parse("#111318"),
            FontWeight = FontWeight.Bold
        };
        var uninstall = new Button
        {
            Content = UninstallButtonLabel(confirmLabel),
            Width = 96,
            Height = 36,
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(0),
            Background = Brush.Parse("#E95757"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };
        var warningIcon = new PathIcon
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#E6A700"),
            Data = StreamGeometry.Parse("M13 14H11V9H13M13 18H11V16H13M1 21H23L12 2L1 21Z")
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(warningIcon);
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 19,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#111318")
        };
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);

        var contentText = new TextBlock
        {
            Text = message,
            Margin = new Thickness(42, 15, 0, 22),
            FontSize = 12,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#6F7783")
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { cancel, uninstall }
        };

        var dialogScale = new ScaleTransform(0.94, 0.94)
        {
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleXProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                },
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleYProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            }
        };
        var dialogTranslate = new TranslateTransform(0, 12)
        {
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            }
        };
        var dialogTransforms = new TransformGroup();
        dialogTransforms.Children.Add(dialogScale);
        dialogTransforms.Children.Add(dialogTranslate);

        var dialogCard = new Border
        {
            MinHeight = 190,
            Padding = new Thickness(26, 24),
            Background = Brush.Parse("#FFFFFF"),
            BorderBrush = Brush.Parse("#E5E7EB"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Opacity = 0,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = dialogTransforms,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Border.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(170),
                    Easing = new CubicEaseOut()
                }
            },
            Child = new StackPanel
            {
                Children = { header, contentText, actions }
            }
        };
        dialog.Content = dialogCard;

        var isClosing = false;
        async Task CloseAnimatedAsync(bool result)
        {
            if (isClosing) return;
            isClosing = true;
            cancel.IsEnabled = false;
            uninstall.IsEnabled = false;
            dialogCard.Opacity = 0;
            dialogScale.ScaleX = 0.97;
            dialogScale.ScaleY = 0.97;
            dialogTranslate.Y = 7;
            await Task.Delay(135);
            dialog.Close(result);
        }

        cancel.Click += async (_, _) => await CloseAnimatedAsync(false);
        uninstall.Click += async (_, _) => await CloseAnimatedAsync(true);
        dialog.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            await CloseAnimatedAsync(false);
        };
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            dialogCard.Opacity = 1;
            dialogScale.ScaleX = 1;
            dialogScale.ScaleY = 1;
            dialogTranslate.Y = 0;
        }, DispatcherPriority.Render);

        return await dialog.ShowDialog<bool>(this);
    }

    private static TextBlock UninstallButtonLabel(string text) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };

    private static void SetDetailComponentState(TextBlock target, DeploymentState state, string missingText)
    {
        target.Text = state.Installed ? "已安装" : missingText;
        target.Foreground = Brush.Parse(state.Installed ? "#278A68" : "#89919D");
    }

    private async void DeploymentGroup_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Border { Tag: string group }) return;
        e.Handled = true;
        var (panel, chevron, expandedHeight) = group switch
        {
            "runtime" => (RuntimeGroupPanel, (Control)RuntimeChevron, 232d),
            "whisper" => (WhisperGroupPanel, (Control)WhisperChevron, 348d),
            "qwen" => (QwenGroupPanel, (Control)QwenChevron, 116d),
            "funasr" => (FunAsrGroupPanel, (Control)FunAsrChevron, 116d),
            "nvidia" => (NvidiaGroupPanel, (Control)NvidiaChevron, 116d),
            "moss" => (MossGroupPanel, (Control)MossChevron, 58d),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
        };

        var expand = !_expandedDeploymentGroups.Remove(panel);
        if (expand) _expandedDeploymentGroups.Add(panel);
        await _motion.AnimateCardSwapAsync(panel, chevron, expand, expandedHeight);
    }

    private void RefreshDeploymentStatus(bool keepSummaryOnError = false)
    {
        var states = _deployment.Inspect();
        SetDeploymentRow(states["python-runtime"], PythonRuntimeBadge, PythonRuntimeStatus, PythonRuntimeAction, "修复环境");
        SetDeploymentRow(states["whisper-runtime"], WhisperRuntimeBadge, WhisperRuntimeStatus, WhisperRuntimeAction, "安装环境");
        SetDeploymentRow(states["qwen-runtime"], QwenRuntimeBadge, QwenRuntimeStatus, QwenRuntimeAction, "安装环境");
        SetDeploymentRow(states["nvidia-runtime"], NvidiaRuntimeBadge, NvidiaRuntimeStatus, NvidiaRuntimeAction, "安装环境");
        SetDeploymentRow(states["whisper-tiny"], WhisperTinyBadge, WhisperTinyStatus, WhisperTinyAction, "下载模型");
        SetDeploymentRow(states["whisper-base"], WhisperBaseBadge, WhisperBaseStatus, WhisperBaseAction, "下载模型");
        SetDeploymentRow(states["whisper-small"], WhisperSmallBadge, WhisperSmallStatus, WhisperSmallAction, "下载模型");
        SetDeploymentRow(states["whisper-medium"], WhisperMediumBadge, WhisperMediumStatus, WhisperMediumAction, "下载模型");
        SetDeploymentRow(states["whisper-large-v3"], WhisperLargeBadge, WhisperLargeStatus, WhisperLargeAction, "下载模型");
        SetDeploymentRow(states["whisper-v3-turbo"], WhisperTurboBadge, WhisperTurboStatus, WhisperTurboAction, "下载模型");
        SetDeploymentRow(states["qwen-0.6b"], QwenSmallBadge, QwenSmallStatus, QwenSmallAction, "下载模型");
        SetDeploymentRow(states["qwen-1.7b"], QwenLargeBadge, QwenLargeStatus, QwenLargeAction, "下载模型");
        SetDeploymentRow(states["funasr-nano"], FunAsrNanoBadge, FunAsrNanoStatus, FunAsrNanoAction, "下载模型");
        SetDeploymentRow(states["sensevoice-small"], SenseVoiceBadge, SenseVoiceStatus, SenseVoiceAction, "下载模型");
        SetDeploymentRow(states["nvidia-parakeet-v3"], NvidiaParakeetBadge, NvidiaParakeetStatus, NvidiaParakeetAction, "下载模型");
        SetDeploymentRow(states["nvidia-canary-v2"], NvidiaCanaryBadge, NvidiaCanaryStatus, NvidiaCanaryAction, "下载模型");
        SetDeploymentRow(states["moss-0.9b"], MossBadge, MossStatus, MossAction, "下载模型");

        SetRecommendedState(RecommendedNvidiaText, states["nvidia-parakeet-v3"]);
        SetRecommendedState(RecommendedQwenText, states["qwen-0.6b"]);
        SetRecommendedState(RecommendedWhisperText, states["whisper-v3-turbo"]);
        RefreshConfigurableModels(states);

        RuntimeGroupSummary.Text = InstalledSummary(states, "python-runtime", "whisper-runtime", "qwen-runtime", "nvidia-runtime");
        WhisperGroupSummary.Text = InstalledSummary(states, "whisper-tiny", "whisper-base", "whisper-small", "whisper-medium", "whisper-large-v3", "whisper-v3-turbo");
        QwenGroupSummary.Text = InstalledSummary(states, "qwen-0.6b", "qwen-1.7b");
        FunAsrGroupSummary.Text = InstalledSummary(states, "funasr-nano", "sensevoice-small");
        NvidiaGroupSummary.Text = InstalledSummary(states, "nvidia-parakeet-v3", "nvidia-canary-v2");
        MossGroupSummary.Text = states["moss-0.9b"].Installed ? "已安装" : "长视频 · 时间戳 · 说话人分离";

        if (!keepSummaryOnError)
        {
            _installedModelCount = ModelDeployments.Keys.Count(id => states[id].Installed);
            UpdateDeploymentSummary();
        }
        _ = RefreshCudaDetailAsync();
    }

    private async Task RefreshModelCatalogAsync(bool forceRefresh = false)
    {
        var previous = _catalogRefresh;
        _catalogRefresh = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        var request = _catalogRefresh;

        try
        {
            var result = await _modelCatalog.RefreshAsync(request.Token, forceRefresh);
            if (!ReferenceEquals(request, _catalogRefresh)) return;
            ApplyModelCatalog(result);
            _catalogStatus = result.OnlineCount == ModelDeployments.Count
                ? $"在线目录已更新 {result.RefreshedAt:HH:mm}"
                : result.Models.Count > 0
                    ? $"部分网络不可用 · 已合并缓存 {result.RefreshedAt:HH:mm}"
                    : "网络不可用 · 保留内置目录";
            UpdateDeploymentSummary();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(request, _catalogRefresh)) return;
            _catalogStatus = $"目录更新失败：{ShortMessage(exception.Message)}";
            UpdateDeploymentSummary();
        }
    }

    private void ApplyModelCatalog(ModelCatalogResult result)
    {
            UpdateCatalogMetadata(WhisperTinyMetadataText, result, "whisper-tiny", "75 MB · 99 种语言 · 最快");
        UpdateCatalogMetadata(WhisperBaseMetadataText, result, "whisper-base", "145 MB · 速度与精度入门平衡");
        UpdateCatalogMetadata(WhisperSmallMetadataText, result, "whisper-small", "464 MB · 均衡 · 分段时间戳");
        UpdateCatalogMetadata(WhisperMediumMetadataText, result, "whisper-medium", "1.53 GB · 高精度 · 中等显存占用");
        UpdateCatalogMetadata(WhisperLargeMetadataText, result, "whisper-large-v3", "3.1 GB · 高精度 · 建议 6 GB 显存");
        UpdateCatalogMetadata(WhisperTurboMetadataText, result, "whisper-v3-turbo", "1.62 GB · 99 种语言 · 大幅提速");
            UpdateCatalogMetadata(QwenSmallMetadataText, result, "qwen-0.6b", "1.8 GB · 30 种语言 + 22 种中文方言");
        UpdateCatalogMetadata(QwenLargeMetadataText, result, "qwen-1.7b", "4.7 GB · 高精度 · 建议 8 GB 显存");
        UpdateCatalogMetadata(FunAsrNanoMetadataText, result, "funasr-nano", "1.99 GB · 方言、热词、时间戳、实时识别");
        UpdateCatalogMetadata(SenseVoiceMetadataText, result, "sensevoice-small", "944 MB · 低延迟 · 情绪与声音事件");
        UpdateCatalogMetadata(NvidiaParakeetMetadataText, result, "nvidia-parakeet-v3", "2.6 GB · 25 种语言 · 高吞吐与时间戳");
        UpdateCatalogMetadata(NvidiaCanaryMetadataText, result, "nvidia-canary-v2", "6.36 GB · 25 种语言 · 识别与语音翻译");
        UpdateCatalogMetadata(MossMetadataText, result, "moss-0.9b", "1.83 GB · 长音频 · 时间戳 · 说话人分离");

        if (result.Models.TryGetValue("nvidia-parakeet-v3", out var nvidia))
            RecommendedNvidiaMetadataText.Text = $"25 种语言 · 同类热门 #{nvidia.CategoryRank} · {FormatDownloads(nvidia.Downloads)}";
        if (result.Models.TryGetValue("qwen-0.6b", out var qwen))
            RecommendedQwenMetadataText.Text = $"多语言轻量模型 · 同类热门 #{qwen.CategoryRank} · {FormatDownloads(qwen.Downloads)}";
        if (result.Models.TryGetValue("whisper-v3-turbo", out var turbo))
            RecommendedWhisperMetadataText.Text = $"99 种语言 · 同类热门 #{turbo.CategoryRank} · {FormatDownloads(turbo.Downloads)}";

        ReorderModelRows(WhisperModelRows, result,
            ("whisper-tiny", WhisperTinyRow), ("whisper-base", WhisperBaseRow),
            ("whisper-small", WhisperSmallRow), ("whisper-medium", WhisperMediumRow),
            ("whisper-large-v3", WhisperLargeRow), ("whisper-v3-turbo", WhisperTurboRow));
        ReorderModelRows(QwenModelRows, result, ("qwen-0.6b", QwenSmallRow), ("qwen-1.7b", QwenLargeRow));
        ReorderModelRows(FunAsrModelRows, result, ("funasr-nano", FunAsrNanoRow), ("sensevoice-small", SenseVoiceRow));
        ReorderModelRows(NvidiaModelRows, result, ("nvidia-parakeet-v3", NvidiaParakeetRow), ("nvidia-canary-v2", NvidiaCanaryRow));
    }

    private static void UpdateCatalogMetadata(
        TextBlock target,
        ModelCatalogResult result,
        string deploymentId,
        string fallback)
    {
        if (!result.Models.TryGetValue(deploymentId, out var item))
        {
            target.Text = fallback;
            return;
        }

        var cacheSuffix = item.FromCache ? " · 缓存" : string.Empty;
        target.Text = $"{fallback} · {FormatDownloads(item.Downloads)} · 同类 #{item.CategoryRank} · 更新 {item.LastModified.ToLocalTime():yyyy-MM}{cacheSuffix}";
    }

    private static void ReorderModelRows(
        StackPanel panel,
        ModelCatalogResult result,
        params (string Id, Border Row)[] rows)
    {
        var ordered = rows
            .OrderBy(row => result.Models.TryGetValue(row.Id, out var item) ? item.CategoryRank : int.MaxValue)
            .ToArray();

        var currentBorders = panel.Children.OfType<Border>().ToArray();
        var isIdentical = currentBorders.Length == ordered.Length;
        if (isIdentical)
        {
            for (var i = 0; i < ordered.Length; i++)
            {
                if (!ReferenceEquals(ordered[i].Row, currentBorders[i]))
                {
                    isIdentical = false;
                    break;
                }
            }
        }
        if (isIdentical) return;

        foreach (var (_, row) in ordered) panel.Children.Remove(row);
        foreach (var (_, row) in ordered) panel.Children.Add(row);
    }

    private static string FormatDownloads(long downloads) => downloads switch
    {
        >= 10_000_000 => $"{downloads / 10_000_000d:0.#} 千万下载",
        >= 10_000 => $"{downloads / 10_000d:0.#} 万下载",
        >= 1_000 => $"{downloads / 1_000d:0.#} 千下载",
        _ => $"{downloads} 次下载"
    };

    private void UpdateDeploymentSummary()
    {
        var local = $"已安装 {_installedModelCount} / {ModelDeployments.Count} 个模型";
        DeploymentSummaryText.Text = string.IsNullOrWhiteSpace(_catalogStatus) ? local : $"{local} · {_catalogStatus}";
    }

    private void CatalogSpinner_OnTick(object? sender, EventArgs e)
    {
        // One complete lap takes roughly 1.15 seconds. Keeping this on the
        // render-friendly 16 ms timer makes the luminance hand-off fluid.
        _catalogLoadingTilePhase = (_catalogLoadingTilePhase + 4d / 72d) % 4d;
        UpdateCatalogLoadingTiles();
    }

    private void UpdateCatalogLoadingTiles()
    {
        // Clockwise from the upper-left: upper-left, upper-right,
        // lower-right, lower-left. Adjacent tiles cross-fade so the loader
        // remains smooth instead of stepping between four discrete frames.
        var tiles = new Control[]
        {
            ModelCatalogLoadingTileTopLeft,
            ModelCatalogLoadingTileTopRight,
            ModelCatalogLoadingTileBottomRight,
            ModelCatalogLoadingTileBottomLeft
        };

        UpdateLoadingTileOpacities(tiles, _catalogLoadingTilePhase);
    }

    private static void UpdateLoadingTileOpacities(IReadOnlyList<Control> tiles, double phase)
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            var distance = Math.Abs(phase - index);
            distance = Math.Min(distance, 4d - distance);
            var emphasis = Math.Max(0d, 1d - distance);
            emphasis = emphasis * emphasis * (3d - (2d * emphasis));
            tiles[index].Opacity = 0.28d + (0.72d * emphasis);
        }
    }

    private static string InstalledSummary(IReadOnlyDictionary<string, DeploymentState> states, params string[] ids)
    {
        var installed = ids.Count(id => states[id].Installed);
        return installed == 0 ? "未安装" : $"已安装 {installed} / {ids.Length}";
    }

    private static void SetDeploymentRow(
        DeploymentState state,
        Border badge,
        TextBlock status,
        Button action,
        string installLabel)
    {
        if (state.Installed)
        {
            badge.Background = Brush.Parse("#EAF9F3");
            status.Foreground = Brush.Parse("#278A68");
            status.Text = "已安装";
            action.Foreground = Brush.Parse("#278A68");
            action.Content = "管理";
        }
        else
        {
            badge.Background = Brush.Parse("#F0F1F4");
            status.Foreground = Brush.Parse("#777A85");
            status.Text = "未下载";
            action.Foreground = Brush.Parse("#3399F3");
            action.Content = "下载";
        }
    }

    private static void SetRecommendedState(Button target, DeploymentState state)
    {
        target.Content = state.Installed ? "管理" : "下载";
        target.Foreground = Brush.Parse(state.Installed ? "#278A68" : "#3399F3");
    }

    private async void ModelTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tab }) return;
        await SwitchModelTabAsync(tab == "settings");
    }

    private async Task SwitchModelTabAsync(bool settings)
    {
        if (settings == _modelSettingsActive)
        {
            if (!settings) _ = RefreshModelCatalogAsync(forceRefresh: true);
            return;
        }

        ModelDownloadTab.Classes.Set("selected", !settings);
        ModelSettingsTab.Classes.Set("selected", settings);
        ModelTabIndicator.Width = settings ? 78 : 52;
        ModelTabIndicator.RenderTransform = TransformOperations.Parse(settings
            ? "translate(102px, 0px)"
            : "translate(0px, 0px)");

        _modelTabNavigation.Cancel();
        _modelTabNavigation.Dispose();
        _modelTabNavigation = new CancellationTokenSource();
        var current = _modelSettingsActive ? (Control)ModelSettingsView : ModelDownloadHost;
        var target = settings ? (Control)ModelSettingsView : ModelDownloadHost;
        _modelSettingsActive = settings;
        try
        {
            await _motion.ContentTransitionAsync(
                current, target, GetPageMotionItems(current), GetPageMotionItems(target), _modelTabNavigation.Token);
            if (!settings) _ = RefreshModelCatalogAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer tab click owns the content transition.
        }
    }

    private void RefreshConfigurableModels(IReadOnlyDictionary<string, DeploymentState> states)
    {
        var previous = ConfiguredModelCombo.SelectedItem as string;
        _configurableModels.Clear();
        AddConfigurable(states, "whisper-tiny", "Whisper Tiny");
        AddConfigurable(states, "whisper-base", "Whisper Base");
        AddConfigurable(states, "whisper-small", "Whisper Small");
        AddConfigurable(states, "whisper-medium", "Whisper Medium");
        AddConfigurable(states, "whisper-large-v3", "Whisper Large V3");
        AddConfigurable(states, "whisper-v3-turbo", "Whisper Large V3 Turbo");
        AddConfigurable(states, "qwen-0.6b", "Qwen3-ASR 0.6B");
        AddConfigurable(states, "qwen-1.7b", "Qwen3-ASR 1.7B");
        AddConfigurable(states, "funasr-nano", "Fun-ASR Nano 2512");
        AddConfigurable(states, "sensevoice-small", "SenseVoice Small");
        AddConfigurable(states, "nvidia-parakeet-v3", "NVIDIA Parakeet TDT 0.6B V3");
        AddConfigurable(states, "nvidia-canary-v2", "NVIDIA Canary 1B V2");
        AddConfigurable(states, "moss-0.9b", "MOSS Transcribe-Diarize 0.9B");

        var names = _configurableModels.Select(model => model.Name).ToArray();
        ConfiguredModelCombo.ItemsSource = names;
        ConfiguredModelCombo.SelectedItem = names.Contains(previous) ? previous : names.FirstOrDefault();
        var transcriptionNames = _configurableModels
            .Where(model => SupportsLocalTranscription(model.Id))
            .Select(model => model.Name)
            .ToArray();
        var projectPrevious = ProjectTranscriptionModelCombo.SelectedItem as string;
        _loadingProjectTranscription = true;
        ProjectTranscriptionModelCombo.ItemsSource = transcriptionNames;
        ProjectTranscriptionModelCombo.SelectedItem = transcriptionNames.Contains(projectPrevious)
            ? projectPrevious
            : transcriptionNames.FirstOrDefault();
        _loadingProjectTranscription = false;
        ProjectTranscriptionModelEmpty.IsVisible = transcriptionNames.Length == 0;
        RefreshProjectTranscriptionReadiness();
        ModelConfigurationEmpty.IsVisible = names.Length == 0;
        ModelConfigurationPanel.IsVisible = names.Length > 0;
        if (names.Length == 0)
        {
            _configuredModelIndex = -1;
            UpdateConfiguredModelIndicator(-1, animate: false);
        }
        RefreshHomeDashboard();
    }

    private void AddConfigurable(IReadOnlyDictionary<string, DeploymentState> states, string id, string name)
    {
        if (states[id].Installed) _configurableModels.Add((id, name));
    }

    private void ConfiguredModel_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = ConfiguredModelCombo.SelectedItem as string ?? string.Empty;
        var selectedModel = _configurableModels.FirstOrDefault(model => model.Name == selected);
        if (string.IsNullOrWhiteSpace(selectedModel.Id)) return;
        var nextIndex = _configurableModels.FindIndex(model => model.Id == selectedModel.Id);
        var previousIndex = _configuredModelIndex;
        _configuredModelIndex = nextIndex;
        UpdateConfiguredModelIndicator(nextIndex, animate: previousIndex >= 0);

        ApplyConfiguredModelSelection(selectedModel.Id, selected);
        RefreshHomeDashboard();
    }

    private void UpdateConfiguredModelIndicator(int index, bool animate)
    {
        if (index < 0)
        {
            ConfiguredModelIndicator.IsVisible = false;
            return;
        }

        const double itemStride = 50;
        var targetY = index * itemStride;
        if (!ConfiguredModelIndicator.IsVisible || !animate)
        {
            ConfiguredModelIndicator.RenderTransform = new TranslateTransform(0, targetY);
            ConfiguredModelIndicator.IsVisible = true;
            return;
        }

        _motion.AnimateModelSelectionIndicator(ConfiguredModelIndicator, targetY);
    }

    private void ApplyConfiguredModelSelection(string? modelId, string selectedName)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        ConfiguredModelTitle.Text = $"{selectedName} 参数";
        _loadingModelConfiguration = true;
        try
        {
            ConfigureModelSpecificLayout(modelId);
            ResetModelConfigurationDefaults();
            LoadModelConfiguration(modelId);
        }
        finally
        {
            _loadingModelConfiguration = false;
        }

        PersistModelConfiguration(CaptureModelConfiguration(modelId), makeActive: true);
    }

    private void ConfigureModelSpecificLayout(string modelId)
    {
        ConfigurePrecisionOptions(modelId);
        ConfigureLanguageOptions(modelId);
        foreach (var row in new Control[]
                 {
                     DeviceSettingRow, LanguageSettingRow, PrecisionSettingRow, BeamSettingRow,
                     VadSettingRow, VadThresholdSettingRow, VadMinSilenceSettingRow, VadSpeechPadSettingRow,
                     MaxTokensSettingRow, TimestampSettingRow, HotwordsSettingRow,
                     EmotionSettingRow, AudioEventSettingRow, SpeakerCountSettingRow,
                     DiarizationSettingRow, TemperatureSettingRow, ChunkSecondsSettingRow
                 })
            row.IsVisible = false;

        DeviceSettingRow.IsVisible = true;
        switch (modelId.ToLowerInvariant())
        {
            case "whisper-tiny":
            case "whisper-base":
            case "whisper-small":
                LanguageSettingRow.IsVisible = true;
                PrecisionSettingRow.IsVisible = true;
                BeamSettingRow.IsVisible = true;
                VadSettingRow.IsVisible = true;
                VadThresholdSettingRow.IsVisible = true;
                VadMinSilenceSettingRow.IsVisible = true;
                VadSpeechPadSettingRow.IsVisible = true;
                TimestampSettingRow.IsVisible = true;
                HotwordsSettingRow.IsVisible = true;
                BeamSizeSlider.Maximum = 8;
                break;
            case "whisper-medium":
            case "whisper-large-v3":
            case "whisper-v3-turbo":
                LanguageSettingRow.IsVisible = true;
                PrecisionSettingRow.IsVisible = true;
                BeamSettingRow.IsVisible = true;
                VadSettingRow.IsVisible = true;
                VadThresholdSettingRow.IsVisible = true;
                VadMinSilenceSettingRow.IsVisible = true;
                VadSpeechPadSettingRow.IsVisible = true;
                TimestampSettingRow.IsVisible = true;
                HotwordsSettingRow.IsVisible = true;
                BeamSizeSlider.Maximum = 10;
                break;
            case "qwen-0.6b":
                LanguageSettingRow.IsVisible = true;
                PrecisionSettingRow.IsVisible = true;
                MaxTokensSettingRow.IsVisible = true;
                HotwordsSettingRow.IsVisible = true;
                MaxTokensSlider.Maximum = 1024;
                break;
            case "qwen-1.7b":
                LanguageSettingRow.IsVisible = true;
                PrecisionSettingRow.IsVisible = true;
                MaxTokensSettingRow.IsVisible = true;
                HotwordsSettingRow.IsVisible = true;
                MaxTokensSlider.Maximum = 2048;
                break;
            case "funasr-nano":
                LanguageSettingRow.IsVisible = true;
                VadSettingRow.IsVisible = true;
                VadThresholdSettingRow.IsVisible = true;
                VadMinSilenceSettingRow.IsVisible = true;
                VadSpeechPadSettingRow.IsVisible = true;
                TimestampSettingRow.IsVisible = true;
                HotwordsSettingRow.IsVisible = true;
                break;
            case "sensevoice-small":
                LanguageSettingRow.IsVisible = true;
                VadSettingRow.IsVisible = true;
                VadThresholdSettingRow.IsVisible = true;
                VadMinSilenceSettingRow.IsVisible = true;
                VadSpeechPadSettingRow.IsVisible = true;
                EmotionSettingRow.IsVisible = true;
                AudioEventSettingRow.IsVisible = true;
                break;
            case "nvidia-parakeet-v3":
                PrecisionSettingRow.IsVisible = true;
                TimestampSettingRow.IsVisible = true;
                ChunkSecondsSettingRow.IsVisible = true;
                break;
            case "nvidia-canary-v2":
                LanguageSettingRow.IsVisible = true;
                PrecisionSettingRow.IsVisible = true;
                BeamSettingRow.IsVisible = true;
                TimestampSettingRow.IsVisible = true;
                BeamSizeSlider.Maximum = 10;
                break;
            case "moss-0.9b":
                PrecisionSettingRow.IsVisible = true;
                TimestampSettingRow.IsVisible = true;
                SpeakerCountSettingRow.IsVisible = true;
                DiarizationSettingRow.IsVisible = true;
                ChunkSecondsSettingRow.IsVisible = true;
                break;
        }

        BuildAdvancedParameterRows(modelId);
    }

    private void ConfigurePrecisionOptions(string modelId)
    {
        PrecisionCombo.Items.Clear();
        var options = modelId.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase) ||
                      modelId.StartsWith("nvidia-", StringComparison.OrdinalIgnoreCase)
            ? new[] { "自动", "BFloat16", "Float16", "Float32" }
            : new[] { "自动", "Float16", "Int8", "Int8 Float16", "Float32" };
        foreach (var option in options)
            PrecisionCombo.Items.Add(new ComboBoxItem { Content = option });
        PrecisionCombo.SelectedIndex = 0;
    }

    private void ConfigureLanguageOptions(string modelId)
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "自动检测", Tag = string.Empty });
        var isQwen = modelId.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase);
        var specification = LanguageSpecificationForModel(modelId);
        foreach (var entry in specification.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|', 2);
            if (parts.Length != 2) continue;
            LanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = isQwen ? parts[1] : $"{parts[1]}  ({parts[0]})",
                Tag = parts[0]
            });
        }
        LanguageCombo.SelectedIndex = 0;
    }

    private static string LanguageSpecificationForModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return string.Empty;
        if (modelId.StartsWith("qwen-", StringComparison.OrdinalIgnoreCase)) return QwenLanguageSpec;
        if (modelId.Equals("funasr-nano", StringComparison.OrdinalIgnoreCase)) return FunAsrNanoLanguageSpec;
        if (modelId.Equals("sensevoice-small", StringComparison.OrdinalIgnoreCase)) return SenseVoiceLanguageSpec;
        if (modelId.StartsWith("nvidia-", StringComparison.OrdinalIgnoreCase)) return NvidiaLanguageSpec;
        if (modelId.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase)) return WhisperLanguageSpec;
        return string.Empty;
    }

    private void BuildAdvancedParameterRows(string modelId)
    {
        ModelAdvancedParameters.Children.Clear();
        _advancedConfigControls.Clear();
        _activeAdvancedDefinitions = AdvancedParameters.TryGetValue(modelId, out var definitions)
            ? definitions
            : Array.Empty<ParameterDefinition>();

        string? currentSection = null;
        var showSections = _activeAdvancedDefinitions.Count > 4;
        foreach (var definition in _activeAdvancedDefinitions)
        {
            if (showSections && !string.Equals(currentSection, definition.Section, StringComparison.Ordinal))
            {
                currentSection = definition.Section;
                ModelAdvancedParameters.Children.Add(new Border
                {
                    Height = 40,
                    Padding = new Thickness(20, 0),
                    Background = Brush.Parse("#FAFAFB"),
                    BorderBrush = Brush.Parse("#E9E9EC"),
                    BorderThickness = new Thickness(0, 1, 0, 1),
                    Child = new TextBlock
                    {
                        Text = currentSection,
                        FontSize = 11,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brush.Parse("#626772"),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }

            var editor = CreateAdvancedEditor(definition);
            _advancedConfigControls[definition.Key] = editor;
            var description = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = definition.Label, FontWeight = FontWeight.SemiBold, FontSize = 12.5 },
                    new TextBlock
                    {
                        Text = definition.Description,
                        Foreground = Brush.Parse("#858992"),
                        FontSize = 10.5,
                        Margin = new Thickness(0, 3, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220") };
            grid.Children.Add(description);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            var row = new Border { Child = grid };
            row.Classes.Add("settingRow");
            ModelAdvancedParameters.Children.Add(row);
        }
    }

    private Control CreateAdvancedEditor(ParameterDefinition definition)
    {
        if (definition.Kind == ParameterKind.Boolean)
        {
            var toggle = new ToggleSwitch
            {
                IsChecked = Convert.ToBoolean(definition.DefaultValue, CultureInfo.InvariantCulture),
                OnContent = string.Empty,
                OffContent = string.Empty,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            toggle.PropertyChanged += ConfigurationValue_OnPropertyChanged;
            return toggle;
        }

        if (definition.Kind == ParameterKind.Select)
        {
            var combo = new ComboBox
            {
                Width = 180,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            combo.Classes.Add("settingSelect");
            foreach (var option in definition.Options ?? Array.Empty<string>())
                combo.Items.Add(new ComboBoxItem { Content = option });
            SelectComboText(combo, Convert.ToString(definition.DefaultValue, CultureInfo.InvariantCulture));
            combo.SelectionChanged += ConfigurationValue_OnSelectionChanged;
            return combo;
        }

        var textBox = new TextBox
        {
            Width = 200,
            Text = Convert.ToString(definition.DefaultValue, CultureInfo.InvariantCulture) ?? string.Empty,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        textBox.Classes.Add("settingInput");
        textBox.TextChanged += ConfigurationText_OnTextChanged;
        return textBox;
    }

    private void ConfigurationValue_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        QueueModelConfigurationAutoSave();

    private void ConfigurationText_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        QueueModelConfigurationAutoSave();

    private void ConfigurationValue_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Slider.ValueProperty || e.Property == ToggleSwitch.IsCheckedProperty)
            QueueModelConfigurationAutoSave();
    }

    private void QueueModelConfigurationAutoSave()
    {
        if (_loadingModelConfiguration) return;
        var selectedName = ConfiguredModelCombo.SelectedItem as string;
        var selected = _configurableModels.FirstOrDefault(model => model.Name == selectedName);
        if (string.IsNullOrWhiteSpace(selected.Id)) return;

        var snapshot = CaptureModelConfiguration(selected.Id);
        _configurationAutoSave.Cancel();
        _configurationAutoSave.Dispose();
        _configurationAutoSave = new CancellationTokenSource();
        _ = AutoSaveModelConfigurationAsync(snapshot, _configurationAutoSave.Token);
    }

    private async Task AutoSaveModelConfigurationAsync(
        ModelConfigurationSnapshot snapshot,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(220, token);
            var selectedName = ConfiguredModelCombo.SelectedItem as string;
            var active = _configurableModels.FirstOrDefault(model => model.Name == selectedName);
            var isActive = string.Equals(active.Id, snapshot.ModelId, StringComparison.OrdinalIgnoreCase);
            PersistModelConfiguration(snapshot, makeActive: isActive);
        }
        catch (OperationCanceledException)
        {
            // A newer edit owns the pending auto-save.
        }
    }

    private ModelConfigurationSnapshot CaptureModelConfiguration(string modelId) => new(
        modelId,
        SelectedComboText(DeviceCombo),
        SelectedComboValue(LanguageCombo),
        SelectedComboText(PrecisionCombo),
        (int)Math.Round(BeamSizeSlider.Value),
        VadToggle.IsChecked == true,
        Math.Round(VadThresholdSlider.Value, 2),
        (int)Math.Round(VadMinSilenceSlider.Value),
        (int)Math.Round(VadSpeechPadSlider.Value),
        (int)Math.Round(MaxTokensSlider.Value),
        TimestampToggle.IsChecked == true,
        HotwordsBox.Text ?? string.Empty,
        EmotionToggle.IsChecked == true,
        AudioEventToggle.IsChecked == true,
        SelectedComboText(SpeakerCountCombo),
        DiarizationToggle.IsChecked == true,
        Math.Round(TemperatureSlider.Value, 1),
        (int)Math.Round(ChunkSecondsSlider.Value),
        CaptureAdvancedConfiguration());

    private IReadOnlyDictionary<string, object?> CaptureAdvancedConfiguration()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _activeAdvancedDefinitions)
        {
            if (!_advancedConfigControls.TryGetValue(definition.Key, out var control)) continue;
            values[definition.Key] = control switch
            {
                ToggleSwitch toggle => toggle.IsChecked == true,
                ComboBox combo => SelectedComboText(combo),
                TextBox text when definition.Kind == ParameterKind.Integer =>
                    int.TryParse(text.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                        ? integer
                        : definition.DefaultValue,
                TextBox text when definition.Kind == ParameterKind.Decimal =>
                    double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? number
                        : definition.DefaultValue,
                TextBox text => text.Text ?? string.Empty,
                _ => definition.DefaultValue
            };
        }
        return values;
    }

    private void PersistModelConfiguration(ModelConfigurationSnapshot snapshot, bool makeActive)
    {
        var directory = Path.Combine(_deployment.RuntimeRoot, "config");
        Directory.CreateDirectory(directory);
        var settings = new
        {
            model = snapshot.ModelId,
            device = snapshot.Device,
            language = snapshot.Language,
            precision = snapshot.Precision,
            beamSize = snapshot.BeamSize,
            vad = snapshot.Vad,
            vadThreshold = snapshot.VadThreshold,
            vadMinSilence = snapshot.VadMinSilence,
            vadSpeechPad = snapshot.VadSpeechPad,
            maxTokens = snapshot.MaxTokens,
            timestamps = snapshot.Timestamps,
            hotwords = snapshot.Hotwords,
            emotionDetection = snapshot.EmotionDetection,
            audioEventDetection = snapshot.AudioEventDetection,
            speakerCount = snapshot.SpeakerCount,
            diarization = snapshot.Diarization,
            temperature = snapshot.Temperature,
            chunkSeconds = snapshot.ChunkSeconds,
            advanced = snapshot.Advanced
        };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, $"{snapshot.ModelId}.json"), json);
        if (makeActive) File.WriteAllText(Path.Combine(directory, "asr-settings.json"), json);
    }

    private void ResetModelConfigurationDefaults()
    {
        DeviceCombo.SelectedIndex = 0;
        LanguageCombo.SelectedIndex = 0;
        PrecisionCombo.SelectedIndex = 0;
        BeamSizeSlider.Value = 5;
        VadToggle.IsChecked = true;
        VadThresholdSlider.Value = 0.3;
        VadMinSilenceSlider.Value = 2000;
        VadSpeechPadSlider.Value = 400;
        MaxTokensSlider.Value = 512;
        TimestampToggle.IsChecked = true;
        HotwordsBox.Text = string.Empty;
        EmotionToggle.IsChecked = true;
        AudioEventToggle.IsChecked = true;
        SpeakerCountCombo.SelectedIndex = 0;
        DiarizationToggle.IsChecked = true;
        TemperatureSlider.Value = 0.2;
        ChunkSecondsSlider.Value = 30;
    }

    private void LoadModelConfiguration(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var path = Path.Combine(_deployment.RuntimeRoot, "config", $"{modelId}.json");
        if (!File.Exists(path)) return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            SelectComboProperty(root, "device", DeviceCombo);
            SelectComboProperty(root, "language", LanguageCombo);
            SelectComboProperty(root, "precision", PrecisionCombo);
            SelectComboProperty(root, "speakerCount", SpeakerCountCombo);
            SetSliderProperty(root, "beamSize", BeamSizeSlider);
            SetSliderProperty(root, "maxTokens", MaxTokensSlider);
            SetSliderProperty(root, "temperature", TemperatureSlider);
            SetSliderProperty(root, "chunkSeconds", ChunkSecondsSlider);
            SetToggleProperty(root, "vad", VadToggle);
            SetSliderProperty(root, "vadThreshold", VadThresholdSlider);
            SetSliderProperty(root, "vadMinSilence", VadMinSilenceSlider);
            SetSliderProperty(root, "vadSpeechPad", VadSpeechPadSlider);
            SetToggleProperty(root, "timestamps", TimestampToggle);
            SetToggleProperty(root, "emotionDetection", EmotionToggle);
            SetToggleProperty(root, "audioEventDetection", AudioEventToggle);
            SetToggleProperty(root, "diarization", DiarizationToggle);
            if (root.TryGetProperty("hotwords", out var hotwords) && hotwords.ValueKind == JsonValueKind.String)
                HotwordsBox.Text = hotwords.GetString() ?? string.Empty;
            if (root.TryGetProperty("advanced", out var advanced) && advanced.ValueKind == JsonValueKind.Object)
                LoadAdvancedConfiguration(advanced);
        }
        catch (JsonException)
        {
        }
    }

    private void LoadAdvancedConfiguration(JsonElement advanced)
    {
        foreach (var definition in _activeAdvancedDefinitions)
        {
            if (!advanced.TryGetProperty(definition.Key, out var property) ||
                !_advancedConfigControls.TryGetValue(definition.Key, out var control)) continue;
            switch (control)
            {
                case ToggleSwitch toggle when property.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    toggle.IsChecked = property.GetBoolean();
                    break;
                case ComboBox combo when property.ValueKind == JsonValueKind.String:
                    SelectComboText(combo, property.GetString());
                    break;
                case TextBox text when property.ValueKind == JsonValueKind.String:
                    text.Text = property.GetString() ?? string.Empty;
                    break;
                case TextBox text when property.ValueKind == JsonValueKind.Number:
                    text.Text = property.GetRawText();
                    break;
            }
        }
    }

    private static void SelectComboProperty(JsonElement root, string name, ComboBox combo)
    {
        if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            SelectComboText(combo, property.GetString());
    }

    private static void SetSliderProperty(JsonElement root, string name, Slider slider)
    {
        if (root.TryGetProperty(name, out var property) && property.TryGetDouble(out var value))
            slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
    }

    private static void SetToggleProperty(JsonElement root, string name, ToggleSwitch toggle)
    {
        if (root.TryGetProperty(name, out var property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
            toggle.IsChecked = property.GetBoolean();
    }

    private static void SelectComboText(ComboBox combo, string? value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            var content = item.Content?.ToString() ?? string.Empty;
            if (string.Equals(content, value, StringComparison.Ordinal) ||
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(value) && content.StartsWith(value, StringComparison.Ordinal)))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static string SelectedComboText(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? string.Empty : string.Empty;

    private static string SelectedComboValue(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? item.Content?.ToString() ?? string.Empty
            : string.Empty;

    private static string ShortMessage(string message)
    {
        var line = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(line)) return "未知错误";
        return line.Length > 72 ? line[..72] + "…" : line;
    }

    private async Task NavigateTo(string page)
    {
        var epoch = Interlocked.Increment(ref _navigationEpoch);

        var navigation = new CancellationTokenSource();
        var previousNavigation = Interlocked.Exchange(ref _navigation, navigation);
        previousNavigation.Cancel();
        previousNavigation.Dispose();

        if (page == "recognition")
            Dispatcher.UIThread.Post(() => RecognitionPage.Offset = new Vector(0, 0), DispatcherPriority.Loaded);
        if (page != "project")
            WorkspaceVideoHost?.UpdateNativeVisibility(false);

        Control target = page switch
        {
            "tasks" => TasksPage,
            "recognition" => RecognitionPage,
            "models" => ModelsPage,
            "settings" => SettingsPage,
            "project" => ProjectPage,
            _ => OverviewPage
        };

        MoveSidebarIndicator(page);

        OverviewNav.Classes.Set("selected", page == "overview");
        TasksNav.Classes.Set("selected", page == "tasks");
        RecognitionNav.Classes.Set("selected", page == "recognition");
        ModelsNav.Classes.Set("selected", page == "models");
        SettingsNav.Classes.Set("selected", page == "settings");
        SetProjectSelection(page == "project" ? _activeProjectId : null);

        var allPages = new Control[] { OverviewPage, TasksPage, RecognitionPage, ModelsPage, SettingsPage, ProjectPage };

        var current = _activePage;
        if (ReferenceEquals(target, current))
        {
            foreach (var p in allPages)
            {
                if (!ReferenceEquals(p, target))
                    p.IsVisible = false;
            }

            var group = GetPageMotionGroup(target);
            _motion.RestorePage(target, group);

            if (ReferenceEquals(target, ModelsPage))
                _ = RefreshModelCatalogAsync();
            if (ReferenceEquals(target, SettingsPage))
                ScheduleSettingsStatusRefresh();
            return;
        }

        _activePage = target;

        var swapImmediately =
            ReferenceEquals(current, RecognitionPage) ||
            ReferenceEquals(target, RecognitionPage) ||
            ReferenceEquals(current, SettingsPage) ||
            ReferenceEquals(target, SettingsPage);

        if (swapImmediately)
        {
            current.IsVisible = false;
            var targetGroup = GetPageMotionGroup(target);
            _motion.RestorePage(target, targetGroup);
        }
        else
        {
            SetupButtonMotion(target);
            if (ReferenceEquals(target, ModelsPage))
                SetupModelRowMotion(ModelsPage);
            var currentGroup = GetPageMotionGroup(current);
            var targetGroup = GetPageMotionGroup(target);

            try
            {
                await _motion.PageTransitionAsync(
                    current,
                    target,
                    currentGroup,
                    targetGroup,
                    navigation.Token);
            }
            catch (OperationCanceledException)
            {
                // A newer navigation request took over
            }
        }

        if (epoch == _navigationEpoch)
        {
            foreach (var p in allPages)
            {
                if (!ReferenceEquals(p, target))
                {
                    p.IsVisible = false;
                }
            }
            var targetGroup = GetPageMotionGroup(target);
            _motion.RestorePage(target, targetGroup);

            if (page != "project" && _workspacePlayer.IsRunning && !_workspacePlayer.IsPaused)
                _ = PauseWorkspaceAfterNavigationAsync();
            if (page != "recognition" && _translationApiDrawerOpen)
                _ = CloseTranslationDrawerAfterNavigationAsync();

            if (ReferenceEquals(target, ModelsPage))
                _ = RefreshModelCatalogAsync();
            if (ReferenceEquals(target, SettingsPage))
                ScheduleSettingsStatusRefresh();
        }
    }

    private async Task PauseWorkspaceAfterNavigationAsync()
    {
        try
        {
            await _workspacePlayer.SetPauseAsync(true);
        }
        catch
        {
            // The native video surface is already hidden. A later workspace
            // activation will reconcile playback state if mpv was shutting down.
        }
    }

    private async Task CloseTranslationDrawerAfterNavigationAsync()
    {
        for (var attempt = 0; attempt < 15 && _translationApiDrawerAnimating; attempt++)
            await Task.Delay(16);
        if (_translationApiDrawerOpen && !_translationApiDrawerAnimating)
            await CloseTranslationApiDrawerAsync();
    }

    private void ScheduleSettingsStatusRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_activePage, SettingsPage))
                RefreshSettingsHardwareAndCache();
        }, DispatcherPriority.Background);
    }

    private void MoveSidebarIndicator(string page)
    {
        SidebarNavIndicator.IsVisible = page != "project";
        if (page == "project") return;

        var targetNav = page switch
        {
            "tasks" => TasksNav,
            "recognition" => RecognitionNav,
            "models" => ModelsNav,
            "settings" => SettingsNav,
            _ => OverviewNav
        };
        var targetPoint = targetNav.TranslatePoint(new Point(0, 0), SidebarRoot);
        var overviewPoint = OverviewNav.TranslatePoint(new Point(0, 0), SidebarRoot);
        if (targetPoint.HasValue && overviewPoint.HasValue)
        {
            var offset = targetPoint.Value.Y - overviewPoint.Value.Y;
            _motion.AnimateSidebarIndicator(SidebarNavIndicator, offset);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                var tp = targetNav.TranslatePoint(new Point(0, 0), SidebarRoot);
                var op = OverviewNav.TranslatePoint(new Point(0, 0), SidebarRoot);
                var offset = tp.HasValue && op.HasValue
                    ? tp.Value.Y - op.Value.Y
                    : targetNav.Bounds.Y - OverviewNav.Bounds.Y;
                _motion.AnimateSidebarIndicator(SidebarNavIndicator, offset);
            }, DispatcherPriority.Loaded);
        }
    }

    private static IReadOnlyList<Control> GetMotionItems(Control root, string className)
    {
        var items = root.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.Classes.Contains(className))
            .ToList();
        if (items.Count == 0)
        {
            items = root.GetLogicalDescendants()
                .OfType<Control>()
                .Where(control => control.Classes.Contains(className))
                .ToList();
        }
        return items;
    }

    private PageMotionGroup GetPageMotionGroup(Control page)
    {
        var group = new PageMotionGroup();
        if (ReferenceEquals(page, RecognitionPage))
        {
            var leftList = new List<Control>();
            var searchBox = RecognitionPage.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("translationProviderSearch"));
            if (searchBox != null) leftList.Add(searchBox);

            if (TranslationProviderListHost != null)
            {
                foreach (var btn in TranslationProviderListHost.Children.OfType<Control>())
                {
                    if (btn.IsVisible) leftList.Add(btn);
                }
            }
            group.LeftItems = leftList;

            var rightList = new List<Control>();
            if (TranslationProfilePanel != null)
            {
                foreach (var child in TranslationProfilePanel.Children.OfType<Control>())
                {
                    if (child.IsVisible) rightList.Add(child);
                }
            }
            if (rightList.Count == 0 && TranslationProfilePanel != null)
                rightList.Add(TranslationProfilePanel);
            group.RightItems = rightList;
            return group;
        }

        group.RightItems = GetPageMotionItems(page);
        return group;
    }

    private static IReadOnlyList<Control> GetPageMotionItems(Control page)
    {
        var result = new List<Control>();
        var content = page is ScrollViewer { Content: Control scrollContent }
            ? scrollContent
            : page;

        if (content is Panel panel)
        {
            foreach (var child in panel.Children.OfType<Control>())
            {
                if (child.IsVisible)
                    result.Add(child);
            }
        }
        else
        {
            CollectPageMotionItems(content, result, includeSelf: false);
        }

        if (result.Count == 0 && content != null)
        {
            result.Add(content);
        }

        return result;
    }

    // Treat cards, hints, and major sections as animation leaves while traversing containers.
    private static void CollectPageMotionItems(Control control, ICollection<Control> result, bool includeSelf = true)
    {
        if (includeSelf && !control.IsVisible) return;
        var isCard = control is Border &&
            (control.Classes.Contains("card") ||
             control.Classes.Contains("deploymentCard") ||
             control.Classes.Contains("settingsPanel") ||
             control.Classes.Contains("deployCard"));
        var isHint = control is Border && control.Classes.Contains("motion-item") && !isCard;
        var isAnimationLeaf = isCard || isHint;

        if (includeSelf && isAnimationLeaf)
        {
            result.Add(control);
            return;
        }

        var children = control.GetVisualChildren().OfType<Control>().ToList();
        if (children.Count == 0)
            children = control.GetLogicalChildren().OfType<Control>().ToList();

        foreach (var child in children)
            CollectPageMotionItems(child, result);
    }

    private void SetupButtonMotion(Control root)
    {
        var buttons = root.GetVisualDescendants().OfType<Button>().ToList();
        if (buttons.Count == 0)
            buttons = root.GetLogicalDescendants().OfType<Button>().ToList();

        foreach (var button in buttons)
        {
            // Expandable card headers keep their full width during interaction.
            if (button.Classes.Contains("deploymentCardHeader")) continue;
            if (button.Classes.Contains("titleBarCommand")) continue;
            if (button.Classes.Contains("windowButton")) continue;
            if (button.Classes.Contains("nav") || button.Classes.Contains("sidebarCreate")) continue;
            if (button.Classes.Contains("projectListHeader") ||
                button.Classes.Contains("projectListAdd") ||
                button.Classes.Contains("projectItemMore") ||
                button.Classes.Contains("taskNavExpand") ||
                button.Classes.Contains("taskOverviewRow")) continue;
            // Provider-card lift and icon rotation are handled by XAML transitions. Do not layer the generic
            // elastic button scale on top of that motion.
            if (button.Classes.Contains("translationProviderCard")) continue;
            // Provider switching is deliberately static: the row
            // changes selection immediately without press scale or rebound.
            if (button.Classes.Contains("translationProviderListRow")) continue;
            if (button.Classes.Contains("settingsCatNav") ||
                button.Classes.Contains("actionPrimarySmall") ||
                button.Classes.Contains("actionOutlineSmall") ||
                button.Classes.Contains("actionDangerSmall") ||
                button.Classes.Contains("actionLink")) continue;
            if (button.Classes.Contains("noButtonMotion")) continue;
            if (!_motionButtons.Add(button)) continue;
            button.PointerEntered += Button_OnPointerEntered;
            button.PointerPressed += Button_OnPointerPressed;
            button.PointerReleased += Button_OnPointerReleased;
            button.PointerExited += Button_OnPointerExited;
        }
    }

    private void SetupModelRowMotion(Control root)
    {
        foreach (var row in root.GetLogicalDescendants().OfType<Border>()
                     .Where(border => border.Classes.Contains("deploymentModelRow")).ToList())
        {
            if (!_motionModelRows.Add(row)) continue;
            if (row.Child is not Control content) continue;

            row.Child = null;
            content.Margin = new Thickness(15, 6, 8, 6);
            var hoverLayer = new Border
            {
                Margin = new Thickness(3, 2),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#D5E6FD")),
                Background = new SolidColorBrush(Color.Parse("#E0EAFD")),
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(0.8, 0.8)
            };
            hoverLayer.Classes.Add("modelRowHover");
            var host = new Grid();
            host.Children.Add(hoverLayer);
            host.Children.Add(content);
            row.Child = host;

            row.PointerEntered += ModelRow_OnPointerEntered;
            row.PointerPressed += ModelRow_OnMotionPointerPressed;
            row.PointerReleased += ModelRow_OnMotionPointerReleased;
            row.PointerExited += ModelRow_OnPointerExited;
        }
    }

    private void ModelRow_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border row)
            _motion.AnimateModelListItem(row, hovered: true, pressed: _pressedModelRows.Contains(row));
    }

    private void ModelRow_OnMotionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        _pressedModelRows.Add(row);
        _motion.AnimateModelListItem(row, hovered: true, pressed: true);
    }

    private void ModelRow_OnMotionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border row || e.InitialPressMouseButton != MouseButton.Left) return;
        _pressedModelRows.Remove(row);
        _motion.AnimateModelListItem(row, hovered: row.IsPointerOver, pressed: false);
    }

    private void ModelRow_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border row) return;
        _pressedModelRows.Remove(row);
        _motion.AnimateModelListItem(row, hovered: false, pressed: false);
    }

    private void Button_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        if (button.Classes.Contains("translationProviderListRow"))
        {
            button.RenderTransform = null;
            return;
        }
        if (button.Classes.Contains("nav"))
        {
            _motion.AnimateNavigationPress(button);
            return;
        }
        _motion.AnimateButtonPress(button, button.Classes.Contains("windowButton"));
    }

    private void Button_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Classes.Contains("translationProviderListRow"))
        {
            button.RenderTransform = null;
            return;
        }
        if (button.Classes.Contains("nav"))
            _motion.AnimateNavigationEnter(button);
        else
            _motion.AnimateButtonEnter(button, button.Classes.Contains("windowButton"));
    }

    private void Button_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Classes.Contains("translationProviderListRow"))
        {
            button.RenderTransform = null;
            return;
        }
        if (button.Classes.Contains("nav"))
            _motion.AnimateNavigationRelease(button, button.IsPointerOver);
        else
            _motion.AnimateButtonRelease(button, button.Classes.Contains("windowButton"), button.IsPointerOver);
    }

    private void Button_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Classes.Contains("translationProviderListRow"))
        {
            button.RenderTransform = null;
            return;
        }
        if (button.Classes.Contains("nav"))
            _motion.AnimateNavigationExit(button);
        else
            _motion.AnimateButtonExit(button, button.Classes.Contains("windowButton"));
    }
}
