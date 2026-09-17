using System.Runtime.InteropServices;

namespace AstraCat;

internal static class MediaToolLocator
{
    private static readonly object PathLookupSync = new();
    private static readonly Dictionary<string, string?> PathLookupCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindLibMpv()
    {
        var astraCore = AstraCoreRuntime.Current?.Resolve("libmpv");
        if (astraCore is not null) return astraCore;
        var name = OperatingSystem.IsWindows() ? "libmpv-2.dll" : OperatingSystem.IsMacOS() ? "libmpv.2.dylib" : "libmpv.so.2";
        var rid = OperatingSystem.IsWindows()
            ? (Environment.Is64BitProcess ? "win-x64" : "win-x86")
            : OperatingSystem.IsMacOS()
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64");

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "mpv", name),
            Path.Combine(Environment.CurrentDirectory, "runtime", "tools", "mpv", name),
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(Environment.CurrentDirectory, "runtimes", rid, "native", name),
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", name)
        };

        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/opt/homebrew/lib/" + name);
            candidates.Add("/usr/local/lib/" + name);
            candidates.Add("/opt/homebrew/lib/libmpv.dylib");
            candidates.Add("/usr/local/lib/libmpv.dylib");
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates.Add("/usr/lib/x86_64-linux-gnu/" + name);
            candidates.Add("/usr/lib/aarch64-linux-gnu/" + name);
            candidates.Add("/usr/lib/" + name);
            candidates.Add("/usr/local/lib/" + name);
            candidates.Add("/usr/lib/x86_64-linux-gnu/libmpv.so");
            candidates.Add("/usr/lib/aarch64-linux-gnu/libmpv.so");
            candidates.Add("/usr/lib/libmpv.so");
            candidates.Add("/usr/local/lib/libmpv.so");
        }

        var found = FindFile(candidates.ToArray());
        if (found is not null) return found;

        if (NativeLibrary.TryLoad(name, out var handle))
        {
            NativeLibrary.Free(handle);
            return name;
        }

        if (OperatingSystem.IsMacOS() && NativeLibrary.TryLoad("libmpv.dylib", out var macHandle))
        {
            NativeLibrary.Free(macHandle);
            return "libmpv.dylib";
        }

        if (OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libmpv.so", out var linuxHandle))
        {
            NativeLibrary.Free(linuxHandle);
            return "libmpv.so";
        }

        return null;
    }

    public static string? FindFfmpeg() => AstraCoreRuntime.Current?.Resolve("ffmpeg") ?? Find(
        "ffmpeg",
        Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "ffmpeg", Executable("ffmpeg")),
        Path.Combine(Environment.CurrentDirectory, "runtime", "tools", "ffmpeg", Executable("ffmpeg")));

    public static string? FindFfprobe() => AstraCoreRuntime.Current?.Resolve("ffprobe") ?? Find(
        "ffprobe",
        Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "ffmpeg", Executable("ffprobe")),
        Path.Combine(Environment.CurrentDirectory, "runtime", "tools", "ffmpeg", Executable("ffprobe")));

    public static string? FindDownloadPython()
    {
        var pyName = Executable("python");
        var appRoot = FindAppRoot();
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "python-embed", pyName),
            Path.Combine(appRoot, "runtime", "python-embed", pyName),
            Path.Combine(Environment.CurrentDirectory, "runtime", "python-embed", pyName),
            Path.Combine(AppContext.BaseDirectory, "runtime", "python", "Scripts", pyName),
            Path.Combine(appRoot, "runtime", "python", "Scripts", pyName),
            Path.Combine(Environment.CurrentDirectory, "runtime", "python", "Scripts", pyName),
            Path.Combine(AppContext.BaseDirectory, "runtime", "python", pyName),
            Path.Combine(appRoot, "runtime", "python", pyName),
            Path.Combine(Environment.CurrentDirectory, "runtime", "python", pyName),
            Path.Combine(AppContext.BaseDirectory, "runtime", "python", "bin", "python3"),
            Path.Combine(appRoot, "runtime", "python", "bin", "python3"),
            Path.Combine(Environment.CurrentDirectory, "runtime", "python", "bin", "python3"),
        };

        var pythonLocation = Environment.GetEnvironmentVariable("pythonLocation");
        if (!string.IsNullOrWhiteSpace(pythonLocation))
        {
            var trimmedLocation = pythonLocation.Trim().Trim('"');
            candidates.Add(Path.Combine(trimmedLocation, pyName));
            candidates.Add(Path.Combine(trimmedLocation, "bin", "python3"));
            candidates.Add(Path.Combine(trimmedLocation, "Scripts", pyName));
        }

        return FindFile(candidates.ToArray())
            ?? FindFromSystemPath("python")
            ?? FindFromSystemPath("python3");
    }

    public static string? FindDownloadWorker()
    {
        var appRoot = FindAppRoot();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "engines", "download_worker.py"),
            Path.Combine(appRoot, "engines", "download_worker.py"),
            Path.Combine(Environment.CurrentDirectory, "engines", "download_worker.py"),
        };
        return FindFile(candidates);
    }

    private static string FindAppRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "engines", "download_worker.py")) ||
                    File.Exists(Path.Combine(directory.FullName, "engines", "asr_worker.py")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }

    private static string Executable(string name) => OperatingSystem.IsWindows() ? $"{name}.exe" : name;

    private static string? FindFile(params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        return null;
    }

    public static string? FindFromSystemPath(string command)
    {
        lock (PathLookupSync)
        {
            if (PathLookupCache.TryGetValue(command, out var cached)) return cached;
            var executable = Executable(command);
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), executable);
                    if (!File.Exists(candidate)) continue;
                    cached = Path.GetFullPath(candidate);
                    PathLookupCache[command] = cached;
                    return cached;
                }
                catch
                {
                    // Skip malformed or inaccessible PATH entries.
                }
            }

            PathLookupCache[command] = null;
            return null;
        }
    }

    private static string? Find(string command, params string[] candidates)
    {
        var found = FindFile(candidates);
        if (found is not null) return found;

        if (!string.Equals(Environment.GetEnvironmentVariable("ASTRACAT_ALLOW_SYSTEM_MEDIA_TOOLS"), "1", StringComparison.Ordinal))
            return null;

        return FindFromSystemPath(command);
    }
}
