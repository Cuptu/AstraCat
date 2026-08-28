using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AstraCat;

public sealed record ModelCatalogItem(
    string DeploymentId,
    string RepositoryId,
    string Category,
    long Downloads,
    int Likes,
    DateTimeOffset LastModified,
    int CategoryRank,
    bool FromCache);

public sealed record ModelCatalogResult(
    IReadOnlyDictionary<string, ModelCatalogItem> Models,
    DateTimeOffset RefreshedAt,
    bool UsedCache,
    int OnlineCount);

/// <summary>
/// Refreshes popularity and maintenance metadata for the deliberately small
/// compatibility allow-list. Hub popularity is never treated as proof that an
/// arbitrary repository can be deployed by AstraCat.
/// </summary>
public sealed class ModelCatalogService : IDisposable
{
    private sealed record CatalogEntry(string DeploymentId, string RepositoryId, string Category);

    private sealed record CachedCatalog(DateTimeOffset RefreshedAt, List<ModelCatalogItem> Models);

    private static readonly CatalogEntry[] Entries =
    [
        new("whisper-tiny", "Systran/faster-whisper-tiny", "whisper"),
        new("whisper-base", "Systran/faster-whisper-base", "whisper"),
        new("whisper-small", "Systran/faster-whisper-small", "whisper"),
        new("whisper-medium", "Systran/faster-whisper-medium", "whisper"),
        new("whisper-large-v3", "Systran/faster-whisper-large-v3", "whisper"),
        new("whisper-v3-turbo", "openai/whisper-large-v3-turbo", "whisper"),
        new("qwen-0.6b", "Qwen/Qwen3-ASR-0.6B", "qwen"),
        new("qwen-1.7b", "Qwen/Qwen3-ASR-1.7B", "qwen"),
        new("funasr-nano", "FunAudioLLM/Fun-ASR-Nano-2512", "funasr"),
        new("sensevoice-small", "FunAudioLLM/SenseVoiceSmall", "funasr"),
        new("nvidia-parakeet-v3", "nvidia/parakeet-tdt-0.6b-v3", "nvidia"),
        new("nvidia-canary-v2", "nvidia/canary-1b-v2", "nvidia"),
        new("moss-0.9b", "OpenMOSS-Team/MOSS-Transcribe-Diarize", "moss")
    ];

    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly object _refreshLock = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private Task<ModelCatalogResult>? _inflightRefresh;
    private ModelCatalogResult? _memoryCache;
    private DateTimeOffset _memoryCacheExpiresAt;
    private bool _disposed;
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(7);

    public ModelCatalogService(string cacheDirectory)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://huggingface.co/api/"),
            Timeout = TimeSpan.FromSeconds(12)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AstraCat", "1.0"));
        _cachePath = Path.Combine(cacheDirectory, "model-catalog.json");
    }

    public Task<ModelCatalogResult> RefreshAsync(CancellationToken token, bool forceRefresh = false)
    {
        Task<ModelCatalogResult> refresh;
        TaskCompletionSource<ModelCatalogResult>? starter = null;
        lock (_refreshLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!forceRefresh && _memoryCache is not null && DateTimeOffset.UtcNow < _memoryCacheExpiresAt)
                return Task.FromResult(_memoryCache).WaitAsync(token);

            if (_inflightRefresh is null)
            {
                starter = new TaskCompletionSource<ModelCatalogResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inflightRefresh = starter.Task;
            }

            refresh = _inflightRefresh;
        }

        // Start outside the lock. A caller cancellation only stops that caller's
        // wait; the shared request remains available to other views.
        if (starter is not null) _ = RunRefreshAsync(starter);
        return refresh.WaitAsync(token);
    }

    public void InvalidateMemoryCache()
    {
        lock (_refreshLock)
        {
            _memoryCache = null;
            _memoryCacheExpiresAt = default;
        }
    }

    private async Task RunRefreshAsync(TaskCompletionSource<ModelCatalogResult> completion)
    {
        try
        {
            var result = await RefreshCoreAsync(_disposeCancellation.Token);
            lock (_refreshLock)
            {
                // A total outage with no usable disk fallback must remain
                // immediately retryable instead of hiding recovery for ten minutes.
                if (result.OnlineCount > 0 || result.Models.Count > 0)
                {
                    _memoryCache = result;
                    _memoryCacheExpiresAt = DateTimeOffset.UtcNow + MemoryCacheTtl;
                }
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_refreshLock)
            {
                if (ReferenceEquals(_inflightRefresh, completion.Task))
                    _inflightRefresh = null;
            }
        }
    }

    private async Task<ModelCatalogResult> RefreshCoreAsync(CancellationToken token)
    {
        var cached = await ReadCacheAsync(token);
        var requests = Entries.Select(entry => ReadOnlineAsync(entry, token)).ToArray();
        var fetched = await Task.WhenAll(requests);
        token.ThrowIfCancellationRequested();

        var online = fetched.Where(item => item is not null).Cast<ModelCatalogItem>().ToList();
        var merged = online.ToDictionary(item => item.DeploymentId, StringComparer.OrdinalIgnoreCase);
        if (cached is not null)
        {
            foreach (var cachedItem in cached.Models)
            {
                if (!merged.ContainsKey(cachedItem.DeploymentId))
                    merged[cachedItem.DeploymentId] = cachedItem with { FromCache = true };
            }
        }

        RankWithinCategories(merged);
        var refreshedAt = online.Count > 0 ? DateTimeOffset.Now : cached?.RefreshedAt ?? DateTimeOffset.Now;
        if (online.Count > 0)
            await WriteCacheAsync(new CachedCatalog(refreshedAt, merged.Values.ToList()), token);

        return new ModelCatalogResult(
            merged,
            refreshedAt,
            UsedCache: online.Count < Entries.Length,
            OnlineCount: online.Count);
    }

    private async Task<ModelCatalogItem?> ReadOnlineAsync(CatalogEntry entry, CancellationToken token)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"models/{Uri.EscapeDataString(entry.RepositoryId).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}",
                HttpCompletionOption.ResponseHeadersRead,
                token);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            var returnedId = ReadString(root, "id");
            if (!string.Equals(returnedId, entry.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
                ReadBoolean(root, "private") || ReadGated(root))
                return null;

            var downloads = ReadInt64(root, "downloads");
            var lastModified = DateTimeOffset.TryParse(ReadString(root, "lastModified"), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            // Low-volume entries must be actively maintained. Mature models may
            // remain stable without frequent commits when download adoption is high.
            var activelyMaintained = lastModified >= DateTimeOffset.UtcNow.AddYears(-3);
            if (downloads < 1_000 || (!activelyMaintained && downloads < 100_000)) return null;

            return new ModelCatalogItem(
                entry.DeploymentId,
                entry.RepositoryId,
                entry.Category,
                downloads,
                (int)Math.Min(int.MaxValue, ReadInt64(root, "likes")),
                lastModified,
                CategoryRank: 0,
                FromCache: false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<CachedCatalog?> ReadCacheAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            await using var stream = File.OpenRead(_cachePath);
            var cached = await JsonSerializer.DeserializeAsync<CachedCatalog>(stream, cancellationToken: token);
            if (cached is null || cached.Models is null ||
                cached.RefreshedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
                DateTimeOffset.UtcNow - cached.RefreshedAt > DiskCacheTtl)
                return null;
            return cached;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(CachedCatalog cache, CancellationToken token)
    {
        var temporaryPath = _cachePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, cache, cancellationToken: token);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (IOException)
        {
            // The live list remains usable even when the optional cache cannot be persisted.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only installations simply skip the offline cache.
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static void RankWithinCategories(Dictionary<string, ModelCatalogItem> models)
    {
        foreach (var category in models.Values.GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase))
        {
            var rank = 1;
            foreach (var item in category.OrderByDescending(item => item.Downloads).ThenByDescending(item => item.LastModified))
                models[item.DeploymentId] = item with { CategoryRank = rank++ };
        }
    }

    private static string ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static bool ReadBoolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool ReadGated(JsonElement root)
    {
        if (!root.TryGetProperty("gated", out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => !string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
        _httpClient.Dispose();
    }
}
