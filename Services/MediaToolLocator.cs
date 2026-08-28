namespace AstraCat;

internal static class MediaToolLocator
{
    private static readonly object PathLookupSync = new();
    private static readonly Dictionary<string, string?> PathLookupCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindLibMpv()
    {
        var name = OperatingSystem.IsWindows() ? "libmpv-2.dll" : OperatingSystem.IsMacOS() ? "libmpv.2.dylib" : "libmpv.so.2";
        return FindFile(
            Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "mpv", name),
            Path.Combine(Environment.CurrentDirectory, "runtime", "tools", "mpv", name),
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(Environment.CurrentDirectory, "runtimes", "win-x64", "native", name));
    }

    public static string? FindFfmpeg() => Find(
        "ffmpeg",
        Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "ffmpeg", Executable("ffmpeg")),
        Path.Combine(Environment.CurrentDirectory, "runtime", "tools", "ffmpeg", Executable("ffmpeg")));

    public static string? FindFfprobe() => Find(
        "ffprobe",
        Path.Combine(AppContext.BaseDirectory, "runtime", "tools", "ffmpeg", Executable("ffprobe")),
        Path.Combine(Environment.CurrentDirectory, "runtime", "tools", "ffmpeg", Executable("ffprobe")));

    private static string Executable(string name) => OperatingSystem.IsWindows() ? $"{name}.exe" : name;

    private static string? FindFile(params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        return null;
    }

    private static string? Find(string command, params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);

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
}
