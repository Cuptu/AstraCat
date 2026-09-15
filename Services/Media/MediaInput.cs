namespace AstraCat;

internal static class MediaInput
{
    public static bool Exists(string value) => File.Exists(value) || TryGetRemoteUri(value, out _);

    public static string Extension(string value)
    {
        if (TryGetRemoteUri(value, out var uri)) return Path.GetExtension(uri.AbsolutePath);
        return Path.GetExtension(value);
    }

    public static string CacheIdentity(string value)
    {
        if (TryGetRemoteUri(value, out var uri)) return $"remote|{uri.AbsoluteUri}";
        var info = new FileInfo(value);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static bool TryGetRemoteUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }
}
