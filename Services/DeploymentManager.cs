using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AstraCat;

public sealed record DeploymentState(string Id, bool Installed, string Path);

public sealed record DeploymentProgress(
    string Message,
    double? Fraction = null,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? Remaining = null);

public sealed record CudaRuntimeOption(string Version, string Label, long DownloadBytes);

public sealed record CudaRuntimeStatus(
    bool HasNvidiaGpu,
    string? GpuName,
    string? DriverVersion,
    string? InstalledVersion,
    bool Ready,
    bool TorchReady,
    string Summary);

public enum ModelDownloadSource
{
    Auto,
    HfMirror,
    HuggingFace
}

/// <summary>
/// Keeps the desktop shell independent from Python while still using the real
/// runtime and weight files as the source of truth for deployment status.
/// </summary>
public sealed class DeploymentManager
{
    private sealed record CudaPackage(string Url, string Sha256, long Bytes, string LicenseName);
    private sealed record CudaRelease(string Version, CudaPackage Cublas, CudaPackage Cudart);

    private const string PythonVersion = "3.12.10";
    private const string PythonPackageUrl =
        "https://api.nuget.org/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg";
    private const string PythonPackageSha256 =
        "0EB85C2DFCCCCF1B17352DE4C397F69194035B7D37149EACC16F1147D93DE3B8";

    public static IReadOnlyList<CudaRuntimeOption> CudaRuntimeOptions { get; } =
    [
        new("12.8", "CUDA 12.8（推荐 · 541 MiB）", 566_698_679),
        new("12.4", "CUDA 12.4（兼容 · 376 MiB）", 394_013_208),
        new("cpu", "仅 CPU（不下载）", 0)
    ];

    private static readonly IReadOnlyDictionary<string, CudaRelease> CudaReleases =
        new Dictionary<string, CudaRelease>(StringComparer.OrdinalIgnoreCase)
        {
            ["12.8"] = new("12.8",
                new("https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.8.4.1-archive.zip",
                    "57A470112CEC7E112C95253DDE8B3C7184D795DBD92B0BDE77A4CB7F8C94C8AA", 563_660_944, "CUDA-cuBLAS-LICENSE.txt"),
                new("https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.8.90-archive.zip",
                    "4A39058FD8519444A81CFC7AE055D136F48D1A31FFA41AE255B35B2EDD61E13B", 3_037_735, "CUDA-Runtime-LICENSE.txt")),
            ["12.4"] = new("12.4",
                new("https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.4.5.8-archive.zip",
                    "698140F12DA055A3709EEE2E022FCFE7BC8EDF31F30115E3F7A5C877A9491DE5", 391_538_487, "CUDA-cuBLAS-LICENSE.txt"),
                new("https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.4.127-archive.zip",
                    "6A1C32E68EE1A95CA17334691FF9AD1FFE7F352C24A083D55E4C96B8063B2BCB", 2_474_721, "CUDA-Runtime-LICENSE.txt"))
        };

    public string AppRoot { get; } = FindAppRoot();
    public string RuntimeRoot => Path.Combine(AppRoot, "runtime");
    public string PythonRoot => Path.Combine(RuntimeRoot, "python");
    public string PythonExecutable => Path.Combine(PythonRoot, "Scripts", "python.exe");
    private string PythonBaseRoot => Path.Combine(RuntimeRoot, "python-base");
    private string PythonBaseExecutable => Path.Combine(PythonBaseRoot, "python.exe");
    public string ModelRoot => Path.Combine(RuntimeRoot, "models");
    public string EnvironmentRoot => Path.Combine(RuntimeRoot, "e");
    public string GpuRuntimeRoot => Path.Combine(RuntimeRoot, "gpu");

    public string GetRuntimePythonExecutable(string runtimeId) => runtimeId switch
    {
        "nvidia-runtime" or "funasr-runtime" or "nemo-runtime" or "moss-runtime" =>
            Path.Combine(EnvironmentRoot, RuntimeFolderName(runtimeId), "Scripts", "python.exe"),
        _ => PythonExecutable
    };

    private readonly object _inspectLock = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _torchProbeGate = new(1, 1);
    private readonly object _torchProbeCacheLock = new();
    private string? _torchProbeCacheKey;
    private bool _torchProbeCacheValue;
    private DateTime _torchProbeCacheExpiresAt;
    private IReadOnlyDictionary<string, DeploymentState>? _cachedStates;
    private bool? _cachedPythonReady;
    private long _cachedPythonLength = -1;
    private DateTime _cachedPythonWriteTimeUtc = DateTime.MinValue;
    private DateTime _lastInspectTime = DateTime.MinValue;
    private static readonly TimeSpan InspectCacheTtl = TimeSpan.FromSeconds(20);

    public void InvalidateInspectCache()
    {
        lock (_inspectLock)
        {
            _cachedStates = null;
            _lastInspectTime = DateTime.MinValue;
        }
    }

    public IReadOnlyDictionary<string, DeploymentState> Inspect(bool forceRefresh = false)
    {
        lock (_inspectLock)
        {
            if (!forceRefresh && _cachedStates is not null && (DateTime.UtcNow - _lastInspectTime) < InspectCacheTtl)
                return _cachedStates;

            var sitePackages = Path.Combine(PythonRoot, "Lib", "site-packages");
            // Starting Python merely to read its version is the most expensive part
            // of inspection. Reuse the result until the executable fingerprint changes.
            var pythonReady = IsManagedPythonReady();
            var nvidiaPackages = RuntimeSitePackages("nvidia-runtime");
            var funAsrPackages = RuntimeSitePackages("funasr-runtime");
            var nemoPackages = RuntimeSitePackages("nemo-runtime");
            var mossPackages = RuntimeSitePackages("moss-runtime");
            var states = new Dictionary<string, DeploymentState>(StringComparer.OrdinalIgnoreCase)
            {
                ["python-runtime"] = State("python-runtime", pythonReady, PythonRoot),
                ["whisper-runtime"] = State("whisper-runtime",
                    pythonReady && Directory.Exists(Path.Combine(sitePackages, "faster_whisper")), PythonRoot),
                ["qwen-runtime"] = State("qwen-runtime",
                    pythonReady && Directory.Exists(Path.Combine(sitePackages, "qwen_asr")) &&
                    Directory.Exists(Path.Combine(sitePackages, "torch")), PythonRoot),
                ["nvidia-runtime"] = State("nvidia-runtime",
                    File.Exists(GetRuntimePythonExecutable("nvidia-runtime")) &&
                    Directory.Exists(Path.Combine(nvidiaPackages, "transformers")) &&
                    RuntimeCanResolvePackage(nvidiaPackages, "torch") &&
                    RuntimeCanResolvePackage(nvidiaPackages, "librosa") &&
                    RuntimeCanResolvePackage(nvidiaPackages, "soundfile") &&
                    HasPythonPackageVersion(nvidiaPackages, "transformers", new Version(5, 9)),
                    RuntimePath("nvidia-runtime")),
                ["funasr-runtime"] = State("funasr-runtime",
                    File.Exists(GetRuntimePythonExecutable("funasr-runtime")) &&
                    Directory.Exists(Path.Combine(funAsrPackages, "funasr")),
                    RuntimePath("funasr-runtime")),
                ["nemo-runtime"] = State("nemo-runtime",
                    File.Exists(GetRuntimePythonExecutable("nemo-runtime")) &&
                    Directory.Exists(Path.Combine(nemoPackages, "nemo")),
                    RuntimePath("nemo-runtime")),
                ["moss-runtime"] = State("moss-runtime",
                    File.Exists(GetRuntimePythonExecutable("moss-runtime")) &&
                    Directory.Exists(Path.Combine(mossPackages, "moss_transcribe_diarize")),
                    RuntimePath("moss-runtime")),
                ["whisper-tiny"] = ModelState("whisper-tiny", "model.bin"),
                ["whisper-base"] = ModelState("whisper-base", "model.bin"),
                ["whisper-small"] = ModelState("whisper-small", "model.bin"),
                ["whisper-medium"] = ModelState("whisper-medium", "model.bin"),
                ["whisper-large-v3"] = ModelState("whisper-large-v3", "model.bin"),
                ["whisper-v3-turbo"] = ModelState("whisper-large-v3-turbo", "*.safetensors"),
                ["qwen-0.6b"] = ModelState("qwen3-asr-0.6b", "*.safetensors"),
                ["qwen-1.7b"] = ModelState("qwen3-asr-1.7b", "*.safetensors"),
                ["funasr-nano"] = ModelState("fun-asr-nano-2512", "model.pt"),
                ["sensevoice-small"] = ModelState("sensevoice-small", "model.pt"),
                ["nvidia-parakeet-v3"] = ModelState("nvidia-parakeet-tdt-0.6b-v3", "*.safetensors"),
                ["nvidia-canary-v2"] = ModelState("nvidia-canary-1b-v2", "*.nemo"),
                ["moss-0.9b"] = ModelState("moss-transcribe-diarize-0.9b", "*.safetensors")
            };

            _cachedStates = states;
            _lastInspectTime = DateTime.UtcNow;
            return states;
        }
    }

    private bool IsManagedPythonReady()
    {
        long length = -1;
        var writeTimeUtc = DateTime.MinValue;
        try
        {
            var executable = new FileInfo(PythonExecutable);
            if (executable.Exists)
            {
                length = executable.Length;
                writeTimeUtc = executable.LastWriteTimeUtc;
            }
        }
        catch (IOException)
        {
            // Fall through to the real process check. A transient metadata read
            // failure must not permanently poison the cached readiness state.
        }
        catch (UnauthorizedAccessException)
        {
            // Same behavior as an unavailable fingerprint.
        }

        if (_cachedPythonReady is { } cached &&
            length == _cachedPythonLength && writeTimeUtc == _cachedPythonWriteTimeUtc)
            return cached;

        var ready = IsPython312(PythonExecutable);
        _cachedPythonReady = ready;
        _cachedPythonLength = length;
        _cachedPythonWriteTimeUtc = writeTimeUtc;
        return ready;
    }

    public string GetTargetPath(string id) => Inspect().TryGetValue(id, out var state)
        ? state.Path
        : RuntimeRoot;

    public async Task InstallAsync(
        string id,
        IProgress<DeploymentProgress>? progress,
        CancellationToken token,
        ModelDownloadSource source = ModelDownloadSource.Auto)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            await InstallCoreAsync(id, progress, token, source);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task InstallCoreAsync(
        string id,
        IProgress<DeploymentProgress>? progress,
        CancellationToken token,
        ModelDownloadSource source)
    {
        if (id == "python-runtime")
        {
            await InstallPythonRuntimeAsync(progress, token);
            return;
        }
        if (!IsPython312(PythonExecutable))
            await InstallPythonRuntimeAsync(progress, token);

        Directory.CreateDirectory(ModelRoot);
        progress?.Report(new DeploymentProgress("正在准备下载…"));

        if (id.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase) &&
            !id.EndsWith("-runtime", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new DeploymentProgress("正在预检 Whisper CUDA 运行环境…"));
            var cudaWarning = await GetWhisperCudaWarningAsync(token);
            if (!string.IsNullOrWhiteSpace(cudaWarning))
                progress?.Report(new DeploymentProgress(cudaWarning));
        }

        var isRuntime = id.EndsWith("-runtime", StringComparison.OrdinalIgnoreCase);
        if (isRuntime)
            await EnsurePipAsync(progress, token);

        if (id is "nvidia-runtime" or "funasr-runtime" or "nemo-runtime" or "moss-runtime")
            await EnsureIsolatedRuntimeAsync(id, progress, token);

        var arguments = id switch
        {
            "whisper-runtime" => PipArguments("faster-whisper==1.2.1", source),
            "qwen-runtime" => PipArguments("qwen-asr==0.0.6", source),
            "nvidia-runtime" => PipArguments(
                "transformers>=5.9.0,<6 torch accelerate librosa soundfile sentencepiece " +
                "huggingface-hub>=1.5,<2 tokenizers>=0.23.1,<0.24 safetensors>=0.8 typer", source),
            "funasr-runtime" => PipArguments("funasr==1.4.4 modelscope huggingface-hub", source),
            "nemo-runtime" => PipArguments("nemo_toolkit[asr]>=2.5,<4", source),
            "moss-runtime" => PipArguments("git+https://github.com/OpenMOSS/MOSS-Transcribe-Diarize.git", source),
            "whisper-tiny" => SnapshotArguments("Systran/faster-whisper-tiny", Path.Combine(ModelRoot, "whisper-tiny"), source),
            "whisper-base" => SnapshotArguments("Systran/faster-whisper-base", Path.Combine(ModelRoot, "whisper-base"), source),
            "whisper-small" => SnapshotArguments("Systran/faster-whisper-small", Path.Combine(ModelRoot, "whisper-small"), source),
            "whisper-medium" => SnapshotArguments("Systran/faster-whisper-medium", Path.Combine(ModelRoot, "whisper-medium"), source),
            "whisper-large-v3" => SnapshotArguments("Systran/faster-whisper-large-v3", Path.Combine(ModelRoot, "whisper-large-v3"), source),
            "whisper-v3-turbo" => SnapshotArguments(
                "openai/whisper-large-v3-turbo", Path.Combine(ModelRoot, "whisper-large-v3-turbo"), source,
                ["*.json", "*.safetensors", "*.txt", "tokenizer*", "preprocessor*", "vocab.json", "merges.txt"]),
            "qwen-0.6b" => SnapshotArguments("Qwen/Qwen3-ASR-0.6B", Path.Combine(ModelRoot, "qwen3-asr-0.6b"), source),
            "qwen-1.7b" => SnapshotArguments("Qwen/Qwen3-ASR-1.7B", Path.Combine(ModelRoot, "qwen3-asr-1.7b"), source),
            "funasr-nano" => SnapshotArguments("FunAudioLLM/Fun-ASR-Nano-2512", Path.Combine(ModelRoot, "fun-asr-nano-2512"), source),
            "sensevoice-small" => SnapshotArguments("FunAudioLLM/SenseVoiceSmall", Path.Combine(ModelRoot, "sensevoice-small"), source),
            "nvidia-parakeet-v3" => SnapshotArguments(
                "nvidia/parakeet-tdt-0.6b-v3", Path.Combine(ModelRoot, "nvidia-parakeet-tdt-0.6b-v3"), source,
                ["*.json", "*.safetensors", "*.model", "*.txt", "tokenizer*", "preprocessor*"]),
            "nvidia-canary-v2" => SnapshotArguments(
                "nvidia/canary-1b-v2", Path.Combine(ModelRoot, "nvidia-canary-1b-v2"), source,
                ["*.nemo", "*.json", "*.jinja", "*.model", "*.txt", "tokenizer*", "preprocessor*"]),
            "moss-0.9b" => SnapshotArguments("OpenMOSS-Team/MOSS-Transcribe-Diarize", Path.Combine(ModelRoot, "moss-transcribe-diarize-0.9b"), source),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "未知部署组件")
        };

        await RunPythonAsync(
            arguments,
            progress,
            token,
            progressDirectory: ModelProgressDirectory(id),
            expectedBytes: ExpectedDownloadBytes(id),
            pythonExecutable: isRuntime ? GetRuntimePythonExecutable(id) : null);

        InvalidateInspectCache();
        if (!isRuntime && (!Inspect(forceRefresh: true).TryGetValue(id, out var installedState) || !installedState.Installed))
            throw new InvalidOperationException("下载进程已结束，但模型权重不完整，请重试下载。");
    }

    public async Task<string?> GetWhisperCudaWarningAsync(CancellationToken token)
    {
        if (!IsPython312(PythonExecutable) ||
            !Directory.Exists(Path.Combine(PythonRoot, "Lib", "site-packages", "ctranslate2")))
            return null;

        var info = new ProcessStartInfo
        {
            FileName = PythonExecutable,
            WorkingDirectory = AppRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureCudaEnvironment(info);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(
            "import sys;sys.path.insert(0,'engines');import asr_worker;print('ready' if asr_worker.cuda_available() else 'cpu')");
        try
        {
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return "CUDA 预检无法启动，将使用 CPU 识别";
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            using var registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });
            await process.WaitForExitAsync(token);
            var output = await outputTask;
            _ = await errorTask;
            token.ThrowIfCancellationRequested();
            return process.ExitCode == 0 && output.Contains("ready", StringComparison.Ordinal)
                ? null
                : "未检测到完整的 CUDA 12 与 cuDNN 9 环境，将自动使用 CPU 识别";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "CUDA 预检未通过，将自动使用 CPU 识别";
        }
    }

    public async Task<CudaRuntimeStatus> GetCudaRuntimeStatusAsync(CancellationToken token)
    {
        var (gpuName, driverVersion) = await QueryNvidiaGpuAsync(token);
        var version = ReadActiveCudaVersion();
        var bin = version is null ? null : CudaBinPath(version);
        var filesReady = bin is not null &&
                         File.Exists(Path.Combine(bin, "cublas64_12.dll")) &&
                         File.Exists(Path.Combine(bin, "cublasLt64_12.dll")) &&
                         File.Exists(Path.Combine(bin, "cudart64_12.dll"));
        var torchReady = filesReady && await ProbeTorchCudaAsync(PythonExecutable, token);
        var ready = gpuName is not null && filesReady;
        var summary = gpuName is null
            ? "未检测到 NVIDIA 显卡，将使用 CPU"
            : !filesReady
                ? "检测到 NVIDIA 显卡，尚未安装 AstraCat CUDA 运行库"
                : !torchReady
                    ? "CUDA 运行库已安装，但当前 PyTorch 仅支持 CPU"
                    : $"GPU 加速已就绪 · CUDA {version}";
        return new CudaRuntimeStatus(gpuName is not null, gpuName, driverVersion, version, ready, torchReady, summary);
    }

    public async Task InstallTorchCudaAsync(
        string version, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        var wheelChannel = version switch
        {
            "12.8" => "cu128",
            "12.4" => "cu124",
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "不支持的 PyTorch CUDA 版本")
        };

        await _mutationGate.WaitAsync(token);
        try
        {
            if (await ProbeTorchCudaAsync(PythonExecutable, token)) return;
            progress?.Report(new DeploymentProgress($"正在安装 PyTorch CUDA {version} 运行时…"));
            await RunPythonAsync(
                $"-m pip install --upgrade --force-reinstall torch --index-url https://download.pytorch.org/whl/{wheelChannel} " +
                "--progress-bar raw --disable-pip-version-check",
                progress, token, runningMessage: "正在安装 PyTorch CUDA…");
            lock (_torchProbeCacheLock)
            {
                _torchProbeCacheKey = null;
                _torchProbeCacheExpiresAt = DateTime.MinValue;
            }
            if (!await ProbeTorchCudaAsync(PythonExecutable, token))
                throw new InvalidOperationException("PyTorch CUDA 安装完成，但 GPU 自检未通过；请检查 NVIDIA 驱动版本");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<bool> ProbeTorchCudaAsync(string pythonExecutable, CancellationToken token)
    {
        var baseTorch = Path.Combine(PythonRoot, "Lib", "site-packages", "torch", "__init__.py");
        var runtimeTorch = Path.Combine(
            RuntimeSitePackages("nvidia-runtime"), "torch", "__init__.py");
        var torchPath = File.Exists(runtimeTorch) ? runtimeTorch : baseTorch;
        var cacheKey = string.Join('|',
            pythonExecutable,
            File.Exists(pythonExecutable) ? File.GetLastWriteTimeUtc(pythonExecutable).Ticks : 0,
            File.Exists(torchPath) ? File.GetLastWriteTimeUtc(torchPath).Ticks : 0,
            ReadActiveCudaVersion());
        lock (_torchProbeCacheLock)
        {
            if (cacheKey == _torchProbeCacheKey && DateTime.UtcNow < _torchProbeCacheExpiresAt)
                return _torchProbeCacheValue;
        }

        await _torchProbeGate.WaitAsync(token);
        try
        {
            // A concurrent caller may have completed the same expensive Python
            // probe while this request was waiting for the gate.
            lock (_torchProbeCacheLock)
            {
                if (cacheKey == _torchProbeCacheKey && DateTime.UtcNow < _torchProbeCacheExpiresAt)
                    return _torchProbeCacheValue;
            }

            var result = await ProbeTorchCudaCoreAsync(pythonExecutable, token);
            lock (_torchProbeCacheLock)
            {
                _torchProbeCacheKey = cacheKey;
                _torchProbeCacheValue = result;
                _torchProbeCacheExpiresAt = DateTime.UtcNow.AddSeconds(20);
            }
            return result;
        }
        finally
        {
            _torchProbeGate.Release();
        }
    }

    private async Task<bool> ProbeTorchCudaCoreAsync(string pythonExecutable, CancellationToken token)
    {
        var info = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = AppRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureCudaEnvironment(info);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import torch;print('ready' if torch.cuda.is_available() and torch.cuda.device_count() > 0 else 'cpu')");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return false;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            using var registration = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            token.ThrowIfCancellationRequested();
            return process.ExitCode == 0 &&
                   output.Trim().Equals("ready", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task InstallCudaRuntimeAsync(
        string version, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            await InstallCudaRuntimeCoreAsync(version, progress, token);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task InstallCudaRuntimeCoreAsync(
        string version, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("AstraCat CUDA 自动安装当前仅支持 Windows x64。");
        if (!CudaReleases.TryGetValue(version, out var release))
            throw new ArgumentOutOfRangeException(nameof(version), version, "不支持的 CUDA 运行库版本");

        var gpu = await GetCudaRuntimeStatusAsync(token);
        if (!gpu.HasNvidiaGpu)
            throw new InvalidOperationException("未检测到 NVIDIA 显卡，无法启用 CUDA 加速。");

        var target = CudaVersionPath(version);
        var targetBin = Path.Combine(target, "bin");
        if (Directory.Exists(targetBin) &&
            File.Exists(Path.Combine(targetBin, "cublas64_12.dll")) &&
            File.Exists(Path.Combine(targetBin, "cublasLt64_12.dll")) &&
            File.Exists(Path.Combine(targetBin, "cudart64_12.dll")))
        {
            WriteActiveCudaVersion(version);
            progress?.Report(new DeploymentProgress($"CUDA {version} 加速运行库已就绪", 1));
            return;
        }

        var cache = Path.Combine(RuntimeRoot, "cache", "cuda");
        Directory.CreateDirectory(cache);
        var packages = new[] { release.Cublas, release.Cudart };
        var totalBytes = packages.Sum(package => package.Bytes);
        long completedBytes = 0;
        var archives = new List<(CudaPackage Package, string Path)>();
        try
        {
            foreach (var package in packages)
            {
                var archive = Path.Combine(cache, Path.GetFileName(new Uri(package.Url).LocalPath));
                if (!await HasHashAsync(archive, package.Sha256, token))
                {
                    try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                    await DownloadCudaPackageAsync(
                        package, archive, completedBytes, totalBytes, progress, token);
                }
                if (!await HasHashAsync(archive, package.Sha256, token))
                    throw new InvalidOperationException($"CUDA 下载文件校验失败：{Path.GetFileName(archive)}");
                archives.Add((package, archive));
                completedBytes += package.Bytes;
            }

            var staging = Path.Combine(GpuRuntimeRoot, $"cuda-{version}.installing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(staging, "bin"));
            try
            {
                progress?.Report(new DeploymentProgress($"正在安装 CUDA {version} 私有运行库…"));
                foreach (var (package, archivePath) in archives)
                    ExtractCudaArchive(archivePath, staging, package.LicenseName);
                File.WriteAllText(Path.Combine(staging, "VERSION.txt"),
                    $"CUDA runtime {version}{Environment.NewLine}Source: NVIDIA redistributable archives{Environment.NewLine}");
                if (!File.Exists(Path.Combine(staging, "bin", "cublas64_12.dll")) ||
                    !File.Exists(Path.Combine(staging, "bin", "cublasLt64_12.dll")) ||
                    !File.Exists(Path.Combine(staging, "bin", "cudart64_12.dll")))
                    throw new InvalidOperationException("CUDA 运行库解压完成，但关键 DLL 不完整。");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            }
            WriteActiveCudaVersion(version);
            progress?.Report(new DeploymentProgress($"CUDA {version} GPU 加速安装完成", 1, totalBytes, totalBytes));
        }
        finally
        {
            foreach (var (_, archive) in archives)
                try { if (File.Exists(archive)) File.Delete(archive); } catch { }
        }
    }

    public void ConfigureCudaEnvironment(ProcessStartInfo info)
    {
        var version = ReadActiveCudaVersion();
        if (version is null) return;
        var bin = CudaBinPath(version);
        if (!Directory.Exists(bin)) return;
        var currentPath = info.Environment.TryGetValue("PATH", out var value)
            ? value
            : Environment.GetEnvironmentVariable("PATH");
        info.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? bin
            : bin + Path.PathSeparator + currentPath;
        info.Environment["ASTRACAT_CUDA_BIN"] = bin;
    }

    private string CudaVersionPath(string version) => Path.Combine(GpuRuntimeRoot, $"cuda-{version}");
    private string CudaBinPath(string version) => Path.Combine(CudaVersionPath(version), "bin");
    private string ActiveCudaPath => Path.Combine(GpuRuntimeRoot, "active.json");

    private string? ReadActiveCudaVersion()
    {
        try
        {
            if (File.Exists(ActiveCudaPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(ActiveCudaPath));
                var version = document.RootElement.GetProperty("version").GetString();
                if (version is not null && CudaReleases.ContainsKey(version)) return version;
            }
        }
        catch { }

        foreach (var candidate in new[] { "12.8", "12.4" })
        {
            var bin = CudaBinPath(candidate);
            if (Directory.Exists(bin) &&
                File.Exists(Path.Combine(bin, "cublas64_12.dll")) &&
                File.Exists(Path.Combine(bin, "cublasLt64_12.dll")) &&
                File.Exists(Path.Combine(bin, "cudart64_12.dll")))
            {
                try { WriteActiveCudaVersion(candidate); } catch { }
                return candidate;
            }
        }
        return null;
    }

    private void WriteActiveCudaVersion(string version)
    {
        Directory.CreateDirectory(GpuRuntimeRoot);
        var temporary = ActiveCudaPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { version }));
        File.Move(temporary, ActiveCudaPath, overwrite: true);
    }

    private static string? _cachedGpuName;
    private static string? _cachedDriverVersion;
    private static bool _gpuQueried;

    private static async Task<(string? GpuName, string? DriverVersion)> QueryNvidiaGpuAsync(CancellationToken token)
    {
        if (_gpuQueried && _cachedGpuName is not null)
            return (_cachedGpuName, _cachedDriverVersion);

        try
        {
            var candidates = new[]
            {
                "nvidia-smi.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
            };
            var executable = candidates.FirstOrDefault(File.Exists) ?? "nvidia-smi.exe";

            var info = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("--query-gpu=name,driver_version");
            info.ArgumentList.Add("--format=csv,noheader");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var process = new Process { StartInfo = info };
            if (!process.Start())
            {
                _gpuQueried = true;
                return (null, null);
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            using var cancellationRegistration = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            });
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { }
                try { await Task.WhenAll(outputTask, errorTask); } catch { }
                token.ThrowIfCancellationRequested();
                _gpuQueried = true;
                return (null, null);
            }
            var line = (await outputTask).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            await errorTask;
            _gpuQueried = true;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(line)) return (null, null);
            var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
            _cachedGpuName = parts[0];
            _cachedDriverVersion = parts.Length > 1 ? parts[1] : null;
            return (_cachedGpuName, _cachedDriverVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _gpuQueried = true;
            return (null, null);
        }
    }

    private static async Task DownloadCudaPackageAsync(
        CudaPackage package, string path, long completedBytes, long totalBytes,
        IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        var partial = path + ".part";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AstraCat/1.0");
            using var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256, true);
            var buffer = new byte[1024 * 256];
            long downloaded = 0;
            var started = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            while (true)
            {
                var read = await input.ReadAsync(buffer, token);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                downloaded += read;
                if (started.Elapsed - lastReport < TimeSpan.FromMilliseconds(120) && downloaded < package.Bytes) continue;
                lastReport = started.Elapsed;
                var overall = completedBytes + downloaded;
                var speed = downloaded / Math.Max(started.Elapsed.TotalSeconds, .001);
                var remaining = speed > 1 ? TimeSpan.FromSeconds((package.Bytes - downloaded) / speed) : (TimeSpan?)null;
                progress?.Report(new DeploymentProgress(
                    "正在从 NVIDIA 官方源下载 CUDA 运行库…",
                    Math.Clamp((double)overall / totalBytes, 0, 1), overall, totalBytes, speed, remaining));
            }
            await output.FlushAsync(token);
            output.Close();
            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void ExtractCudaArchive(string archivePath, string target, string licenseName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.EndsWith("/", StringComparison.Ordinal)) continue;
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                entry.ExtractToFile(Path.Combine(target, "bin", Path.GetFileName(entry.Name)), overwrite: true);
            }
            else if (normalized.EndsWith("/LICENSE", StringComparison.OrdinalIgnoreCase) ||
                     normalized.EndsWith("/LICENSE.txt", StringComparison.OrdinalIgnoreCase))
            {
                entry.ExtractToFile(Path.Combine(target, licenseName), overwrite: true);
            }
        }
    }

    private static async Task<bool> HasHashAsync(string path, string expected, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public async Task UninstallAsync(string id, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            await UninstallCoreAsync(id, progress, token);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task UninstallCoreAsync(string id, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        if (id == "python-runtime")
            throw new InvalidOperationException("Python 基础环境受保护，只能通过 AstraCat 安装程序统一修复或移除。");

        if (id.EndsWith("-runtime", StringComparison.OrdinalIgnoreCase))
        {
            if (id is "nvidia-runtime" or "funasr-runtime" or "nemo-runtime" or "moss-runtime")
            {
                var environment = RuntimePath(id);
                if (Directory.Exists(environment)) Directory.Delete(environment, recursive: true);
                InvalidateInspectCache();
                return;
            }
            if (!File.Exists(PythonExecutable)) return;
            progress?.Report(new DeploymentProgress("正在移除共享运行环境…"));
            var packages = id switch
            {
                "whisper-runtime" => "faster-whisper ctranslate2",
                "nvidia-runtime" => "transformers accelerate librosa soundfile sentencepiece",
                _ => "qwen-asr torch torchvision torchaudio"
            };
            await RunPythonAsync($"-m pip uninstall -y {packages} --disable-pip-version-check", progress, token, "正在卸载…");
            InvalidateInspectCache();
            return;
        }

        var states = Inspect();
        if (!states.TryGetValue(id, out var state) || !IsModelPath(state.Path))
            throw new ArgumentOutOfRangeException(nameof(id), id, "未知或不安全的模型目录");

        progress?.Report(new DeploymentProgress("正在删除模型权重…"));
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(state.Path)) Directory.Delete(state.Path, recursive: true);
        }, token);
        InvalidateInspectCache();
    }

    private async Task EnsurePipAsync(IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        var pipPackage = Path.Combine(PythonRoot, "Lib", "site-packages", "pip");
        if (Directory.Exists(pipPackage)) return;

        progress?.Report(new DeploymentProgress("正在初始化安装环境…"));
        await RunPythonAsync("-m ensurepip --upgrade", progress, token);
    }

    private async Task InstallPythonRuntimeAsync(
        IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        if (IsPython312(PythonExecutable))
        {
            InvalidateInspectCache();
            progress?.Report(new DeploymentProgress("Python 3.12 基础环境已就绪", 1));
            return;
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("自动安装 Python 当前仅支持 Windows x64。");

        // Always use the runtime stored under this AstraCat installation. This keeps
        // package versions and uninstall behavior independent from every other app.
        var sourcePython = await EnsurePrivatePythonAsync(progress, token);

        await CreateBaseVirtualEnvironmentAsync(sourcePython, progress, token);
        if (!IsPython312(PythonExecutable))
            throw new InvalidOperationException("Python 3.12 环境创建完成，但启动验证失败。");

        InvalidateInspectCache();
        progress?.Report(new DeploymentProgress("Python 3.12 基础环境安装完成", 1));
    }

    private async Task<string> EnsurePrivatePythonAsync(
        IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        if (IsPython312(PythonBaseExecutable)) return PythonBaseExecutable;

        var installerDirectory = Path.Combine(RuntimeRoot, "cache", "installers");
        Directory.CreateDirectory(installerDirectory);
        var packagePath = Path.Combine(installerDirectory, $"python-{PythonVersion}-amd64.nupkg");
        if (!await HasExpectedHashAsync(packagePath, token))
        {
            try { if (File.Exists(packagePath)) File.Delete(packagePath); } catch { }
            await DownloadPythonPackageAsync(packagePath, progress, token);
        }

        if (!await HasExpectedHashAsync(packagePath, token))
        {
            try { File.Delete(packagePath); } catch { }
            throw new InvalidOperationException("Python 运行包 SHA-256 校验失败，请重试下载。");
        }

        progress?.Report(new DeploymentProgress("正在解压 Python 3.12 私有运行时…"));
        var staging = Path.Combine(RuntimeRoot, $"python-base.installing-{Guid.NewGuid():N}");
        try
        {
            await Task.Run(() => ExtractPythonPackage(packagePath, staging, token), token);
            var stagedPython = Path.Combine(staging, "python.exe");
            if (!IsPython312(stagedPython))
                throw new InvalidOperationException("Python 3.12 私有运行时解压后验证失败。");

            if (Directory.Exists(PythonBaseRoot))
                Directory.Delete(PythonBaseRoot, recursive: true);
            Directory.Move(staging, PythonBaseRoot);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }

        if (!IsPython312(PythonBaseExecutable))
            throw new InvalidOperationException("Python 3.12 私有运行时安装后验证失败。");
        return PythonBaseExecutable;
    }

    private static void ExtractPythonPackage(
        string packagePath, string targetRoot, CancellationToken token)
    {
        Directory.CreateDirectory(targetRoot);
        var normalizedRoot = Path.GetFullPath(targetRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith("tools/", StringComparison.Ordinal) ||
                entry.FullName.Length == "tools/".Length)
                continue;

            var relativePath = entry.FullName["tools/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
            if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Python 运行包包含无效路径。");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private async Task DownloadPythonPackageAsync(
        string packagePath, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        var partialPath = packagePath + ".part";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AstraCat/1.0");
            using var response = await client.GetAsync(
                PythonPackageUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            progress?.Report(new DeploymentProgress(
                "正在下载 Python 3.12 私有运行包…", TotalBytes: total));

            long downloaded = 0;
            double? speed = null;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(
                             partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                var sampledAt = DateTime.UtcNow;
                var lastReportedAt = sampledAt;
                long sampledBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, token);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    downloaded += read;
                    var now = DateTime.UtcNow;
                    var seconds = (now - sampledAt).TotalSeconds;
                    if (seconds >= .35)
                    {
                        var instant = (downloaded - sampledBytes) / Math.Max(seconds, .001);
                        speed = speed.HasValue ? speed.Value * .7 + instant * .3 : instant;
                        sampledAt = now;
                        sampledBytes = downloaded;
                    }
                    if ((now - lastReportedAt).TotalMilliseconds < 100 && downloaded != total) continue;
                    lastReportedAt = now;
                    ReportPythonDownloadProgress(progress, downloaded, total, speed);
                }
                await output.FlushAsync(token);
            }
            ReportPythonDownloadProgress(progress, downloaded, total, speed);
            // The partial stream must be disposed before Windows can rename it.
            File.Move(partialPath, packagePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
            throw;
        }
    }

    private static void ReportPythonDownloadProgress(
        IProgress<DeploymentProgress>? progress, long downloaded, long? total, double? speed)
    {
        var fraction = total is > 0 ? Math.Clamp((double)downloaded / total.Value, 0, 1) : (double?)null;
        var remaining = total is > 0 && speed is > 1
            ? TimeSpan.FromSeconds(Math.Max(0, (total.Value - downloaded) / speed.Value))
            : (TimeSpan?)null;
        progress?.Report(new DeploymentProgress(
            "正在从清华镜像下载 Python 3.12…", fraction, downloaded, total, speed, remaining));
    }

    private async Task CreateBaseVirtualEnvironmentAsync(
        string sourcePython, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        var staging = Path.Combine(RuntimeRoot, $"python.installing-{Guid.NewGuid():N}");
        var backup = Path.Combine(RuntimeRoot, $"python.replaced-{Guid.NewGuid():N}");
        try
        {
            progress?.Report(new DeploymentProgress("正在创建 AstraCat Python 隔离环境…"));
            var info = new ProcessStartInfo
            {
                FileName = sourcePython,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-m");
            info.ArgumentList.Add("venv");
            info.ArgumentList.Add(staging);
            await RunProcessAsync(info, token, "无法创建 Python 隔离环境。");

            var stagedPython = Path.Combine(staging, "Scripts", "python.exe");
            if (!IsPython312(stagedPython))
                throw new InvalidOperationException("新建的 Python 隔离环境无法启动。");

            if (Directory.Exists(PythonRoot)) Directory.Move(PythonRoot, backup);
            try
            {
                Directory.Move(staging, PythonRoot);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(PythonRoot))
                    Directory.Move(backup, PythonRoot);
                throw;
            }
            try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); } catch { }
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static async Task RunProcessAsync(
        ProcessStartInfo info, CancellationToken token, string failureMessage)
    {
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException(failureMessage);
        var outputTask = info.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync(token) : Task.FromResult(string.Empty);
        var errorTask = info.RedirectStandardError ? process.StandardError.ReadToEndAsync(token) : Task.FromResult(string.Empty);
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? string.IsNullOrWhiteSpace(output) ? failureMessage : output.Trim() : error.Trim());
    }

    private static bool IsPython312(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("import sys;print(f'{sys.version_info.major}.{sys.version_info.minor}')");
            using var process = Process.Start(info);
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0 && output.Trim() == "3.12";
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(PythonPackageSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureIsolatedRuntimeAsync(
        string id, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        var executable = GetRuntimePythonExecutable(id);
        var target = RuntimePath(id);
        if (!File.Exists(executable))
        {
            Directory.CreateDirectory(EnvironmentRoot);
            progress?.Report(new DeploymentProgress("正在创建隔离运行环境…"));
            await RunPythonAsync($"-m venv --system-site-packages \"{target}\"", progress, token);
        }

        // Python's nested venv only inherits the underlying system interpreter's
        // packages, not the parent AstraCat venv. Add the parent site-packages
        // explicitly so large shared packages such as Torch are not duplicated.
        var isolatedSitePackages = Path.Combine(target, "Lib", "site-packages");
        Directory.CreateDirectory(isolatedSitePackages);
        await File.WriteAllTextAsync(
            Path.Combine(isolatedSitePackages, "_astracat_base.pth"),
            Path.Combine(PythonRoot, "Lib", "site-packages") + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), token);
    }

    private async Task RunPythonAsync(
        string arguments,
        IProgress<DeploymentProgress>? progress,
        CancellationToken token,
        string runningMessage = "正在下载…",
        string? progressDirectory = null,
        long? expectedBytes = null,
        string? pythonExecutable = null)
    {

        var startInfo = new ProcessStartInfo(pythonExecutable ?? PythonExecutable, arguments)
        {
            WorkingDirectory = AppRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureCudaEnvironment(startInfo);
        startInfo.Environment["HF_HOME"] = Path.Combine(ModelRoot, "huggingface");
        startInfo.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        startInfo.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "0";
        startInfo.Environment["HF_HUB_DISABLE_XET"] = "1";
        startInfo.Environment["HF_HUB_ETAG_TIMEOUT"] = "12";
        startInfo.Environment["HF_HUB_DOWNLOAD_TIMEOUT"] = "45";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        using var process = new Process { StartInfo = startInfo };
        var lastLine = string.Empty;
        var resolvedExpectedBytes = expectedBytes;
        void CaptureOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lastLine = line;
            var totalMatch = Regex.Match(line, @"ASTRACAT_TOTAL:(\d+)");
            if (totalMatch.Success && long.TryParse(totalMatch.Groups[1].Value, out var reportedTotal) && reportedTotal > 0)
            {
                resolvedExpectedBytes = reportedTotal;
                progress?.Report(new DeploymentProgress(runningMessage, TotalBytes: reportedTotal));
                return;
            }
            if (!TryParseProgress(line, out var fraction)) return;
            // Snapshot downloads contain several nested tqdm bars whose
            // individual percentages repeatedly jump back to zero. For model
            // weights, the cache byte count below is the stable total instead.
            if (expectedBytes is null)
                progress?.Report(new DeploymentProgress(runningMessage, fraction));
        }
        process.OutputDataReceived += (_, e) =>
        {
            CaptureOutput(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            CaptureOutput(e.Data);
        };

        if (!process.Start()) throw new InvalidOperationException("无法启动部署进程。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        progress?.Report(new DeploymentProgress(runningMessage));

        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var monitorTask = MonitorDirectoryProgressAsync(
            progressDirectory, () => resolvedExpectedBytes, runningMessage, progress,
            monitorCancellation.Token);
        using var cancellationRegistration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have completed between the checks.
            }
        });

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Pause/cancel kills the entire Python tree. Wait for it to be
            // fully gone before a resumed attempt starts against the same
            // Hugging Face or pip cache.
            try
            {
                if (!process.HasExited)
                    await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original cancellation result.
            }
            throw;
        }
        finally
        {
            monitorCancellation.Cancel();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastLine) ? "部署失败，请检查网络连接。" : lastLine);
        progress?.Report(new DeploymentProgress(
            "正在完成部署…", 1, resolvedExpectedBytes, resolvedExpectedBytes, null, TimeSpan.Zero));
    }

    private static async Task MonitorDirectoryProgressAsync(
        string? directory,
        Func<long?> expectedBytesProvider,
        string message,
        IProgress<DeploymentProgress>? progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        long? previousBytes = null;
        var previousSampleAt = DateTime.UtcNow;
        double? smoothedBytesPerSecond = null;
        while (!token.IsCancellationRequested)
        {
            // Directory enumeration is comparatively expensive for multi-GB model
            // snapshots. A 750 ms sample still feels live without saturating disk metadata I/O.
            await Task.Delay(750, token);
            long downloadedBytes;
            try
            {
                downloadedBytes = CalculateModelDownloadBytes(directory);
            }
            catch
            {
                continue;
            }

            var expectedBytes = expectedBytesProvider();
            if (expectedBytes is null or <= 0) continue;
            var fileFraction = Math.Clamp((double)downloadedBytes / expectedBytes.Value, 0, 0.985);
            var sampledAt = DateTime.UtcNow;
            var sampleSeconds = Math.Max((sampledAt - previousSampleAt).TotalSeconds, 0.001);
            if (previousBytes.HasValue && downloadedBytes >= previousBytes.Value)
            {
                var instantSpeed = (downloadedBytes - previousBytes.Value) / sampleSeconds;
                if (instantSpeed > 0)
                    smoothedBytesPerSecond = smoothedBytesPerSecond.HasValue
                        ? smoothedBytesPerSecond.Value * 0.72 + instantSpeed * 0.28
                        : instantSpeed;
            }
            previousBytes = downloadedBytes;
            previousSampleAt = sampledAt;

            TimeSpan? remaining = null;
            if (smoothedBytesPerSecond is > 1)
            {
                var remainingSeconds = Math.Max(0,
                    (expectedBytes.Value - downloadedBytes) / smoothedBytesPerSecond.Value);
                remaining = TimeSpan.FromSeconds(Math.Min(remainingSeconds, TimeSpan.FromDays(30).TotalSeconds));
            }
            if (fileFraction > 0)
                progress?.Report(new DeploymentProgress(
                    message,
                    fileFraction,
                    Math.Min(downloadedBytes, expectedBytes.Value),
                    expectedBytes.Value,
                    smoothedBytesPerSecond,
                    remaining));
        }
    }

    private static bool TryParseProgress(string line, out double fraction)
    {
        var percentMatch = Regex.Match(line, @"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%");
        if (percentMatch.Success &&
            double.TryParse(percentMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var percent))
        {
            fraction = Math.Clamp(percent / 100d, 0, 1);
            return true;
        }

        var rawMatch = Regex.Match(line, @"Progress\s+(\d+)\s+of\s+(\d+)", RegexOptions.IgnoreCase);
        if (rawMatch.Success &&
            long.TryParse(rawMatch.Groups[1].Value, out var current) &&
            long.TryParse(rawMatch.Groups[2].Value, out var total) && total > 0)
        {
            fraction = Math.Clamp((double)current / total, 0, 1);
            return true;
        }

        fraction = 0;
        return false;
    }

    private static long CalculateModelDownloadBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            var firstPart = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            // Ignore the legacy cache layout created by older AstraCat builds.
            // local_dir uses .cache only for active partial files and metadata,
            // so those bytes intentionally remain part of live progress.
            if (firstPart.StartsWith("models--", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(firstPart, ".locks", StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".metadata", StringComparison.OrdinalIgnoreCase))
                continue;
            try { total += new FileInfo(file).Length; }
            catch { }
        }
        return total;
    }

    private string? ModelProgressDirectory(string id) => id switch
    {
        "whisper-tiny" => Path.Combine(ModelRoot, "whisper-tiny"),
        "whisper-base" => Path.Combine(ModelRoot, "whisper-base"),
        "whisper-small" => Path.Combine(ModelRoot, "whisper-small"),
        "whisper-medium" => Path.Combine(ModelRoot, "whisper-medium"),
        "whisper-large-v3" => Path.Combine(ModelRoot, "whisper-large-v3"),
        "whisper-v3-turbo" => Path.Combine(ModelRoot, "whisper-large-v3-turbo"),
        "qwen-0.6b" => Path.Combine(ModelRoot, "qwen3-asr-0.6b"),
        "qwen-1.7b" => Path.Combine(ModelRoot, "qwen3-asr-1.7b"),
        "funasr-nano" => Path.Combine(ModelRoot, "fun-asr-nano-2512"),
        "sensevoice-small" => Path.Combine(ModelRoot, "sensevoice-small"),
        "nvidia-parakeet-v3" => Path.Combine(ModelRoot, "nvidia-parakeet-tdt-0.6b-v3"),
        "nvidia-canary-v2" => Path.Combine(ModelRoot, "nvidia-canary-1b-v2"),
        "moss-0.9b" => Path.Combine(ModelRoot, "moss-transcribe-diarize-0.9b"),
        _ => null
    };

    private static long? ExpectedDownloadBytes(string id) => id switch
    {
        "whisper-tiny" => 78_000_000L,
        "whisper-base" => 148_000_000L,
        "whisper-small" => 464_000_000L,
        "whisper-medium" => 1_535_000_000L,
        "whisper-large-v3" => 3_100_000_000L,
        "whisper-v3-turbo" => 1_625_000_000L,
        "qwen-0.6b" => 1_800_000_000L,
        "qwen-1.7b" => 4_700_000_000L,
        "funasr-nano" => 1_990_000_000L,
        "sensevoice-small" => 944_000_000L,
        "nvidia-parakeet-v3" => 2_550_000_000L,
        "nvidia-canary-v2" => 6_365_000_000L,
        "moss-0.9b" => 1_830_000_000L,
        _ => null
    };

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private DeploymentState ModelState(string folderName, string pattern)
    {
        var folder = Path.Combine(ModelRoot, folderName);
        var installed = Directory.Exists(folder) && (folderName.StartsWith("qwen3-asr-", StringComparison.OrdinalIgnoreCase)
            ? HasCompleteQwenSnapshot(folder)
            : Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
                .Any(file => new FileInfo(file).Length > 1024 * 1024));
        return State(folderName switch
        {
            "whisper-large-v3-turbo" => "whisper-v3-turbo",
            "qwen3-asr-0.6b" => "qwen-0.6b",
            "qwen3-asr-1.7b" => "qwen-1.7b",
            "fun-asr-nano-2512" => "funasr-nano",
            "sensevoice-small" => "sensevoice-small",
            "nvidia-parakeet-tdt-0.6b-v3" => "nvidia-parakeet-v3",
            "nvidia-canary-1b-v2" => "nvidia-canary-v2",
            "moss-transcribe-diarize-0.9b" => "moss-0.9b",
            _ => folderName
        }, installed, folder);
    }

    private bool HasCompleteQwenSnapshot(string folder)
    {
        static bool HasWeights(string directory) =>
            File.Exists(Path.Combine(directory, "config.json")) &&
            Directory.EnumerateFiles(directory, "*.safetensors", SearchOption.TopDirectoryOnly)
                .Any(file => new FileInfo(file).Length > 1024 * 1024);
        static bool HasProcessor(string directory) =>
            new[] { "preprocessor_config.json", "tokenizer_config.json", "vocab.json", "merges.txt" }
                .All(file => File.Exists(Path.Combine(directory, file)));

        var localCandidates = new[] { folder, Path.Combine(folder, ".resolved") }
            .Concat(Directory.EnumerateDirectories(folder, "snapshots", SearchOption.AllDirectories)
                .SelectMany(directory => Directory.EnumerateDirectories(directory)))
            .Where(Directory.Exists)
            .ToArray();
        if (localCandidates.Any(directory => HasWeights(directory) && HasProcessor(directory))) return true;

        var repoFolder = Path.GetFileName(folder).EndsWith("0.6b", StringComparison.OrdinalIgnoreCase)
            ? "models--Qwen--Qwen3-ASR-0.6B"
            : "models--Qwen--Qwen3-ASR-1.7B";
        var sharedSnapshots = Path.Combine(ModelRoot, "huggingface", "hub", repoFolder, "snapshots");
        var hasSharedProcessor = Directory.Exists(sharedSnapshots) &&
                                 Directory.EnumerateDirectories(sharedSnapshots).Any(HasProcessor);

        return hasSharedProcessor && localCandidates.Any(HasWeights);
    }

    private static DeploymentState State(string id, bool installed, string path) => new(id, installed, path);

    private string RuntimeSitePackages(string id) =>
        Path.Combine(RuntimePath(id), "Lib", "site-packages");

    private bool RuntimeCanResolvePackage(string runtimeSitePackages, string package)
    {
        var baseSitePackages = Path.Combine(PythonRoot, "Lib", "site-packages");
        return Directory.Exists(Path.Combine(runtimeSitePackages, package)) ||
               File.Exists(Path.Combine(runtimeSitePackages, package + ".py")) ||
               Directory.Exists(Path.Combine(baseSitePackages, package)) ||
               File.Exists(Path.Combine(baseSitePackages, package + ".py"));
    }

    private string RuntimePath(string id) => Path.Combine(EnvironmentRoot, RuntimeFolderName(id));

    private static string RuntimeFolderName(string id) => id switch
    {
        "nvidia-runtime" => "n",
        "funasr-runtime" => "f",
        "nemo-runtime" => "c",
        "moss-runtime" => "m",
        _ => id
    };

    private static bool HasPythonPackageVersion(
        string sitePackages, string distribution, Version minimumVersion)
    {
        if (!Directory.Exists(sitePackages)) return false;
        foreach (var path in Directory.EnumerateDirectories(
                     sitePackages, $"{distribution}-*.dist-info", SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(path);
            var versionText = folderName[distribution.Length..^".dist-info".Length].TrimStart('-');
            var stableVersion = versionText.Split('-', '+')[0];
            if (Version.TryParse(stableVersion, out var installedVersion) && installedVersion >= minimumVersion)
                return true;
        }
        return false;
    }

    private bool IsModelPath(string path)
    {
        var root = Path.GetFullPath(ModelRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(target.TrimEnd(Path.DirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string SnapshotArguments(
        string repository,
        string cacheDirectory,
        ModelDownloadSource source,
        IReadOnlyList<string>? allowPatterns = null)
    {
        var repo = repository.Replace("\\", "\\\\").Replace("'", "\\'");
        var target = cacheDirectory.Replace("\\", "\\\\").Replace("'", "\\'");
        var patterns = allowPatterns is null
            ? "[]"
            : "[" + string.Join(", ", allowPatterns.Select(pattern =>
                $"'{pattern.Replace("\\", "\\\\").Replace("'", "\\'")}'")) + "]";
        var endpoints = source switch
        {
            ModelDownloadSource.HfMirror => new[] { "https://hf-mirror.com" },
            ModelDownloadSource.HuggingFace => new[] { "https://huggingface.co" },
            // hf-mirror currently omits metadata headers required by recent
            // huggingface_hub local_dir downloads. Keep it as a fallback, but
            // never let it block the compatible official source in Auto mode.
            _ => new[] { "https://huggingface.co", "https://hf-mirror.com" }
        };
        var endpointTuple = string.Join(", ", endpoints.Select(endpoint => $"'{endpoint}'")) + ",";
        var script =
            "from huggingface_hub import snapshot_download\n" +
            "from huggingface_hub import HfApi\n" +
            "from pathlib import Path\n" +
            "from urllib.parse import quote\n" +
            "from urllib.request import Request, urlopen\n" +
            "from urllib.error import HTTPError\n" +
            "import fnmatch, os, time\n" +
            "errors = []\n" +
            $"repo = '{repo}'\ntarget = '{target}'\nallow_patterns = {patterns}\n" +
            "def mirror_download(endpoint, info):\n" +
            "    root = Path(target)\n" +
            "    root.mkdir(parents=True, exist_ok=True)\n" +
            "    for item in info.siblings:\n" +
            "        name = item.rfilename\n" +
            "        if allow_patterns and not any(fnmatch.fnmatch(name, pattern) for pattern in allow_patterns):\n" +
            "            continue\n" +
            "        expected = item.size or 0\n" +
            "        destination = root / Path(name)\n" +
            "        destination.parent.mkdir(parents=True, exist_ok=True)\n" +
            "        if destination.is_file() and (not expected or destination.stat().st_size == expected):\n" +
            "            continue\n" +
            "        partial = Path(str(destination) + '.incomplete')\n" +
            "        for attempt in range(3):\n" +
            "            offset = partial.stat().st_size if partial.exists() else 0\n" +
            "            headers = {'User-Agent': 'AstraCat/1.0'}\n" +
            "            if offset:\n" +
            "                headers['Range'] = f'bytes={offset}-'\n" +
            "            url = f\"{endpoint}/{repo}/resolve/main/{quote(name, safe='/')}?download=true\"\n" +
            "            try:\n" +
            "                response = urlopen(Request(url, headers=headers), timeout=45)\n" +
            "                append = offset > 0 and getattr(response, 'status', 200) == 206\n" +
            "                with partial.open('ab' if append else 'wb') as output:\n" +
            "                    while True:\n" +
            "                        chunk = response.read(1024 * 1024)\n" +
            "                        if not chunk:\n" +
            "                            break\n" +
            "                        output.write(chunk)\n" +
            "                if expected and partial.stat().st_size != expected:\n" +
            "                    raise IOError(f'{name} 大小不完整：{partial.stat().st_size}/{expected}')\n" +
            "                os.replace(partial, destination)\n" +
            "                break\n" +
            "            except HTTPError as exc:\n" +
            "                if exc.code == 416 and expected and partial.exists() and partial.stat().st_size == expected:\n" +
            "                    os.replace(partial, destination)\n" +
            "                    break\n" +
            "                if attempt == 2:\n" +
            "                    raise\n" +
            "                time.sleep(1 + attempt)\n" +
            "            except Exception:\n" +
            "                if attempt == 2:\n" +
            "                    raise\n" +
            "                time.sleep(1 + attempt)\n" +
            $"for endpoint in ({endpointTuple}):\n" +
            "    try:\n" +
            "        info = HfApi(endpoint=endpoint).model_info(repo, files_metadata=True)\n" +
            "        total = sum((item.size or 0) for item in info.siblings if not allow_patterns or any(fnmatch.fnmatch(item.rfilename, pattern) for pattern in allow_patterns))\n" +
            "        if total:\n" +
            "            print(f'ASTRACAT_TOTAL:{total}', flush=True)\n" +
            "        if endpoint == 'https://hf-mirror.com':\n" +
            "            mirror_download(endpoint, info)\n" +
            "        else:\n" +
            "            snapshot_download(repo_id=repo, local_dir=target, endpoint=endpoint, etag_timeout=12, max_workers=2, allow_patterns=allow_patterns or None)\n" +
            "        break\n" +
            "    except Exception as exc:\n" +
            "        errors.append(f'{endpoint}: {exc}')\n" +
            "else:\n" +
            "    raise RuntimeError('所有下载源均不可用：' + ' | '.join(errors))\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"-c \"import base64;exec(base64.b64decode('{encoded}'))\"";
    }

    private static string PipArguments(string package, ModelDownloadSource source) =>
        source == ModelDownloadSource.HuggingFace
            ? $"-m pip install {package} --index-url https://pypi.org/simple --progress-bar raw --disable-pip-version-check"
            : $"-m pip install {package} --index-url https://pypi.tuna.tsinghua.edu.cn/simple " +
              "--extra-index-url https://pypi.org/simple --progress-bar raw --disable-pip-version-check";

    private static string PipArgumentsWithoutDependencies(string package, ModelDownloadSource source) =>
        PipArguments(package, source) + " --no-deps";

    private static string FindAppRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "engines", "asr_worker.py")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }
}
