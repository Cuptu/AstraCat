using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode.Videos.ClosedCaptions;

namespace AstraCat;

/// <summary>
/// 纯 C# 原生媒体下载服务。
/// 完全基于 .NET 10、YoutubeExplode 与系统 FFmpeg，零 Python 依赖，跨平台原生运行。
/// </summary>
public sealed class PureMediaDownloadService
{
    public static PureMediaDownloadService Instance { get; } = new();

    private static HttpClient CreateHttpClient(string? proxyUrl)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        return client;
    }

    private static YoutubeClient CreateYoutubeClient(string? proxyUrl, IReadOnlyList<Cookie>? cookies = null)
    {
        var cookieContainer = new CookieContainer();
        if (cookies != null)
        {
            foreach (var c in cookies)
            {
                cookieContainer.Add(c);
            }
        }

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = cookieContainer
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler, disposeHandler: true);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        return new YoutubeClient(client);
    }

    private static List<Cookie> ParseCookies(string? rawCookies, Uri domainUri)
    {
        var list = new List<Cookie>();
        if (string.IsNullOrWhiteSpace(rawCookies)) return list;

        var parts = rawCookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                var name = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                try
                {
                    list.Add(new Cookie(name, value, "/", domainUri.Host));
                }
                catch { }
            }
        }
        return list;
    }

    public async Task<DownloadInspectResult> InspectAsync(
        string url,
        string? proxy = null,
        IReadOnlyList<Cookie>? cookies = null,
        string? rawCookies = null,
        CancellationToken token = default)
    {
        url = url.Trim();
        if (cookies == null && !string.IsNullOrWhiteSpace(rawCookies) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            cookies = ParseCookies(rawCookies, uri);
        }

        if (IsYoutubeUrl(url))
        {
            var yt = CreateYoutubeClient(proxy, cookies);
            var video = await yt.Videos.GetAsync(url, token).ConfigureAwait(false);

            bool hasSubs = false;
            var subLangs = new List<string>();
            try
            {
                var captionManifest = await yt.Videos.ClosedCaptions.GetManifestAsync(url, token).ConfigureAwait(false);
                foreach (var track in captionManifest.Tracks)
                {
                    subLangs.Add(track.Language.Name);
                }
                hasSubs = subLangs.Count > 0;
            }
            catch { }

            return new DownloadInspectResult(
                Id: video.Id.Value,
                Title: video.Title,
                Duration: video.Duration?.TotalSeconds ?? 0,
                Thumbnail: video.Thumbnails.Count > 0 ? video.Thumbnails[^1].Url : null,
                Uploader: video.Author.ChannelTitle,
                WebpageUrl: video.Url,
                Subtitles: subLangs,
                AutomaticCaptions: null,
                HasSubtitles: hasSubs);
        }
        else
        {
            // 通用直接媒体链接嗅探 (Direct HTTP)
            using var http = CreateHttpClient(proxy);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var filename = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(filename)) filename = "online_media";

            return new DownloadInspectResult(
                Id: null,
                Title: filename,
                Duration: 0,
                Thumbnail: null,
                Uploader: "网络直接媒体",
                WebpageUrl: url,
                Subtitles: null,
                AutomaticCaptions: null,
                HasSubtitles: false);
        }
    }

    public async Task<DownloadCompletedResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgressEvent>? progress = null,
        IReadOnlyList<Cookie>? cookies = null,
        CancellationToken token = default)
    {
        var url = request.Url.Trim();
        var outDir = Path.GetFullPath(request.OutputDir);
        Directory.CreateDirectory(outDir);

        if (cookies == null && !string.IsNullOrWhiteSpace(request.RawCookies) && Uri.TryCreate(url, UriKind.Absolute, out var reqUri))
        {
            cookies = ParseCookies(request.RawCookies, reqUri);
        }

        if (IsYoutubeUrl(url))
        {
            return await DownloadYoutubeAsync(request, outDir, progress, cookies, token).ConfigureAwait(false);
        }
        else
        {
            return await DownloadDirectHttpAsync(request, outDir, progress, token).ConfigureAwait(false);
        }
    }

    private async Task<DownloadCompletedResult> DownloadYoutubeAsync(
        DownloadRequest request,
        string outDir,
        IProgress<DownloadProgressEvent>? progress,
        IReadOnlyList<Cookie>? cookies,
        CancellationToken token)
    {
        var yt = CreateYoutubeClient(request.Proxy, cookies);
        var video = await yt.Videos.GetAsync(request.Url, token).ConfigureAwait(false);

        var safeTitle = SanitizeFilename(video.Title);
        var streamManifest = await yt.Videos.Streams.GetManifestAsync(request.Url, token).ConfigureAwait(false);

        string targetMediaPath;
        var ffmpeg = MediaToolLocator.FindFfmpeg();

        var progWrapper = new Progress<double>(val =>
        {
            var pct = Math.Min(100.0, Math.Max(0.0, val * 100.0));
            progress?.Report(new DownloadProgressEvent(
                Percent: pct,
                DownloadedBytes: (long)(pct * 1024 * 1024),
                TotalBytes: 100 * 1024 * 1024,
                Speed: null,
                Eta: null,
                Status: "downloading"));
        });

        if (request.AudioOnly)
        {
            targetMediaPath = Path.Combine(outDir, $"{safeTitle}.mp3");
            if (File.Exists(targetMediaPath)) File.Delete(targetMediaPath);

            var audioStream = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate()
                ?? throw new InvalidOperationException("未找到可用的音频流。");

            var builder = new ConversionRequestBuilder(targetMediaPath)
                .SetContainer(Container.Mp3);
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                builder.SetFFmpegPath(ffmpeg);
            }

            await yt.Videos.DownloadAsync(new[] { audioStream }, builder.Build(), progWrapper, token).ConfigureAwait(false);
        }
        else
        {
            targetMediaPath = Path.Combine(outDir, $"{safeTitle}.mp4");
            if (File.Exists(targetMediaPath)) File.Delete(targetMediaPath);

            var bestAudio = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            var bestVideo = streamManifest.GetVideoOnlyStreams().GetWithHighestVideoQuality();

            var streamsToDownload = new List<IStreamInfo>();
            if (bestAudio != null) streamsToDownload.Add(bestAudio);
            if (bestVideo != null) streamsToDownload.Add(bestVideo);

            if (streamsToDownload.Count == 0)
            {
                var muxed = streamManifest.GetMuxedStreams().GetWithHighestVideoQuality()
                    ?? throw new InvalidOperationException("未找到可用的视频或音频流。");
                streamsToDownload.Add(muxed);
            }

            var builder = new ConversionRequestBuilder(targetMediaPath)
                .SetContainer(Container.Mp4);
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                builder.SetFFmpegPath(ffmpeg);
            }

            await yt.Videos.DownloadAsync(streamsToDownload, builder.Build(), progWrapper, token).ConfigureAwait(false);
        }

        // 下载字幕 (若开启)
        string? subPath = null;
        if (request.WriteSubtitles)
        {
            try
            {
                var captionManifest = await yt.Videos.ClosedCaptions.GetManifestAsync(request.Url, token).ConfigureAwait(false);
                if (captionManifest.Tracks.Count > 0)
                {
                    var track = captionManifest.GetByLanguage("zh")
                        ?? captionManifest.GetByLanguage("en")
                        ?? captionManifest.Tracks[0];

                    subPath = Path.Combine(outDir, $"{safeTitle}.srt");
                    await yt.Videos.ClosedCaptions.DownloadAsync(track, subPath, cancellationToken: token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        progress?.Report(new DownloadProgressEvent(100.0, 100, 100, null, 0, "finished"));

        return new DownloadCompletedResult(
            MediaPath: targetMediaPath,
            SubtitlePath: subPath,
            Title: video.Title,
            Duration: video.Duration?.TotalSeconds ?? 0);
    }

    private async Task<DownloadCompletedResult> DownloadDirectHttpAsync(
        DownloadRequest request,
        string outDir,
        IProgress<DownloadProgressEvent>? progress,
        CancellationToken token)
    {
        using var http = CreateHttpClient(request.Proxy);
        using var response = await http.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var filename = Path.GetFileName(new Uri(request.Url).LocalPath);
        if (string.IsNullOrWhiteSpace(filename)) filename = "downloaded_media.mp4";

        var safeName = SanitizeFilename(filename);
        var targetFile = Path.Combine(outDir, safeName);

        await using var contentStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                var pct = (double)totalRead / totalBytes * 100.0;
                progress?.Report(new DownloadProgressEvent(pct, totalRead, totalBytes, null, null, "downloading"));
            }
        }

        progress?.Report(new DownloadProgressEvent(100.0, totalRead, totalRead, null, 0, "finished"));

        return new DownloadCompletedResult(
            MediaPath: targetFile,
            SubtitlePath: null,
            Title: Path.GetFileNameWithoutExtension(targetFile),
            Duration: 0);
    }

    private static bool IsYoutubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        return new string(chars).Trim();
    }
}
