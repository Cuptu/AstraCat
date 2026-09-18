namespace AstraCat.Services.Speech;

public sealed record ResolvedModelPaths
{
    public required string ModelDirectory { get; init; }
    public string? EncoderPath { get; init; }
    public string? DecoderPath { get; init; }
    public string? ConvFrontendPath { get; init; }
    public string? ModelPath { get; init; }
    public string? TokensPath { get; init; }
    public string? TokenizerPath { get; init; }
}

public static class SherpaModelLocator
{
    public static string GetModelRoot()
    {
        var appRoot = FindAppRoot();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "models"),
            Path.Combine(Environment.CurrentDirectory, "runtime", "models"),
            Path.Combine(appRoot, "runtime", "models"),
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir)) return Path.GetFullPath(dir);
        }

        return Path.Combine(appRoot, "runtime", "models");
    }

    public static string? FindSileroVadModel()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runtime", "models", "vad", "silero_vad.onnx"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "silero_vad.onnx"),
            Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx"),
            Path.Combine(Environment.CurrentDirectory, "runtime", "models", "vad", "silero_vad.onnx"),
            Path.Combine(Environment.CurrentDirectory, "runtime", "silero_vad.onnx"),
            Path.Combine(Environment.CurrentDirectory, "silero_vad.onnx"),
        };

        // Also check app root
        var appRoot = FindAppRoot();
        candidates.Add(Path.Combine(appRoot, "runtime", "models", "vad", "silero_vad.onnx"));
        candidates.Add(Path.Combine(appRoot, "runtime", "silero_vad.onnx"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var vadDir = Path.Combine(appRoot, "runtime", "models", "vad");
        if (Directory.Exists(vadDir))
        {
            var vadFiles = Directory.GetFiles(vadDir, "*vad*.onnx");
            if (vadFiles.Length > 0) return Path.GetFullPath(vadFiles[0]);
        }

        return null;
    }

    public static bool IsModelInstalled(SherpaModelProfile profile, out ResolvedModelPaths? paths)
    {
        paths = ResolveModelPaths(profile);
        return paths is not null;
    }

    public static ResolvedModelPaths? ResolveModelPaths(SherpaModelProfile profile, string? customDir = null)
    {
        var searchDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(customDir) && Directory.Exists(customDir))
        {
            searchDirs.Add(customDir);
        }

        var modelRoot = GetModelRoot();
        searchDirs.Add(Path.Combine(modelRoot, profile.Id));

        if (!string.IsNullOrWhiteSpace(profile.ArchiveName))
        {
            var folderFromArchive = profile.ArchiveName.Replace(".tar.bz2", "", StringComparison.OrdinalIgnoreCase);
            searchDirs.Add(Path.Combine(modelRoot, folderFromArchive));
        }

        var appRoot = FindAppRoot();
        searchDirs.Add(Path.Combine(appRoot, "runtime", "models", profile.Id));

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            if (TryResolveInDirectory(profile, dir, out var resolved))
                return resolved;
        }

        return null;
    }

    private static bool TryResolveInDirectory(SherpaModelProfile profile, string dir, out ResolvedModelPaths? resolved)
    {
        resolved = null;
        switch (profile.Family)
        {
            case AsrModelFamily.Whisper:
            {
                var encoder = FindFileInDir(dir, profile.EncoderFile, "*-encoder*.onnx", "*encoder*.onnx");
                var decoder = FindFileInDir(dir, profile.DecoderFile, "*-decoder*.onnx", "*decoder*.onnx");
                var tokens = FindFileInDir(dir, profile.TokensFile, "*-tokens.txt", "*tokens*.txt");

                if (encoder is not null && decoder is not null && tokens is not null)
                {
                    resolved = new ResolvedModelPaths
                    {
                        ModelDirectory = dir,
                        EncoderPath = encoder,
                        DecoderPath = decoder,
                        TokensPath = tokens
                    };
                    return true;
                }
                break;
            }

            case AsrModelFamily.Qwen3Asr:
            {
                var conv = FindFileInDir(dir, profile.ConvFrontendFile, "*conv_frontend*.onnx");
                var encoder = FindFileInDir(dir, profile.EncoderFile, "*encoder*.onnx");
                var decoder = FindFileInDir(dir, profile.DecoderFile, "*decoder*.onnx");
                var tokenizer = FindDirInDir(dir, profile.TokenizerDir, "tokenizer");

                if (conv is not null && encoder is not null && decoder is not null && tokenizer is not null)
                {
                    resolved = new ResolvedModelPaths
                    {
                        ModelDirectory = dir,
                        ConvFrontendPath = conv,
                        EncoderPath = encoder,
                        DecoderPath = decoder,
                        TokenizerPath = tokenizer
                    };
                    return true;
                }
                break;
            }

            case AsrModelFamily.NeMoCtc:
            {
                var model = FindFileInDir(dir, profile.ModelFile, "*model*.onnx", "*.onnx");
                var tokens = FindFileInDir(dir, profile.TokensFile, "*tokens*.txt", "tokens.txt");

                if (model is not null && tokens is not null)
                {
                    resolved = new ResolvedModelPaths
                    {
                        ModelDirectory = dir,
                        ModelPath = model,
                        TokensPath = tokens
                    };
                    return true;
                }
                break;
            }
        }

        return false;
    }

    private static string? FindFileInDir(string dir, string? specificName, params string[] searchPatterns)
    {
        if (!string.IsNullOrWhiteSpace(specificName))
        {
            var p = Path.Combine(dir, specificName);
            if (File.Exists(p)) return Path.GetFullPath(p);
        }

        foreach (var pattern in searchPatterns)
        {
            var matches = Directory.GetFiles(dir, pattern);
            if (matches.Length > 0)
                return Path.GetFullPath(matches[0]);
        }

        return null;
    }

    private static string? FindDirInDir(string dir, string? specificName, params string[] searchPatterns)
    {
        if (!string.IsNullOrWhiteSpace(specificName))
        {
            var p = Path.Combine(dir, specificName);
            if (Directory.Exists(p)) return Path.GetFullPath(p);
        }

        foreach (var pattern in searchPatterns)
        {
            var matches = Directory.GetDirectories(dir, pattern);
            if (matches.Length > 0)
                return Path.GetFullPath(matches[0]);
        }

        return null;
    }

    private static string FindAppRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "runtime", "models")) ||
                    File.Exists(Path.Combine(directory.FullName, "AstraCat.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return AppContext.BaseDirectory;
    }
}
