using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AstraCat;

public sealed record DownloadMediaResult(string MediaPath, string? SubtitlePath, string Title);

public partial class DownloadMediaWindow : Window
{
    private CancellationTokenSource? _activeCts;
    private DownloadInspectResult? _lastInspectResult;
    private string? _capturedCookies;

    public DownloadMediaWindow()
    {
        InitializeComponent();
    }

    private async void WebAssistButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var initialUrl = UrlInput.Text?.Trim();
            var webDialog = new WebDownloadWindow(initialUrl);
            var result = await webDialog.ShowDialog<WebDownloadCaptureResult?>(this);
            if (result != null && !string.IsNullOrWhiteSpace(result.Url))
            {
                UrlInput.Text = result.Url;
                _capturedCookies = result.Cookies;
                InspectButton_OnClick(InspectButton, e);
            }
        }
        catch (Exception ex)
        {
            ErrorSummaryText.Text = $"打开网页助手失败: {ex.Message}";
        }
    }

    private async void PasteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is not null)
            {
                var transfer = await topLevel.Clipboard.TryGetDataAsync();
                if (transfer is not null)
                {
                    var text = await transfer.TryGetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        UrlInput.Text = text.Trim();
                    }
                }
            }
        }
        catch { }
    }

    private async void InspectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ErrorSummaryText.Text = "请输入视频或音频链接。";
            return;
        }

        ErrorSummaryText.Text = string.Empty;
        InspectButton.IsEnabled = false;
        InspectButton.Content = "解析中...";

        try
        {
            var proxy = string.IsNullOrWhiteSpace(ProxyInput.Text) ? null : ProxyInput.Text.Trim();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // 纯 C# 原生媒体下载服务
            var result = await PureMediaDownloadService.Instance.InspectAsync(url, proxy, rawCookies: _capturedCookies, token: cts.Token);

            _lastInspectResult = result;
            MediaTitleText.Text = result.Title ?? "未命名媒体";
            var duration = TimeSpan.FromSeconds(result.Duration);
            MediaDurationText.Text = $"时长: {(int)duration.TotalMinutes:D2}:{duration.Seconds:D2}";
            MediaUploaderText.Text = string.IsNullOrWhiteSpace(result.Uploader) ? "" : $"作者: {result.Uploader}";
            MediaSubtitlesBadge.IsVisible = result.HasSubtitles;
            InspectCard.IsVisible = true;
        }
        catch (Exception ex)
        {
            ErrorSummaryText.Text = $"解析失败: {ex.Message}";
        }
        finally
        {
            InspectButton.IsEnabled = true;
            InspectButton.Content = "解析";
        }
    }

    private async void StartDownloadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = UrlInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ErrorSummaryText.Text = "请输入视频或音频链接。";
            return;
        }

        ErrorSummaryText.Text = string.Empty;
        ProgressArea.IsVisible = true;
        DownloadBar.Value = 0;
        StatusText.Text = "正在连接并初始化下载...";
        SpeedText.Text = "-- MB/s";

        StartDownloadButton.IsEnabled = false;
        InspectButton.IsEnabled = false;

        _activeCts = new CancellationTokenSource();
        var token = _activeCts.Token;

        var downloadDir = Path.Combine(AppContext.BaseDirectory, "runtime", "cache", "downloads");
        Directory.CreateDirectory(downloadDir);

        var audioOnly = ModeAudioRadio.IsChecked == true;
        var writeSubs = DownloadSubsCheck.IsChecked == true;
        var proxy = string.IsNullOrWhiteSpace(ProxyInput.Text) ? null : ProxyInput.Text.Trim();

        var request = new DownloadRequest(
            Url: url,
            OutputDir: downloadDir,
            AudioOnly: audioOnly,
            WriteSubtitles: writeSubs,
            Proxy: proxy,
            RawCookies: _capturedCookies);

        var progress = new Progress<DownloadProgressEvent>(ev =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                DownloadBar.Value = ev.Percent;
                var speedStr = ev.Speed.HasValue
                    ? $"{ev.Speed.Value / 1024 / 1024:0.0} MB/s"
                    : "-- MB/s";
                var etaStr = ev.Eta.HasValue
                    ? $"剩余 {TimeSpan.FromSeconds(ev.Eta.Value):mm\\:ss}"
                    : "";
                SpeedText.Text = string.IsNullOrWhiteSpace(etaStr) ? speedStr : $"{speedStr} ({etaStr})";
                StatusText.Text = $"正在下载 ({ev.Percent:0.0}%)...";
            });
        });

        try
        {
            // 纯 C# 原生媒体下载服务
            var res = await PureMediaDownloadService.Instance.DownloadAsync(request, progress, token: token);

            if (string.IsNullOrWhiteSpace(res.MediaPath) || !File.Exists(res.MediaPath))
            {
                throw new FileNotFoundException("未找到下载生成的媒体文件。");
            }

            var title = !string.IsNullOrWhiteSpace(res.Title)
                ? res.Title
                : (_lastInspectResult?.Title ?? Path.GetFileNameWithoutExtension(res.MediaPath));

            Close(new DownloadMediaResult(res.MediaPath, res.SubtitlePath, title));
        }
        catch (OperationCanceledException)
        {
            ErrorSummaryText.Text = "用户已取消下载。";
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            ErrorSummaryText.Text = $"下载失败: {ex.Message}";
            StatusText.Text = "下载出错";
        }
        finally
        {
            StartDownloadButton.IsEnabled = true;
            InspectButton.IsEnabled = true;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeCts is not null && !_activeCts.IsCancellationRequested)
        {
            _activeCts.Cancel();
        }
        else
        {
            Close(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        base.OnClosed(e);
    }
}
