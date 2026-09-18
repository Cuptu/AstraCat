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
using AstraCat.Services.Speech;

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
/// Manages speech recognition models and runtime acceleration packages.
/// Uses the real model weight files and integrity markers as the source of truth.
/// </summary>
public sealed class DeploymentManager
{
    private sealed record CudaPackage(string Url, string Sha256, long Bytes, string LicenseName);
    private sealed record CudaRelease(string Version, CudaPackage Cublas, CudaPackage Cudart);

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
    public string ModelRoot => Path.Combine(RuntimeRoot, "models");
    public string GpuRuntimeRoot => Path.Combine(RuntimeRoot, "gpu");

    private readonly object _inspectLock = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private IReadOnlyDictionary<string, DeploymentState>? _cachedStates;
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

            var states = new Dictionary<string, DeploymentState>(StringComparer.OrdinalIgnoreCase)
            {
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
        Directory.CreateDirectory(ModelRoot);
        progress?.Report(new DeploymentProgress("正在准备下载…"));

        if (SherpaModelRegistry.TryGetProfile(id, out var profile) && profile != null)
        {
            var archiveName = profile.ArchiveName
                ?? throw new InvalidOperationException($"模型 {id} 未配置下载包名称。");

            var downloadUrl = profile.GitHubReleaseUrl;
            if (source == ModelDownloadSource.HfMirror && !string.IsNullOrWhiteSpace(profile.ArchiveName))
            {
                downloadUrl = $"https://ghfast.top/{profile.GitHubReleaseUrl}";
            }

            var cacheDir = Path.Combine(RuntimeRoot, "cache", "models");
            Directory.CreateDirectory(cacheDir);
            var archivePath = Path.Combine(cacheDir, archiveName);

            progress?.Report(new DeploymentProgress($"正在下载 {profile.DisplayName}…", 0.05));
            await DownloadFileWithProgressAsync(downloadUrl ?? profile.GitHubReleaseUrl!, archivePath, progress, token);

            progress?.Report(new DeploymentProgress($"正在解压 {profile.DisplayName} 模型文件…", 0.9));
            var targetDir = Path.Combine(ModelRoot, id);
            Directory.CreateDirectory(targetDir);

            if (archiveName.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ||
                archiveName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarArchiveAsync(archivePath, ModelRoot, token);
            }
            else if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            }

            if (SherpaModelLocator.IsModelInstalled(profile, out var resolved) && resolved != null)
            {
                var marker = Path.Combine(resolved.ModelDirectory, ".astracat_complete");
                if (!File.Exists(marker))
                {
                    await File.WriteAllTextAsync(marker, $"Installed on {DateTime.UtcNow:O}", token);
                }
            }

            InvalidateInspectCache();
            progress?.Report(new DeploymentProgress($"{profile.DisplayName} 部署完成", 1.0));
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "未知或不受支持的模型组件");
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
        var ready = gpuName is not null && filesReady;
        var summary = gpuName is null
            ? "未检测到 NVIDIA 显卡，将使用 CPU"
            : !filesReady
                ? "检测到 NVIDIA 显卡，尚未安装 AstraCat CUDA 运行库"
                : $"GPU 加速已就绪 · CUDA {version}";
        return new CudaRuntimeStatus(gpuName is not null, gpuName, driverVersion, version, ready, filesReady, summary);
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
        File.WriteAllText(temporary, AotJson.Serialize(new Dictionary<string, object?> { ["version"] = version }));
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
            string[] candidates = OperatingSystem.IsWindows()
                ? [
                    "nvidia-smi.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                    @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
                  ]
                : [
                    "nvidia-smi",
                    "/usr/bin/nvidia-smi",
                    "/usr/local/bin/nvidia-smi"
                  ];
            var executable = candidates.FirstOrDefault(File.Exists) ?? (OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi");

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

    private static async Task DownloadFileWithProgressAsync(
        string url, string path, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
        if (File.Exists(path))
        {
            progress?.Report(new DeploymentProgress("文件已存在，跳过下载", 1.0));
            return;
        }

        var partial = path + ".part";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AstraCat/1.0");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
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

                if (started.Elapsed - lastReport < TimeSpan.FromMilliseconds(150) && totalBytes.HasValue && downloaded < totalBytes.Value)
                    continue;

                lastReport = started.Elapsed;
                var speed = downloaded / Math.Max(started.Elapsed.TotalSeconds, 0.001);
                var fraction = totalBytes is > 0 ? Math.Clamp((double)downloaded / totalBytes.Value, 0, 1) : (double?)null;
                var remaining = totalBytes is > 0 && speed > 1
                    ? TimeSpan.FromSeconds((totalBytes.Value - downloaded) / speed)
                    : (TimeSpan?)null;

                progress?.Report(new DeploymentProgress(
                    "正在下载模型文件…",
                    fraction,
                    downloaded,
                    totalBytes,
                    speed,
                    remaining));
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

    private static async Task ExtractTarArchiveAsync(string archivePath, string destinationDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(destinationDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "tar.exe" : "tar",
            Arguments = $"-xf \"{archivePath}\" -C \"{destinationDirectory}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动系统解压工具 (tar)。");

        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"解压模型归档失败：{error}");
        }
    }

    private async Task UninstallCoreAsync(string id, IProgress<DeploymentProgress>? progress, CancellationToken token)
    {
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

    public static void OpenFolder(string path) => PlatformHelper.OpenFolder(path);

    private DeploymentState ModelState(string folderName, string pattern)
    {
        var id = folderName switch
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
        };

        var folder = Path.Combine(ModelRoot, folderName);
        var completeMarker = Path.Combine(folder, ".astracat_complete");
        var hasMarker = File.Exists(completeMarker);

        bool installed;
        if (!Directory.Exists(folder))
        {
            installed = false;
        }
        else if (folderName.StartsWith("qwen3-asr-", StringComparison.OrdinalIgnoreCase))
        {
            installed = HasCompleteQwenSnapshot(folder);
        }
        else
        {
            var hasMatchingFile = Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
                .Any(file => new FileInfo(file).Length > 1024 * 1024);

            if (!hasMatchingFile)
            {
                installed = false;
            }
            else if (hasMarker)
            {
                installed = true;
            }
            else if (ExpectedDownloadBytes(id) is { } expected && expected > 0)
            {
                // Verify folder total size is at least 85% of expected size to reject truncated/corrupt downloads
                var actualBytes = CalculateModelDownloadBytes(folder);
                installed = actualBytes >= (long)(expected * 0.85);
            }
            else
            {
                installed = true;
            }
        }

        if (!installed && SherpaModelRegistry.TryGetProfile(id, out var sherpaProfile) && sherpaProfile is not null)
        {
            if (SherpaModelLocator.IsModelInstalled(sherpaProfile, out var resolvedPaths) && resolvedPaths is not null)
            {
                installed = true;
                folder = resolvedPaths.ModelDirectory;
            }
        }

        return State(id, installed, folder);
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


    private bool IsModelPath(string path)
    {
        var root = Path.GetFullPath(ModelRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(target.TrimEnd(Path.DirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    internal static string FindAppRoot()
    {
        if (NativeAotSmoke.ApplicationRoot is { } smokeRoot) return smokeRoot;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AstraCat.csproj")) ||
                    Directory.Exists(Path.Combine(directory.FullName, "runtime")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }
}
