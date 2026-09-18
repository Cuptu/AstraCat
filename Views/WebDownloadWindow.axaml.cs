using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AstraCat;

public sealed record WebDownloadCaptureResult(string Url, string? Cookies);

public partial class WebDownloadWindow : Window
{
    private const string DefaultUrl = "https://www.youtube.com";

    public WebDownloadWindow() : this(null)
    {
    }

    public WebDownloadWindow(string? initialUrl = null)
    {
        InitializeComponent();

        var targetUrl = !string.IsNullOrWhiteSpace(initialUrl) ? initialUrl.Trim() : DefaultUrl;
        AddressInput.Text = targetUrl;
        NavigateTo(targetUrl);
    }

    private void NavigateTo(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AddressInput.Text = uri.ToString();
            LoadingOverlay.IsVisible = true;
            CurrentStatusText.Text = $"正在加载: {uri}";
            BrowserView.Source = uri;
        }
    }

    private void BrowserView_OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        LoadingOverlay.IsVisible = false;
        if (BrowserView.Source != null)
        {
            AddressInput.Text = BrowserView.Source.ToString();
        }

        if (e.IsSuccess)
        {
            CurrentStatusText.Text = "页面加载完成。浏览至视频页面后，点击右下角按钮直接解析下载。";
        }
        else
        {
            CurrentStatusText.Text = "页面加载遇到问题，请检查网络连接或代理设置。";
        }
    }

    private async void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await BrowserView.InvokeScript("history.back()");
        }
        catch { }
    }

    private async void ForwardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await BrowserView.InvokeScript("history.forward()");
        }
        catch { }
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await BrowserView.InvokeScript("location.reload()");
        }
        catch
        {
            if (BrowserView.Source != null)
            {
                NavigateTo(BrowserView.Source.ToString());
            }
        }
    }

    private void GoButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var text = AddressInput.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            NavigateTo(text);
        }
    }

    private void AddressInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoButton_OnClick(sender, e);
        }
    }

    private void QuickYouTube_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo("https://www.youtube.com");
    }

    private void QuickBili_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateTo("https://www.bilibili.com");
    }

    private async void CaptureVideo_OnClick(object? sender, RoutedEventArgs e)
    {
        var currentUrl = BrowserView.Source?.ToString() ?? AddressInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(currentUrl))
        {
            CurrentStatusText.Text = "当前未打开任何有效页面。";
            return;
        }

        string? cookies = null;
        try
        {
            // 通过双向 JS 执行提取当前会话已激活的 Cookies
            cookies = await BrowserView.InvokeScript("document.cookie");
        }
        catch { }

        Close(new WebDownloadCaptureResult(currentUrl, cookies));
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
