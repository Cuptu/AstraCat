using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AstraCat;

/// <summary>Lightweight native preview window: embeds mpv for video+subtitle playback with a seek bar.</summary>
public partial class SubtitlePreviewWindow : Window
{
    private readonly string _mediaPath;
    private readonly string? _subtitlePath;
    private readonly MpvPlayerService _player = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _seeking;
    private bool _closed;
    private bool _allowClose;
    private bool _teardownStarted;

    // Required by Avalonia's XAML loader.
    public SubtitlePreviewWindow()
        : this(string.Empty, null)
    {
    }

    public SubtitlePreviewWindow(string mediaPath, string? subtitlePath)
    {
        InitializeComponent();
        _mediaPath = mediaPath;
        _subtitlePath = subtitlePath;
        if (!string.IsNullOrWhiteSpace(mediaPath)) Title = $"字幕预览 - {Path.GetFileName(mediaPath)}";

        _player.PositionChanged += (_, position) => Post(() =>
        {
            if (!_seeking) SeekSlider.Value = Math.Min(position, SeekSlider.Maximum);
            TimeText.Text = $"{FormatTime(position)} / {FormatTime(_player.DurationSeconds)}";
        });
        _player.DurationChanged += (_, duration) => Post(() =>
        {
            SeekSlider.Maximum = Math.Max(1, duration);
            TimeText.Text = $"{FormatTime(_player.PositionSeconds)} / {FormatTime(duration)}";
        });
        _player.PauseChanged += (_, paused) => Post(() => PlayPauseGlyph.Text = paused ? "▶" : "⏸");
        _player.PlaybackError += (_, message) => Post(() => LoadingText.Text = message);
        Opened += VideoPreview_OnOpened;
        Closing += OnClosing;
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private async void VideoPreview_OnOpened(object? sender, EventArgs e)
    {
        if (_closed || string.IsNullOrWhiteSpace(_mediaPath)) return;
        try
        {
            await _player.StartAsync(VideoHost, _mediaPath, _subtitlePath, _lifetime.Token);
            if (_closed) return;
            await _player.SetPauseAsync(false, _lifetime.Token);
            LoadingText.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"预览加载失败：{ex.Message}";
        }
    }

    private async void PlayPause_OnClick(object? sender, RoutedEventArgs e)
    {
        try { await _player.TogglePauseAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void SeekSlider_OnPointerPressed(object? sender, PointerPressedEventArgs e) => _seeking = true;

    private async void SeekSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _seeking = false;
        try { await _player.SeekAsync(SeekSlider.Value, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _ = CloseAfterTeardownAsync();
    }

    private async Task CloseAfterTeardownAsync()
    {
        if (_teardownStarted) return;
        _teardownStarted = true;
        _closed = true;
        _lifetime.Cancel();
        try
        {
            await _player.DisposeAsync();
        }
        catch
        {
            // Keep the preview closable if its native surface was already destroyed.
        }
        finally
        {
            _lifetime.Dispose();
        }

        _allowClose = true;
        Close();
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "--:--";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
