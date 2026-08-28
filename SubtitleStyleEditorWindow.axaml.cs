using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Globalization;
using System.Text;

namespace AstraCat;

public partial class SubtitleStyleEditorWindow : Window
{
    private static readonly Lazy<string[]> SystemFontNames = new(() => FontManager.Current.SystemFonts
        .Select(font => font.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray());

    private readonly SubtitleStyleDefinition _original;
    private SubtitleStyleDefinition _working;
    private Bitmap? _previewBitmap;
    private readonly MpvPlayerService _exactPreviewPlayer = new();
    private readonly string? _previewMediaPath;
    private readonly double _previewPositionSeconds;
    private readonly string _previewTempDirectory;
    private readonly string _previewAssPath;
    private readonly CancellationTokenSource _previewRenderLifetime = new();
    private Task? _previewRenderWorker;
    private int _previewRenderGeneration;
    private bool _isClosed;
    private bool _closeTeardownStarted;
    private bool _allowClose;
    private SubtitleStyleDefinition? _closeResult;
    private bool _loading;
    private TextBlock[] _outlineLayers = Array.Empty<TextBlock>();
    private readonly List<TextBlock> _boxedPreviewTexts = [];
    private readonly List<Border> _boxedPreviewBorders = [];
    private Point _dragStartPoint;
    private bool _isDragging;
    private Point _dragStartAnchor;

    private const double VideoWidth = 1920;
    private const double VideoHeight = 1080;
    private const double SafeLeft = 96;
    private const double SafeRight = VideoWidth - SafeLeft;
    private const double SafeTop = 54;
    private const double SafeBottom = VideoHeight - SafeTop;
    private const double SnapThreshold = 32;
    private const double PreviewLinearScale = 0.816496580927726; // sqrt(2/3): one-third less area
    private const double PreviewEditorHeight = 48;
    private const double PreviewEditorSpacing = 8;

    private void EditorWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Let the available width determine the compact 16:9 preview,
        // but always reserve usable space for the scrolling style controls.
        var availableWidth = Math.Max(0, e.NewSize.Width - 44);
        var availableHeight = Math.Max(0, e.NewSize.Height - 64 - 180);
        if (availableWidth <= 0 || availableHeight <= 0) return;

        var desiredVideoHeight = availableWidth * VideoHeight / VideoWidth * PreviewLinearScale;
        var desiredRegionHeight = desiredVideoHeight + PreviewEditorSpacing + PreviewEditorHeight;
        PreviewRegion.Height = Math.Max(240, Math.Min(desiredRegionHeight, availableHeight));
    }

    private void PreviewRegion_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var availableWidth = Math.Max(0, e.NewSize.Width);
        var availableHeight = Math.Max(0, e.NewSize.Height - PreviewEditorSpacing - PreviewEditorHeight);
        if (availableWidth <= 0 || availableHeight <= 0) return;

        const double videoAspect = VideoWidth / VideoHeight;
        var width = availableWidth;
        var height = width / videoAspect;
        if (height > availableHeight)
        {
            height = availableHeight;
            width = height * videoAspect;
        }

        PreviewHost.Width = Math.Floor(width);
        PreviewHost.Height = Math.Floor(height);
    }

    public SubtitleStyleEditorWindow()
        : this(SubtitleStyleDefinition.MainDefault())
    {
    }

    public SubtitleStyleEditorWindow(SubtitleStyleDefinition style, Bitmap? frameBitmap = null, string? sampleText = null,
        string? mediaPath = null, double positionSeconds = 0)
    {
        InitializeComponent();
        _previewMediaPath = !string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath) ? mediaPath : null;
        _previewPositionSeconds = Math.Max(0, positionSeconds);
        _previewTempDirectory = Path.Combine(Path.GetTempPath(), "AstraCat", "style-preview", Guid.NewGuid().ToString("N"));
        _previewAssPath = Path.Combine(_previewTempDirectory, "preview.ass");
        _original = style.Clone();
        _working = style.Clone();
        Title = $"字幕样式编辑 - {_working.Name}";

        FontCombo.ItemsSource = SystemFontNames.Value;
        FontSizeCombo.ItemsSource = new[]
        {
            12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 40, 42, 44,
            48, 52, 54, 56, 60, 64, 68, 72, 80, 88, 96, 108, 120
        };
        FontCombo.PropertyChanged += EditorEditableCombo_OnPropertyChanged;
        FontSizeCombo.PropertyChanged += EditorEditableCombo_OnPropertyChanged;
        Opened += ExactPreviewWindow_OnOpened;

        if (frameBitmap != null)
        {
            SetPreviewFrame(frameBitmap);
        }
        else
        {
            PreviewBackgroundImage.IsVisible = false;
        }

        var previewSample = string.IsNullOrWhiteSpace(sampleText) ? "AstraCat@样式预览" : sampleText.Trim();
        PreviewTextEditor.Text = previewSample;
        SetPreviewTexts(previewSample);

        LoadControls();
    }

    public void SetPreviewFrame(Bitmap frameBitmap)
    {
        var previous = _previewBitmap;
        _previewBitmap = frameBitmap;
        PreviewBackgroundImage.Source = frameBitmap;
        PreviewBackgroundImage.IsVisible = true;
        previous?.Dispose();
    }

    public void SetPreviewSampleText(string sampleText)
    {
        if (string.IsNullOrWhiteSpace(sampleText)) return;
        var normalized = sampleText.Trim();
        if (string.Equals(PreviewTextEditor.Text, normalized, StringComparison.Ordinal)) return;
        // TextChanged updates the state and schedules exactly one render.
        PreviewTextEditor.Text = normalized;
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        PreviewBackgroundImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_closeTeardownStarted) return;
        _closeTeardownStarted = true;
        _ = CloseAfterExactPreviewTeardownAsync();
    }

    private async Task CloseAfterExactPreviewTeardownAsync()
    {
        _isClosed = true;
        _previewRenderLifetime.Cancel();
        try { await DisposeExactPreviewAsync(); } catch { }
        _allowClose = true;
        Close(_closeResult);
    }

    private async Task DisposeExactPreviewAsync()
    {
        try { await _exactPreviewPlayer.DisposeAsync(); } catch { }
        try { if (Directory.Exists(_previewTempDirectory)) Directory.Delete(_previewTempDirectory, true); } catch { }
        _previewRenderLifetime.Dispose();
    }

    /// <summary>
    /// 描边/阴影层：单个偏移副本画不出环绕文字的描边，
    /// 这里用 8 方向副本按描边宽度外扩叠出实心描边（近似 libass），
    /// 另加一个右下方向的阴影副本，偏移 = 描边宽度 + 阴影距离。
    /// </summary>
    private TextBlock[] EnsureOutlineLayers()
    {
        if (_outlineLayers.Length > 0) return _outlineLayers;
        var offsets = new (double Dx, double Dy)[]
        {
            (1, 0), (0.71, 0.71), (0, 1), (-0.71, 0.71),
            (-1, 0), (-0.71, -0.71), (0, -1), (0.71, -0.71)
        };
        var layers = new List<TextBlock>(offsets.Length + 1);
        foreach (var (dx, dy) in offsets)
            layers.Add(new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                IsHitTestVisible = false,
                Tag = (dx, dy, false)
            });
        layers.Add(new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            IsHitTestVisible = false,
            Tag = (1d, 1d, true)
        });
        _outlineLayers = layers.ToArray();
        foreach (var layer in _outlineLayers) PreviewOutlineLayer.Children.Add(layer);
        return _outlineLayers;
    }

    private void SetPreviewTexts(string text)
    {
        PreviewText.Text = text;
        foreach (var layer in EnsureOutlineLayers()) layer.Text = text;
        RebuildBoxPreviewLines(text);
    }

    private void RebuildBoxPreviewLines(string text)
    {
        PreviewBoxLines.Children.Clear();
        _boxedPreviewTexts.Clear();
        _boxedPreviewBorders.Clear();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var lineText = new TextBlock
            {
                Text = string.IsNullOrEmpty(line) ? " " : line,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                IsHitTestVisible = false
            };
            var background = new Border { Child = lineText, IsHitTestVisible = false };
            _boxedPreviewTexts.Add(lineText);
            _boxedPreviewBorders.Add(background);
            PreviewBoxLines.Children.Add(background);
        }
    }

    private void PreviewTextEditor_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        SetPreviewTexts(PreviewTextEditor.Text ?? string.Empty);
        ApplyPreview();
    }

    private void LoadControls()
    {
        _loading = true;
        FontCombo.SelectedItem = FontCombo.ItemsSource?.Cast<string>()
            .FirstOrDefault(name => string.Equals(name, _working.FontFamily, StringComparison.CurrentCultureIgnoreCase));
        FontCombo.Text = _working.FontFamily;
        FontSizeCombo.Text = _working.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
        TextColorPicker.Color = ParseColor(_working.TextColor, Colors.White);
        OutlineColorPicker.Color = ParseColor(_working.OutlineColor, Color.Parse("#22263B"));
        BoxColorPicker.Color = ParseColor(_working.BoxColor, Colors.Black);
        OutlineSlider.Value = _working.OutlineWidth;
        ShadowSlider.Value = _working.ShadowDistance;
        BoxOpacitySlider.Value = _working.BoxOpacity;
        BoxPaddingSlider.Value = _working.BoxPadding;
        HorizontalMarginSlider.Value = _working.HorizontalMargin;
        MarginSlider.Value = _working.VerticalMargin;
        AlignmentCombo.SelectedIndex = FindContent(AlignmentCombo, _working.Alignment, 7);
        BoldToggle.IsChecked = _working.Bold;
        ItalicToggle.IsChecked = _working.Italic;
        UnderlineToggle.IsChecked = _working.Underline;
        BoxedToggle.IsChecked = _working.Boxed;
        _loading = false;
        ReadControls();
    }

    private static int FindContent(ComboBox box, string value, int fallback)
    {
        for (var i = 0; i < box.ItemCount; i++)
            if (box.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return i;
        return fallback;
    }

    private void ReadControls()
    {
        if (_loading) return;
        var fontName = FontCombo.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(fontName)) _working.FontFamily = fontName;
        var sizeText = FontSizeCombo.Text?.Trim();
        if (double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize) ||
            double.TryParse(sizeText, NumberStyles.Float, CultureInfo.CurrentCulture, out fontSize))
            _working.FontSize = Math.Clamp(fontSize, 12, 120);
        _working.TextColor = ToRgbHex(TextColorPicker.Color);
        _working.OutlineColor = ToRgbHex(OutlineColorPicker.Color);
        _working.BoxColor = ToRgbHex(BoxColorPicker.Color);
        _working.OutlineWidth = OutlineSlider.Value;
        _working.ShadowDistance = ShadowSlider.Value;
        _working.BoxOpacity = BoxOpacitySlider.Value;
        _working.BoxPadding = BoxPaddingSlider.Value;
        _working.HorizontalMargin = HorizontalMarginSlider.Value;
        _working.VerticalMargin = MarginSlider.Value;
        _working.Alignment = (AlignmentCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _working.Alignment;
        _working.Bold = BoldToggle.IsChecked == true;
        _working.Italic = ItalicToggle.IsChecked == true;
        _working.Underline = UnderlineToggle.IsChecked == true;
        _working.Boxed = BoxedToggle.IsChecked == true;
        ApplyPreview();
    }

    private void ApplyPreview()
    {
        // The editor preview is rendered exclusively by libass. These controls
        // remain as layout/input state only and must never become a second,
        // approximate subtitle renderer on top of the video frame.
        PreviewTextContainer.IsVisible = false;
        PreviewOutlineLayer.IsVisible = false;

        PreviewText.FontFamily = new FontFamily(_working.FontFamily);
        PreviewText.FontSize = Math.Clamp(_working.FontSize, 18, 160);
        PreviewText.FontWeight = _working.Bold ? FontWeight.Bold : FontWeight.Normal;
        PreviewText.FontStyle = _working.Italic ? FontStyle.Italic : FontStyle.Normal;
        PreviewText.TextDecorations = _working.Underline ? TextDecorations.Underline : null;
        PreviewText.Foreground = ParseBrush(_working.TextColor, "#FFFFFF");
        PreviewText.Background = Brushes.Transparent;

        var boxPadding = _working.Boxed ? Math.Max(0, _working.BoxPadding) : 0;
        var boxColor = ParseColor(_working.BoxColor, Colors.Black);
        var boxAlpha = (byte)Math.Round(255 * Math.Clamp(_working.BoxOpacity, 0, 100) / 100);
        var boxBrush = new SolidColorBrush(Color.FromArgb(boxAlpha, boxColor.R, boxColor.G, boxColor.B));
        PreviewTextContainer.Background = Brushes.Transparent;
        PreviewTextContainer.CornerRadius = new CornerRadius(0);
        PreviewTextContainer.Padding = new Thickness(0);
        PreviewText.IsVisible = !_working.Boxed;
        PreviewBoxLines.IsVisible = _working.Boxed;

        var isLeft = _working.Alignment.EndsWith("居左", StringComparison.Ordinal);
        var isRight = _working.Alignment.EndsWith("居右", StringComparison.Ordinal);
        var isTop = _working.Alignment.StartsWith("顶部", StringComparison.Ordinal);
        var isCenterV = _working.Alignment.StartsWith("中部", StringComparison.Ordinal);

        PreviewTextContainer.HorizontalAlignment = isLeft ? HorizontalAlignment.Left :
            isRight ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        PreviewText.HorizontalAlignment = HorizontalAlignment.Stretch;
        PreviewText.TextAlignment = isLeft ? TextAlignment.Left :
            isRight ? TextAlignment.Right : TextAlignment.Center;

        PreviewTextContainer.VerticalAlignment = isTop ? VerticalAlignment.Top :
            isCenterV ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        PreviewText.VerticalAlignment = VerticalAlignment.Center;

        var h = Math.Max(0, _working.HorizontalMargin);
        var v = Math.Max(0, _working.VerticalMargin);
        // libass lays out every line inside PlayResX minus MarginL/MarginR.
        // An auto-sized Avalonia TextBlock instead grows beyond the 1920 canvas
        // and gets clipped, which was why long English text stayed on one line.
        var safeTextWidth = Math.Max(80, 1920 - h * 2);
        PreviewTextContainer.Width = double.NaN;
        PreviewTextContainer.MaxWidth = safeTextWidth;
        PreviewText.Width = double.NaN;
        PreviewText.MaxWidth = safeTextWidth;

        PreviewTextContainer.Margin = new Thickness(
            isLeft ? h : 0,
            isTop ? v : 0,
            isRight ? h : 0,
            (!isTop && !isCenterV) ? v : 0);
        PreviewText.Margin = new Thickness(0);

        var lineAlignment = isLeft ? HorizontalAlignment.Left :
            isRight ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        PreviewBoxLines.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var lineText in _boxedPreviewTexts)
        {
            lineText.FontFamily = PreviewText.FontFamily;
            lineText.FontSize = PreviewText.FontSize;
            lineText.FontWeight = PreviewText.FontWeight;
            lineText.FontStyle = PreviewText.FontStyle;
            lineText.TextDecorations = PreviewText.TextDecorations;
            lineText.Foreground = PreviewText.Foreground;
            lineText.TextAlignment = PreviewText.TextAlignment;
            lineText.MaxWidth = Math.Max(40, safeTextWidth - boxPadding * 2);
        }
        foreach (var background in _boxedPreviewBorders)
        {
            background.HorizontalAlignment = lineAlignment;
            background.Background = boxBrush;
            background.CornerRadius = new CornerRadius(0);
            background.Padding = new Thickness(boxPadding, boxPadding * .35);
            background.MaxWidth = safeTextWidth;
        }

        // 描边层：8 方向副本按描边宽度外扩形成环绕描边；阴影副本偏移 描边宽度 + 阴影距离。
        // 用 RenderTransform 做偏移，不参与布局，副本与主文字完全重叠后平移。
        var outlineBrush = ParseBrush(_working.OutlineColor, "#22263B");
        foreach (var layer in EnsureOutlineLayers())
        {
            var (dx, dy, isShadow) = ((double, double, bool))layer.Tag!;
            layer.FontFamily = PreviewText.FontFamily;
            layer.FontSize = PreviewText.FontSize;
            layer.FontWeight = PreviewText.FontWeight;
            layer.FontStyle = PreviewText.FontStyle;
            layer.TextDecorations = PreviewText.TextDecorations;
            layer.HorizontalAlignment = PreviewTextContainer.HorizontalAlignment;
            layer.VerticalAlignment = PreviewTextContainer.VerticalAlignment;
            layer.TextAlignment = PreviewText.TextAlignment;
            layer.TextWrapping = TextWrapping.Wrap;
            layer.TextTrimming = TextTrimming.None;
            layer.Width = double.NaN;
            layer.MaxWidth = Math.Max(40, safeTextWidth - boxPadding * 2);
            layer.Margin = new Thickness(
                isLeft ? h + boxPadding : 0,
                isTop ? v + boxPadding : 0,
                isRight ? h + boxPadding : 0,
                (!isTop && !isCenterV) ? v + boxPadding : 0);
            layer.Foreground = isShadow ? ParseBrush("#87000000", "#87000000") : outlineBrush;
            var offset = isShadow ? _working.OutlineWidth + _working.ShadowDistance : _working.OutlineWidth;
            layer.RenderTransform = offset > 0 ? new TranslateTransform(dx * offset, dy * offset) : null;
            layer.IsVisible = !_working.Boxed && offset > 0;
        }

        OutlineValueText.Text = $"{_working.OutlineWidth:0.0}";
        ShadowValueText.Text = $"{_working.ShadowDistance:0.0}";
        BoxOpacityValueText.Text = $"{_working.BoxOpacity:0}%";
        BoxPaddingValueText.Text = $"{_working.BoxPadding:0} px";
        HorizontalMarginValueText.Text = $"{_working.HorizontalMargin:0} px";
        MarginValueText.Text = $"{_working.VerticalMargin:0} px";
        ScheduleExactPreview();
    }

    private void ScheduleExactPreview()
    {
        if (_isClosed || _previewMediaPath == null || !File.Exists(_previewMediaPath)) return;

        PreviewTextContainer.IsVisible = false;
        PreviewOutlineLayer.IsVisible = false;
        ++_previewRenderGeneration;

        // A single worker owns the ASS file. Rapid changes are debounced and
        // collapsed into one mpv/libass subtitle reload.
        if (_previewRenderWorker == null || _previewRenderWorker.IsCompleted)
            _previewRenderWorker = RunExactPreviewWorkerAsync(_previewRenderLifetime.Token);
    }

    private async Task RunExactPreviewWorkerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !_isClosed)
            {
                var generation = _previewRenderGeneration;
                await Task.Delay(220, token);

                // Debounce to the most recent controls/text state.
                if (generation != _previewRenderGeneration) continue;

                await ReloadExactPreviewAsync(generation, token);
                if (generation == _previewRenderGeneration) return;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _previewRenderWorker = null;
        }
    }

    private async void ExactPreviewWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_previewMediaPath == null || _isClosed) return;
        try
        {
            var token = _previewRenderLifetime.Token;
            ExactPreviewVideoHost.IsVisible = true;
            ExactPreviewVideoHost.ShowImmediate();
            await WritePreviewAssAsync(token);
            await _exactPreviewPlayer.StartAsync(ExactPreviewVideoHost, _previewMediaPath, _previewAssPath,
                token, _previewPositionSeconds);
            if (_isClosed) return;
            await _exactPreviewPlayer.SetPauseAsync(true, token);
            PreviewBackgroundImage.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch
        {
            ExactPreviewVideoHost.HideImmediate();
            ExactPreviewVideoHost.IsVisible = false;
        }
    }

    private async Task ReloadExactPreviewAsync(int generation, CancellationToken token)
    {
        try
        {
            if (_isClosed || generation != _previewRenderGeneration) return;
            await WritePreviewAssAsync(token);
            if (generation != _previewRenderGeneration) return;
            if (_exactPreviewPlayer.IsRunning)
                await _exactPreviewPlayer.ReloadCurrentSubtitleAsync(token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task WritePreviewAssAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_previewTempDirectory);
        var content = MainWindow.BuildSubtitleStylePreviewAss(
            _working.Clone(), PreviewTextEditor.Text ?? string.Empty);
        var stagingPath = _previewAssPath + ".new";
        await File.WriteAllTextAsync(stagingPath, content, new UTF8Encoding(false), token);
        File.Move(stagingPath, _previewAssPath, true);
    }

    private static IBrush ParseBrush(string? hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brush.Parse(fallback);
        try
        {
            var cleaned = hex.Trim();
            if (!cleaned.StartsWith('#')) cleaned = "#" + cleaned;
            if (Color.TryParse(cleaned, out var color))
                return new SolidColorBrush(color);
        }
        catch { }
        return Brush.Parse(fallback);
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var cleaned = hex.Trim();
        if (!cleaned.StartsWith('#')) cleaned = "#" + cleaned;
        return Color.TryParse(cleaned, out var color) ? color : fallback;
    }

    private static string ToRgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void PreviewText_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(VirtualVideoCanvas).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(VirtualVideoCanvas);
            _dragStartAnchor = GetCurrentAnchor();
            SafeAreaGuide.IsVisible = true;
            e.Pointer.Capture(PreviewTextContainer);
            e.Handled = true;
        }
    }

    private void PreviewText_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var currentPoint = e.GetPosition(VirtualVideoCanvas);
        var deltaX = currentPoint.X - _dragStartPoint.X;
        var deltaY = currentPoint.Y - _dragStartPoint.Y;

        var targetX = Math.Clamp(_dragStartAnchor.X + deltaX, 0, VideoWidth);
        var targetY = Math.Clamp(_dragStartAnchor.Y + deltaY, 0, VideoHeight);

        var snapCenterX = Math.Abs(targetX - VideoWidth / 2) <= SnapThreshold;
        var snapCenterY = Math.Abs(targetY - VideoHeight / 2) <= SnapThreshold;
        var snapSafeLeft = Math.Abs(targetX - SafeLeft) <= SnapThreshold;
        var snapSafeRight = Math.Abs(targetX - SafeRight) <= SnapThreshold;
        var snapSafeTop = Math.Abs(targetY - SafeTop) <= SnapThreshold;
        var snapSafeBottom = Math.Abs(targetY - SafeBottom) <= SnapThreshold;

        if (snapCenterX) targetX = VideoWidth / 2;
        else if (snapSafeLeft) targetX = SafeLeft;
        else if (snapSafeRight) targetX = SafeRight;

        if (snapCenterY) targetY = VideoHeight / 2;
        else if (snapSafeTop) targetY = SafeTop;
        else if (snapSafeBottom) targetY = SafeBottom;

        var horizontal = snapCenterX ? 1 : targetX < VideoWidth / 2 ? 0 : 2;
        var vertical = snapCenterY ? 1 : targetY < VideoHeight / 2 ? 0 : 2;
        var horizontalMargin = horizontal switch
        {
            0 => targetX,
            2 => VideoWidth - targetX,
            _ => 0
        };
        var verticalMargin = vertical switch
        {
            0 => targetY,
            2 => VideoHeight - targetY,
            _ => 0
        };

        _loading = true;
        AlignmentCombo.SelectedIndex = vertical * 3 + horizontal;
        HorizontalMarginSlider.Value = Math.Clamp(horizontalMargin, 0, HorizontalMarginSlider.Maximum);
        MarginSlider.Value = Math.Clamp(verticalMargin, 0, MarginSlider.Maximum);
        _loading = false;
        ReadControls();

        VerticalSnapGuide.IsVisible = snapCenterX;
        HorizontalSnapGuide.IsVisible = snapCenterY;

        e.Handled = true;
    }

    private void PreviewText_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            HideDragGuides();
            e.Handled = true;
        }
    }

    private Point GetCurrentAnchor()
    {
        var isLeft = _working.Alignment.EndsWith("居左", StringComparison.Ordinal);
        var isRight = _working.Alignment.EndsWith("居右", StringComparison.Ordinal);
        var isTop = _working.Alignment.StartsWith("顶部", StringComparison.Ordinal);
        var isCenterV = _working.Alignment.StartsWith("中部", StringComparison.Ordinal);
        var x = isLeft ? _working.HorizontalMargin :
            isRight ? VideoWidth - _working.HorizontalMargin : VideoWidth / 2;
        var y = isTop ? _working.VerticalMargin :
            isCenterV ? VideoHeight / 2 : VideoHeight - _working.VerticalMargin;
        return new Point(x, y);
    }

    private void PreviewText_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDragging = false;
        HideDragGuides();
    }

    private void HideDragGuides()
    {
        SafeAreaGuide.IsVisible = false;
        VerticalSnapGuide.IsVisible = false;
        HorizontalSnapGuide.IsVisible = false;
    }

    private void EditorCombo_OnChanged(object? sender, SelectionChangedEventArgs e) => ReadControls();
    private void EditorColorPicker_OnColorChanged(object? sender, ColorChangedEventArgs e) => ReadControls();
    private void EditorEditableCombo_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ComboBox.TextProperty) ReadControls();
    }
    private void EditorNumeric_OnChanged(object? sender, NumericUpDownValueChangedEventArgs e) => ReadControls();
    private void EditorSlider_OnChanged(object? sender, RangeBaseValueChangedEventArgs e) => ReadControls();
    private void EditorToggle_OnClick(object? sender, RoutedEventArgs e) => ReadControls();
    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        _closeResult = null;
        Close(null);
    }

    private void Reset_OnClick(object? sender, RoutedEventArgs e)
    {
        _working = _original.Id == "secondary"
            ? SubtitleStyleDefinition.SecondaryDefault()
            : SubtitleStyleDefinition.MainDefault();
        _working.Name = _original.Name;
        LoadControls();
    }

    private void Apply_OnClick(object? sender, RoutedEventArgs e)
    {
        ReadControls();
        _closeResult = _working.Clone();
        Close(_closeResult);
    }
}
