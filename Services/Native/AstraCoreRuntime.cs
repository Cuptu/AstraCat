using System.Runtime.InteropServices;
using System.Text.Json;

namespace AstraCat;

internal sealed record AstraCoreComponents(
    string LibMpv,
    string Ffmpeg,
    string Ffprobe,
    string? Native = null);

internal sealed record AstraCoreManifest(
    int SchemaVersion,
    string RuntimeIdentifier,
    AstraCoreComponents Components);

internal sealed class AstraCoreRuntime
{
    private const string ManifestName = "astracore-runtime.json";
    private static readonly Lazy<AstraCoreRuntime?> CurrentRuntime = new(FindCore);

    private AstraCoreRuntime(string root, AstraCoreManifest manifest)
    {
        Root = root;
        Manifest = manifest;
    }

    public string Root { get; }
    public AstraCoreManifest Manifest { get; }
    public static AstraCoreRuntime? Current => CurrentRuntime.Value;

    public string? Resolve(string component)
    {
        var relative = component switch
        {
            "libmpv" => Manifest.Components.LibMpv,
            "ffmpeg" => Manifest.Components.Ffmpeg,
            "ffprobe" => Manifest.Components.Ffprobe,
            "native" => Manifest.Components.Native,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(relative)) return null;

        var candidate = Path.GetFullPath(Path.Combine(Root, relative));
        var prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison) || !File.Exists(candidate)) return null;
        return candidate;
    }

    private static AstraCoreRuntime? FindCore()
    {
        foreach (var root in CandidateRoots())
        {
            try
            {
                var fullRoot = Path.GetFullPath(root);
                var manifestPath = Path.Combine(fullRoot, ManifestName);
                if (!File.Exists(manifestPath)) continue;
                using var stream = File.OpenRead(manifestPath);
                var manifest = AotJson.Deserialize<AstraCoreManifest>(stream, JsonOptions);
                if (manifest is null || manifest.SchemaVersion != 1 || manifest.Components is null) continue;
                if (!RuntimeMatches(manifest.RuntimeIdentifier)) continue;
                return new AstraCoreRuntime(fullRoot, manifest);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("ASTRACAT_MEDIA_RUNTIME");
        if (!string.IsNullOrWhiteSpace(explicitRoot)) yield return explicitRoot;

        var rids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CurrentRid(), OsArchitectureRid() };
        foreach (var baseDirectory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            foreach (var rid in rids)
            {
                yield return Path.Combine(baseDirectory, "runtime", "tools", "astracore", rid);
                yield return Path.Combine(baseDirectory, "artifacts", "astracore", rid);
            }
            yield return Path.Combine(baseDirectory, "runtime", "tools", "astracore");
        }
    }

    private static bool RuntimeMatches(string manifestRid)
    {
        if (string.Equals(manifestRid, CurrentRid(), StringComparison.OrdinalIgnoreCase)) return true;
        // Framework-dependent development runs can report a versioned RID. The
        // OS/architecture pair is the compatibility boundary for AstraCore.
        return string.Equals(manifestRid, OsArchitectureRid(), StringComparison.OrdinalIgnoreCase);
    }

    internal static string CurrentRid()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        return string.IsNullOrWhiteSpace(rid) ? OsArchitectureRid() : rid;
    }

    private static string OsArchitectureRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64"
        };
        return $"{os}-{architecture}";
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
