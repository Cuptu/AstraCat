using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AstraCat;

/// <summary>
/// A modern, interactive video progress/seek bar with hover expansion, live scrub, and floating time tooltip.
/// </summary>
public sealed class VideoSeekBarControl : Control
{
    public static readonly DirectProperty<VideoSeekBarControl, double> PositionProperty =
        AvaloniaProperty.RegisterDirect<VideoSeekBarControl, double>(
            nameof(Position),
            o => o.Position,
            (o, v) => o.Position = v);

    public static readonly DirectProperty<VideoSeekBarControl, double> DurationProperty =
        AvaloniaProperty.RegisterDirect<VideoSeekBarControl, double>(
            nameof(Duration),
            o => o.Duration,
            (o, v) => o.Duration = v);

    private double _position;
    private double _duration = 1;
    private bool _isHovered;
    private bool _isDragging;
    private double _hoverX = -1;

    private static readonly IBrush TrackBackgroundBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
    private static readonly IBrush HoverTrackBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
    private static readonly IBrush ProgressFillBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.Parse("#38BDF8"), 0.0),
            new GradientStop(Color.Parse("#2563EB"), 1.0)
        ]
    };
    private static readonly IBrush ThumbBrush = Brushes.White;
    private static readonly IBrush ThumbRingBrush = new SolidColorBrush(Color.FromArgb(120, 37, 99, 235));
    private static readonly IBrush TooltipBackgroundBrush = new SolidColorBrush(Color.FromArgb(235, 15, 18, 24));
    private static readonly IPen TooltipBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 0.8);
    private static readonly IBrush TooltipTextBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly Typeface TooltipTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public event EventHandler<double>? SeekRequested;
    public event EventHandler<double>? Scrubbing;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCompleted;

    public VideoSeekBarControl()
    {
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Position
    {
        get => _position;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0.001, _duration));
            if (Math.Abs(_position - clamped) > 0.0001)
            {
                SetAndRaise(PositionProperty, ref _position, clamped);
                InvalidateVisual();
            }
        }
    }

    public double Duration
    {
        get => _duration;
        set
        {
            var clamped = Math.Max(0.001, value);
            if (Math.Abs(_duration - clamped) > 0.0001)
            {
                SetAndRaise(DurationProperty, ref _duration, clamped);
                InvalidateVisual();
            }
        }
    }

    public bool IsDragging => _isDragging;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsFinite(availableSize.Height) && availableSize.Height > 0
            ? Math.Min(availableSize.Height, 26)
            : 24;
        return new Size(availableSize.Width, height);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isHovered = true;
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovered = false;
        _hoverX = -1;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        _hoverX = Math.Clamp(pt.X, 0, Bounds.Width);

        if (_isDragging && Bounds.Width > 0)
        {
            var fraction = Math.Clamp(_hoverX / Bounds.Width, 0, 1);
            var targetTime = fraction * _duration;
            _position = targetTime;
            Scrubbing?.Invoke(this, targetTime);
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        e.Pointer.Capture(this);
        _isDragging = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);

        var pt = e.GetPosition(this);
        _hoverX = Math.Clamp(pt.X, 0, Bounds.Width);
        if (Bounds.Width > 0)
        {
            var fraction = Math.Clamp(_hoverX / Bounds.Width, 0, 1);
            var targetTime = fraction * _duration;
            _position = targetTime;
            Scrubbing?.Invoke(this, targetTime);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);

            var pt = e.GetPosition(this);
            var fraction = Bounds.Width > 0 ? Math.Clamp(pt.X / Bounds.Width, 0, 1) : 0;
            var targetTime = fraction * _duration;
            _position = targetTime;

            ScrubCompleted?.Invoke(this, EventArgs.Empty);
            SeekRequested?.Invoke(this, targetTime);

            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_isDragging)
        {
            _isDragging = false;
            ScrubCompleted?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_duration <= 0) return;

        var step = Math.Max(1.0, _duration * 0.02); // 2% of duration or at least 1s
        var delta = e.Delta.Y > 0 ? step : -step;
        var newPos = Math.Clamp(_position + delta, 0, _duration);
        Position = newPos;
        SeekRequested?.Invoke(this, newPos);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var activeState = _isHovered || _isDragging;
        var trackHeight = activeState ? 6.0 : 4.0;
        var trackY = (bounds.Height - trackHeight) / 2.0;
        var cornerRadius = trackHeight / 2.0;

        // 1. Background full track
        var fullTrackRect = new Rect(0, trackY, bounds.Width, trackHeight);
        context.DrawRectangle(TrackBackgroundBrush, null, fullTrackRect, cornerRadius, cornerRadius);

        // 2. Hover ghost bar (when hovering and not dragging)
        if (_isHovered && _hoverX > 0 && !_isDragging)
        {
            var hoverRect = new Rect(0, trackY, Math.Min(_hoverX, bounds.Width), trackHeight);
            context.DrawRectangle(HoverTrackBrush, null, hoverRect, cornerRadius, cornerRadius);
        }

        // 3. Progress fill bar
        var progressFraction = _duration > 0 ? Math.Clamp(_position / _duration, 0, 1) : 0;
        var progressWidth = bounds.Width * progressFraction;
        if (progressWidth > 0)
        {
            var progressRect = new Rect(0, trackY, progressWidth, trackHeight);
            context.DrawRectangle(ProgressFillBrush, null, progressRect, cornerRadius, cornerRadius);
        }

        // 4. Scrubber Thumb Handle
        var thumbX = bounds.Width * progressFraction;
        var thumbY = bounds.Height / 2.0;
        var thumbRadius = activeState ? 6.5 : 3.5;

        if (activeState)
        {
            // Outer glow ring
            context.DrawEllipse(ThumbRingBrush, null, new Point(thumbX, thumbY), thumbRadius + 3.0, thumbRadius + 3.0);
        }

        // Inner solid circle
        context.DrawEllipse(ThumbBrush, null, new Point(thumbX, thumbY), thumbRadius, thumbRadius);

        // 5. Floating Hover Time Tooltip
        if (activeState && _hoverX >= 0 && _duration > 0)
        {
            var hoverFraction = Math.Clamp(_hoverX / bounds.Width, 0, 1);
            var hoverTime = hoverFraction * _duration;
            var timeText = FormatTime(hoverTime);

            var formattedText = new FormattedText(
                timeText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                TooltipTypeface,
                10.5,
                TooltipTextBrush);

            var bubblePaddingX = 8.0;
            var bubblePaddingY = 3.5;
            var bubbleWidth = formattedText.Width + bubblePaddingX * 2;
            var bubbleHeight = formattedText.Height + bubblePaddingY * 2;

            var bubbleX = Math.Clamp(_hoverX - bubbleWidth / 2.0, 4.0, bounds.Width - bubbleWidth - 4.0);
            var bubbleY = trackY - bubbleHeight - 8.0;

            var bubbleRect = new Rect(bubbleX, bubbleY, bubbleWidth, bubbleHeight);
            context.DrawRectangle(TooltipBackgroundBrush, TooltipBorderPen, bubbleRect, 5.0, 5.0);

            context.DrawText(formattedText, new Point(bubbleX + bubblePaddingX, bubbleY + bubblePaddingY));
        }
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "00:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}
