using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AstraCat;

public partial class MediaExportWindow : Window
{
    private readonly WorkspaceExportRequest _request;
    private readonly MediaExportService _service = new();
    private readonly MpvPlayerService _player = new();
    private readonly CancellationTokenSource _playerLifetime = new();
    private MediaProbeInfo _media = MediaProbeInfo.Unknown;
    private HardwareEncoderInfo? _hardware;
    private CancellationTokenSource? _exportCancellation;
    private bool _exporting;
    private bool _closeRequestedDuringExport;
    private bool _teardownStarted;
    private bool _allowClose;
    private bool _previewSeeking;
    private bool _playerInitialized;

    // Required by Avalonia's XAML loader. Runtime callers should use the request constructor.
    public MediaExportWindow()
        : this(new WorkspaceExportRequest(string.Empty, "未命名项目"))
    {
    }

    public MediaExportWindow(WorkspaceExportRequest request)
    {
        InitializeComponent();
        _request = request;
        ProjectTitleText.Text = request.ProjectTitle;
        OutputPathBox.Text = SuggestedOutput(request);
        ResolutionCombo.SelectedIndex = 0;
        FormatCombo.SelectedIndex = 0;
        FrameRateCombo.SelectedIndex = 0;
        QualityCombo.SelectedIndex = 0;
        BitRateCombo.SelectedIndex = 0;
        CodecCombo.SelectedIndex = 0;
        EncoderCombo.SelectedIndex = 0;
        AudioQualityCombo.SelectedIndex = 1;
        AudioSampleRateCombo.SelectedIndex = 0;
        var hasSubtitles = !string.IsNullOrWhiteSpace(request.SubtitlePath)
                           || !string.IsNullOrWhiteSpace(request.PlainSubtitlePath);
        SubtitlePanel.IsVisible = hasSubtitles;
        SubtitleFormatCombo.SelectedIndex = 0;

        _player.PositionChanged += (_, position) => Dispatcher.UIThread.Post(() =>
        {
            if (!_previewSeeking && PreviewSeekBar is not null)
                PreviewSeekBar.Position = position;
            if (PreviewTimeText is not null)
                PreviewTimeText.Text = $"{FormatTime(position)} / {FormatTime(_player.DurationSeconds)}";
        });
        _player.DurationChanged += (_, duration) => Dispatcher.UIThread.Post(() =>
        {
            if (PreviewSeekBar is not null)
                PreviewSeekBar.Duration = duration;
            if (PreviewTimeText is not null)
                PreviewTimeText.Text = $"{FormatTime(_player.PositionSeconds)} / {FormatTime(duration)}";
        });
        _player.PauseChanged += (_, paused) => Dispatcher.UIThread.Post(() =>
        {
            if (PreviewPlayPauseIcon is not null)
                PreviewPlayPauseIcon.Kind = paused ? Material.Icons.MaterialIconKind.Play : Material.Icons.MaterialIconKind.Pause;
        });
        _player.PlaybackError += (_, message) => Dispatcher.UIThread.Post(() =>
        {
            if (PreviewLoadingText is not null)
            {
                PreviewLoadingText.Text = $"预览加载异常：{message}";
                PreviewLoadingText.IsVisible = true;
            }
        });

        if (PreviewSeekBar is not null)
        {
            PreviewSeekBar.ScrubStarted += (_, _) => _previewSeeking = true;
            PreviewSeekBar.Scrubbing += async (_, time) =>
            {
                if (!_playerInitialized) return;
                try { await _player.SeekAsync(time, _playerLifetime.Token); } catch { }
            };
            PreviewSeekBar.SeekRequested += async (_, time) =>
            {
                if (!_playerInitialized) return;
                try { await _player.SeekAsync(time, _playerLifetime.Token); } catch { }
            };
            PreviewSeekBar.ScrubCompleted += (_, _) => _previewSeeking = false;
        }

        Opened += OnOpened;
        Closing += OnClosing;
        _ = InitializeEncoderChoicesAsync();
    }

    private async Task InitializeEncoderChoicesAsync()
    {
        try
        {
            _hardware = await MediaExportService.GetHardwareEncodersAsync();
        }
        catch
        {
            _hardware = HardwareEncoderInfo.None;
        }
        UpdateEncoderAvailability();
    }

    private void UpdateEncoderAvailability()
    {
        if (_hardware is null || EncoderStatusText is null) return;
        var codec = EnumTag(CodecCombo, ExportVideoCodec.H264);
        NvencItem.IsEnabled = _hardware.SupportsVendor(ExportEncoder.NvidiaNvenc, codec);
        QsvItem.IsEnabled = _hardware.SupportsVendor(ExportEncoder.IntelQsv, codec);
        AmfItem.IsEnabled = _hardware.SupportsVendor(ExportEncoder.AmdAmf, codec);
        if (EncoderCombo.SelectedItem is ComboBoxItem { IsEnabled: false }) EncoderCombo.SelectedIndex = 0;

        var codecName = codec switch
        {
            ExportVideoCodec.Hevc => "HEVC",
            ExportVideoCodec.Av1 => "AV1",
            _ => "H.264"
        };
        var names = new List<string>();
        if (_hardware.SupportsVendor(ExportEncoder.NvidiaNvenc, codec)) names.Add("NVIDIA NVENC");
        if (_hardware.SupportsVendor(ExportEncoder.IntelQsv, codec)) names.Add("Intel Quick Sync");
        if (_hardware.SupportsVendor(ExportEncoder.AmdAmf, codec)) names.Add("AMD AMF");
        if (names.Count > 0)
        {
            EncoderStatusText.Text = $"{codecName} 可用硬件加速：{string.Join(" / ", names)}";
            return;
        }
        var softwareName = codec switch
        {
            ExportVideoCodec.Hevc => "libx265",
            ExportVideoCodec.Av1 => "SVT-AV1",
            _ => "x264"
        };
        EncoderStatusText.Text = _hardware.SupportsSoftware(codec)
            ? $"{codecName} 无可用硬件编码器，将使用软件编码 ({softwareName})"
            : $"当前 FFmpeg 不支持 {codecName}，导出时将回退 H.264";
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _ = StartEmbeddedPreviewAsync();
        try
        {
            _media = await _service.ProbeAsync(_request.SourcePath, _playerLifetime.Token);
            IncludeVideoCheck.IsChecked = _media.HasVideo;
            IncludeVideoCheck.IsEnabled = _media.HasVideo;
            IncludeAudioCheck.IsChecked = _media.HasAudio;
            IncludeAudioCheck.IsEnabled = _media.HasAudio;
            SourceInfoText.Text = DescribeMedia(_media);
            UpdateEstimate();
        }
        catch (OperationCanceledException) when (_playerLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SourceInfoText.Text = "媒体信息读取失败，将使用通用导出设置";
            ShowError(ex.Message);
            UpdateEstimate();
        }
    }

    private async Task StartEmbeddedPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(_request.SourcePath) || !File.Exists(_request.SourcePath))
        {
            if (PreviewLoadingText is not null)
                PreviewLoadingText.Text = "源媒体文件不存在";
            return;
        }

        try
        {
            var subtitlePath = !string.IsNullOrWhiteSpace(_request.SubtitlePath) && File.Exists(_request.SubtitlePath)
                ? _request.SubtitlePath
                : null;
            await _player.StartAsync(PreviewVideoHost, _request.SourcePath, subtitlePath, _playerLifetime.Token, startPositionSeconds: 0);
            await _player.SetPauseAsync(true, _playerLifetime.Token);
            _playerInitialized = true;
            if (PreviewLoadingText is not null)
                PreviewLoadingText.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (PreviewLoadingText is not null)
            {
                PreviewLoadingText.Text = $"预览初始化失败：{ex.Message}";
                PreviewLoadingText.IsVisible = true;
            }
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_exporting)
        {
            RequestExportCancellation();
            return;
        }

        _ = CloseAfterTeardownAsync(new WorkspaceExportResult(false, true, null, null));
    }

    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        var format = EnumTag(FormatCombo, ExportFormat.Mp4);
        var extension = MediaExportService.FormatExtension(format);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出成片",
            SuggestedFileName = Path.GetFileName(OutputPathBox.Text),
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType($"{format.ToString().ToUpperInvariant()} 视频") { Patterns = [$"*{extension}"] }]
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) OutputPathBox.Text = EnsureExportExtension(path, format);
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        ErrorText.IsVisible = false;
        var options = CurrentOptions();
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            ShowError("请选择导出位置。");
            return;
        }
        if (!options.IncludeVideo && !options.IncludeAudio)
        {
            ShowError("请至少选择视频或音频中的一项。");
            return;
        }
        if (options.Resolution == ExportResolution.Custom && (options.CustomWidth < 16 || options.CustomHeight < 16))
        {
            ShowError("请输入有效的自定义分辨率（宽和高均需 ≥ 16 像素）。");
            return;
        }
        if ((BitRateCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Custom" && options.VideoBitRateMbps is null)
        {
            ShowError("请输入有效的自定义码率（Mbps）。");
            return;
        }

        try { await _player.SetPauseAsync(true, _playerLifetime.Token); } catch { }

        _exporting = true;
        _closeRequestedDuringExport = false;
        _exportCancellation = new CancellationTokenSource();
        ProgressPanel.IsVisible = true;
        ExportButton.IsEnabled = false;
        CancelButton.Content = "取消导出";
        SetOptionsEnabled(false);
        var progress = new Progress<MediaExportProgress>(UpdateProgress);
        try
        {
            await _service.ExportAsync(options, _media, progress, _exportCancellation.Token);
            if (_closeRequestedDuringExport || _exportCancellation.IsCancellationRequested)
                throw new OperationCanceledException(_exportCancellation.Token);

            _exporting = false;
            OpenExportDirectory(options.OutputPath);
            await CloseAfterTeardownAsync(new WorkspaceExportResult(true, false, options.OutputPath, null));
        }
        catch (OperationCanceledException)
        {
            _exporting = false;
            await CloseAfterTeardownAsync(new WorkspaceExportResult(false, true, null, null));
        }
        catch (Exception ex)
        {
            _exporting = false;
            ShowError(ex.Message);
            ProgressPanel.IsVisible = false;
            ExportButton.IsEnabled = true;
            CancelButton.Content = "取消";
            SetOptionsEnabled(true);
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_exporting)
        {
            RequestExportCancellation();
            return;
        }
        _ = CloseAfterTeardownAsync(new WorkspaceExportResult(false, true, null, null));
    }

    private void RequestExportCancellation()
    {
        _closeRequestedDuringExport = true;
        CancelButton.IsEnabled = false;
        ProgressStatusText.Text = "正在取消…";
        _exportCancellation?.Cancel();
    }

    private async Task CloseAfterTeardownAsync(WorkspaceExportResult result)
    {
        if (_teardownStarted) return;
        _teardownStarted = true;
        _playerLifetime.Cancel();
        try
        {
            await _player.DisposeAsync();
        }
        catch
        {
            // The window must remain closable even if the native preview is already gone.
        }
        finally
        {
            _playerLifetime.Dispose();
        }

        _allowClose = true;
        Close(result);
    }

    private static void OpenExportDirectory(string outputPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch
        {
            // Export completion must not fail merely because Explorer is unavailable.
        }
    }

    private void Option_OnChanged(object? sender, TextChangedEventArgs e) => UpdateEstimate();
    private void Option_OnChanged(object? sender, SelectionChangedEventArgs e) => UpdateEstimate();
    private void Option_OnChanged(object? sender, RoutedEventArgs e) => UpdateEstimate();

    private void ResolutionCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CustomResolutionPanel is null) return;
        CustomResolutionPanel.IsVisible = EnumTag(ResolutionCombo, ExportResolution.Original) == ExportResolution.Custom;
        UpdateEstimate();
    }

    private void BitRateCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CustomBitRatePanel is null) return;
        CustomBitRatePanel.IsVisible = (BitRateCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Custom";
        UpdateEstimate();
    }

    private void FormatCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FormatBadgeText is null || OutputPathBox is null) return;
        var format = EnumTag(FormatCombo, ExportFormat.Mp4);
        FormatBadgeText.Text = format.ToString().ToUpperInvariant();
        var path = OutputPathBox.Text;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var updated = EnsureExportExtension(path, format);
            if (!string.Equals(updated, path, StringComparison.Ordinal)) OutputPathBox.Text = updated;
        }
        UpdateEstimate();
    }

    private void CodecCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateEncoderAvailability();
        UpdateEstimate();
    }

    private void SubtitleExport_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (SubtitleFormatCombo is not null)
            SubtitleFormatCombo.IsEnabled = ExportSubtitlesCheck.IsChecked == true;
        UpdateEstimate();
    }

    private async void PreviewPlayPause_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_playerInitialized) return;
        try { await _player.TogglePauseAsync(_playerLifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private MediaExportOptions CurrentOptions()
    {
        var format = EnumTag(FormatCombo, ExportFormat.Mp4);
        return new MediaExportOptions(
            _request.SourcePath,
            EnsureExportExtension(OutputPathBox.Text ?? string.Empty, format),
            EnumTag(ResolutionCombo, ExportResolution.Original),
            EnumTag(FrameRateCombo, ExportFrameRate.Original),
            EnumTag(QualityCombo, ExportQuality.Recommended),
            IncludeVideoCheck.IsChecked == true,
            IncludeAudioCheck.IsChecked == true,
            _request.SubtitlePath,
            format,
            EnumTag(EncoderCombo, ExportEncoder.Auto),
            ParsePositiveInt(CustomWidthBox.Text),
            ParsePositiveInt(CustomHeightBox.Text),
            CurrentBitRateMbps(),
            ExportSubtitlesCheck.IsChecked == true,
            _request.PlainSubtitlePath,
            EnumTag(CodecCombo, ExportVideoCodec.H264),
            CbrRadio.IsChecked == true ? ExportRateControl.Cbr : ExportRateControl.Vbr,
            IntTag(AudioQualityCombo, 192),
            IntTag(AudioSampleRateCombo, 0),
            EnumTag(SubtitleFormatCombo, ExportSubtitleFormat.Srt));
    }

    private static int IntTag(ComboBox box, int fallback)
    {
        var value = ParsePositiveInt((box.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        return value > 0 ? value : fallback;
    }

    private double? CurrentBitRateMbps()
    {
        var tag = (BitRateCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrEmpty(tag) || tag == "Auto") return null;
        if (tag == "Custom")
            return double.TryParse(CustomBitRateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var custom) && custom > 0
                ? custom
                : null;
        return double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var preset) ? preset : null;
    }

    private static int ParsePositiveInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;

    private static T EnumTag<T>(ComboBox box, T fallback) where T : struct, Enum
    {
        var tag = (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<T>(tag, out var value) ? value : fallback;
    }

    private void UpdateEstimate()
    {
        if (EstimateText is null || OutputPathBox is null) return;
        var options = CurrentOptions();
        var bytes = _service.EstimateOutputBytes(_media, options);
        var duration = FormatDuration(_media.DurationSeconds);
        EstimateText.Text = bytes > 0
            ? $"时长：{duration}  ·  预计大小：{FormatBytes(bytes)}"
            : $"时长：{duration}  ·  大小将在导出时计算";
    }

    private void UpdateProgress(MediaExportProgress progress)
    {
        ExportProgressBar.Value = progress.Fraction;
        ProgressPercentText.Text = $"{progress.Fraction:P0}";
        ProgressStatusText.Text = progress.Status;
        ProgressTimeText.Text = $"{FormatDuration(progress.Processed.TotalSeconds)} / {FormatDuration(progress.Total.TotalSeconds)}";
    }

    private void SetOptionsEnabled(bool enabled)
    {
        ResolutionCombo.IsEnabled = enabled;
        FormatCombo.IsEnabled = enabled;
        FrameRateCombo.IsEnabled = enabled;
        QualityCombo.IsEnabled = enabled;
        BitRateCombo.IsEnabled = enabled;
        CodecCombo.IsEnabled = enabled;
        EncoderCombo.IsEnabled = enabled;
        AudioQualityCombo.IsEnabled = enabled;
        AudioSampleRateCombo.IsEnabled = enabled;
        CustomWidthBox.IsEnabled = enabled;
        CustomHeightBox.IsEnabled = enabled;
        CustomBitRateBox.IsEnabled = enabled;
        VbrRadio.IsEnabled = enabled;
        CbrRadio.IsEnabled = enabled;
        OutputPathBox.IsEnabled = enabled;
        ExportSubtitlesCheck.IsEnabled = enabled;
        SubtitleFormatCombo.IsEnabled = enabled && ExportSubtitlesCheck.IsChecked == true;
        IncludeVideoCheck.IsEnabled = enabled && _media.HasVideo;
        IncludeAudioCheck.IsEnabled = enabled && _media.HasAudio;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private static string SuggestedOutput(WorkspaceExportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SuggestedOutputPath))
            return EnsureExportExtension(request.SuggestedOutputPath, ExportFormat.Mp4);
        var directory = Path.GetDirectoryName(request.SourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var name = string.IsNullOrWhiteSpace(request.ProjectTitle)
            ? Path.GetFileNameWithoutExtension(request.SourcePath)
            : request.ProjectTitle;
        return Path.Combine(directory, SanitizeFileName(name) + "-导出.mp4");
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Trim();
    }

    private static string EnsureExportExtension(string path, ExportFormat format)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var extension = MediaExportService.FormatExtension(format);
        return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, extension);
    }

    private static string DescribeMedia(MediaProbeInfo media)
    {
        var parts = new List<string>();
        if (media.HasVideo && media.Width > 0) parts.Add($"{media.Width}×{media.Height}");
        if (media.FrameRate > 0) parts.Add($"{media.FrameRate:0.##} fps");
        parts.Add(media.HasVideo ? media.HasAudio ? "视频 + 音频" : "仅视频" : "仅音频");
        return string.Join("  ·  ", parts);
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "00:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "--:--";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "--";
        var value = (double)bytes;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture)} {units[index]}";
    }
}
