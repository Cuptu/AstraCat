using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using System.Diagnostics;

namespace AstraCat;

public sealed record CueTimingChange(int Index, long OldStart, long OldEnd, long NewStart, long NewEnd,
    int OldTrack = -1, int NewTrack = -1);
public sealed record TimelineCueEdit(IReadOnlyList<CueTimingChange> Changes);
public sealed record ActiveCueSnapshot(IReadOnlyList<int> CueIndexes, long ValidFromMilliseconds,
    long ValidThroughMilliseconds);

public sealed class SubtitleTimelineControl : Control
{
    private sealed class CueTrackIndex
    {
        public int[] CueIndexes { get; init; } = Array.Empty<int>();
        public long[] Starts { get; init; } = Array.Empty<long>();
        public long[] PrefixMaximumEnds { get; init; } = Array.Empty<long>();
    }

    private readonly record struct ConflictSpan(int Track, long Start, long End);

    private enum DragMode { None, Scrub, Move, TrimStart, TrimEnd, VScroll, Marquee }

    private IReadOnlyList<EditorSubtitleCue> _cues = Array.Empty<EditorSubtitleCue>();
    private IReadOnlyList<float> _peaks = Array.Empty<float>();
    private double _waveformDurationSeconds;
    private double _durationSeconds = 60;
    private double _positionSeconds;
    private double _viewStartSeconds;
    private double _pixelsPerSecond = 82;
    private int _selectedIndex = -1;
    private readonly HashSet<int> _selectedIndexes = new();
    private DragMode _dragMode;
    private Point _dragOrigin;
    private readonly List<(int Index, long Start, long End, int Track)> _dragSnapshot = new();
    private readonly Dictionary<int, (long Start, long End, int Track)> _dragPreview = new();
    private long[] _dragSnapTargets = Array.Empty<long>();
    private long _lastScrubDispatchTimestamp;
    private readonly DispatcherTimer _edgeAutoScrollTimer;
    private Point _edgeAutoScrollPoint;
    private KeyModifiers _edgeAutoScrollModifiers;
    private bool _marqueeHasDragged;
    private Point _marqueeOrigin;
    private Point _marqueeCurrent;
    private double _marqueeOriginTimeSeconds;
    private double _marqueeOriginContentY;
    private KeyModifiers _marqueeModifiers;
    private readonly HashSet<int> _marqueeBaseSelection = new();
    private double _dragOriginViewStartSeconds;
    private int _trackCount = 2;
    private SubtitleStyleDefinition _mainStyle = SubtitleStyleDefinition.MainDefault();
    private SubtitleStyleDefinition _secondaryStyle = SubtitleStyleDefinition.SecondaryDefault();
    private long? _snapGuideMilliseconds;
    private int _hoveredIndex = -1;
    private bool _splitMode;
    private Cursor? _splitCursor;
    private double _splitPreviewX = -1;
    private int _splitPreviewIndex = -1;
    private long _splitPreviewMilliseconds = -1;
    private CueTrackIndex[] _cueTrackIndexes = Array.Empty<CueTrackIndex>();
    private ConflictSpan[] _conflictSpans = Array.Empty<ConflictSpan>();
    private long[] _cueBoundaries = Array.Empty<long>();
    private bool _cueIndexesDirty = true;

    private static readonly Typeface TimelineTypeface = new("Inter");
    private static readonly Typeface TimelineSemiboldTypeface = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Geometry PlayheadMarkerGeometry = Geometry.Parse("M -6,0 L 6,0 L 0,8 Z");
    private const int TextLayoutCacheLimit = 512;
    private readonly Dictionary<(string Text, double Size, string Color, double Width, bool Centered), TextLayout> _textLayouts = new();
    private static readonly Dictionary<(string Color, double Thickness), Pen> PenCache = new();
    private static readonly Pen SplitGuidePen = new(CachedBrush("#B31F77C8"), 1, DashStyle.Dash);

    public event EventHandler<double>? SeekRequested;
    public event EventHandler<int>? SelectedCueChanged;
    public event EventHandler<int>? CueEditRequested;
    public event EventHandler<(long PositionMilliseconds, int Track)>? CueInsertRequested;
    public event EventHandler<int>? CueDeleteRequested;
    public event EventHandler<IReadOnlyList<int>>? CuesDeleteRequested;
    public event EventHandler? SelectAllRequested;
    public event EventHandler<IReadOnlyList<int>>? SelectionChanged;
    public event EventHandler? ViewportChanged;
    public event EventHandler<TimelineCueEdit>? CueEdited;
    public event EventHandler? CueInteractionStarted;
    public event EventHandler? CueInteractionCompleted;
    public event EventHandler<(int Index, long PositionMilliseconds)>? CueSplitRequested;
    public event EventHandler<(int Index, long PositionMilliseconds)>? SplitPreviewChanged;
    public event EventHandler<int>? TrackCountChanged;
    public event EventHandler<int>? TrackRemoving;
    public event EventHandler<(int Index, bool Added)>? TrackStructureChanged;
    public event EventHandler<(int Track, bool Muted)>? TrackMutedChanged;

    public const double TrackHeaderWidth = 32.0;
    public const int VisibleTrackCapacity = 3;
    public const double LaneHeight = 34.0;
    public const double CueBlockHeight = 28.0;
    public const double VerticalScrollBarWidth = 12.0;

    private static readonly string[] TrackPalette =
    [
        "#0089FF", // 1: Blue
        "#B45AF6", // 2: Purple
        "#10B981", // 3: Emerald
        "#F59E0B", // 4: Amber
        "#EC4899", // 5: Pink
        "#6366F1", // 6: Indigo
        "#14B8A6", // 7: Teal
        "#8B5CF6"  // 8: Violet
    ];

    private readonly List<string> _trackColors = ["#0089FF", "#B45AF6"];
    private readonly List<bool> _trackMuted = [false, false];
    private bool _isPlusHovered;
    private int _hoveredTrackHeader = -1;
    private double _trackVerticalOffset;
    private bool _isVScrollHovered;
    private double _vScrollDragOriginY;
    private double _vScrollOriginOffset;
    private ContextMenu? _activeContextMenu;
    private TopLevel? _contextMenuOwner;

    public double LaneTop => 32.0;
    public double ViewportTrackHeight => Math.Max(LaneHeight, Bounds.Height - LaneTop - 6);
    public double FixedTimelineHeight => LaneTop + VisibleTrackCapacity * LaneHeight + 6;
    public double WaveformTop => LaneTop;
    public double WaveformBottom => LaneTop + ViewportTrackHeight;
    public IReadOnlyList<int> SelectedCueIndexes => _selectedIndexes.Order().ToArray();

    public double TrackVerticalOffset
    {
        get => _trackVerticalOffset;
        set
        {
            var max = MaxTrackVerticalOffset;
            var clamped = Math.Clamp(value, 0, max);
            if (Math.Abs(_trackVerticalOffset - clamped) > 0.01)
            {
                _trackVerticalOffset = clamped;
                InvalidateVisual();
            }
        }
    }

    public double MaxTrackVerticalOffset
    {
        get
        {
            var totalTracksHeight = _trackCount * LaneHeight;
            return Math.Max(0, totalTracksHeight - ViewportTrackHeight);
        }
    }

    public int TrackCount
    {
        get => _trackCount;
        set
        {
            _trackCount = Math.Clamp(value, 2, 8);
            EnsureTrackLists();
            _cueIndexesDirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void EnsureTrackLists()
    {
        while (_trackColors.Count < _trackCount)
        {
            var nextColor = TrackPalette.FirstOrDefault(c => !_trackColors.Contains(c))
                ?? TrackPalette[_trackColors.Count % TrackPalette.Length];
            _trackColors.Add(nextColor);
        }
        while (_trackColors.Count > _trackCount)
        {
            _trackColors.RemoveAt(_trackColors.Count - 1);
        }
        while (_trackMuted.Count < _trackCount)
        {
            _trackMuted.Add(false);
        }
        while (_trackMuted.Count > _trackCount)
        {
            _trackMuted.RemoveAt(_trackMuted.Count - 1);
        }
    }

    private bool _showWaveform = true;
    public bool ShowWaveform
    {
        get => _showWaveform;
        set
        {
            if (_showWaveform == value) return;
            _showWaveform = value;
            InvalidateMeasure();
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
    }



    public bool IsTrackMuted(int track) =>
        track >= 0 && track < _trackMuted.Count && _trackMuted[track];

    public void ToggleTrackMuted(int track)
    {
        if (track < 0 || track >= _trackCount) return;
        EnsureTrackLists();
        _trackMuted[track] = !_trackMuted[track];
        TrackMutedChanged?.Invoke(this, (track, _trackMuted[track]));
        InvalidateVisual();
    }

    public void SetTrackMuted(int track, bool muted)
    {
        if (track < 0 || track >= _trackCount) return;
        EnsureTrackLists();
        _trackMuted[track] = muted;
        TrackMutedChanged?.Invoke(this, (track, muted));
        InvalidateVisual();
    }

    public string GetTrackColor(int track)
    {
        if (track >= 0 && track < _trackColors.Count) return _trackColors[track];
        return TrackPalette[Math.Abs(track) % TrackPalette.Length];
    }

    public string GetTrackFillColor(int track)
    {
        var hex = GetTrackColor(track).TrimStart('#');
        if (hex.Length == 6) return $"#44{hex}";
        return "#440089FF";
    }

    public double RequiredHeight => LaneTop + Math.Max(VisibleTrackCapacity, _trackCount) * LaneHeight + 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height)
            ? RequiredHeight
            : Math.Min(RequiredHeight, availableSize.Height);
        return new Size(width, Math.Max(0, height));
    }

    public void InsertTrackAtTop()
    {
        if (_trackCount >= 8) return;
        EnsureTrackLists();
        var nextColor = TrackPalette.FirstOrDefault(c => !_trackColors.Contains(c))
            ?? TrackPalette[_trackColors.Count % TrackPalette.Length];
        _trackColors.Insert(0, nextColor);
        _trackMuted.Insert(0, false);
        _trackCount = _trackColors.Count;

        var changes = new List<CueTimingChange>();
        foreach (var (cue, index) in _cues.Select((c, i) => (c, i)))
        {
            var oldTrack = cue.TrackIndex;
            var newTrack = Math.Min(7, oldTrack + 1);
            cue.TrackIndex = newTrack;
            changes.Add(new CueTimingChange(index, cue.StartMilliseconds, cue.EndMilliseconds,
                cue.StartMilliseconds, cue.EndMilliseconds, oldTrack, newTrack));
        }

        _cueIndexesDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
        TrackStructureChanged?.Invoke(this, (0, true));
        TrackCountChanged?.Invoke(this, _trackCount);
        if (changes.Count > 0) CueEdited?.Invoke(this, new TimelineCueEdit(changes));
    }

    public void InsertTrackAtBottom()
    {
        if (_trackCount >= 8) return;
        EnsureTrackLists();
        var nextColor = TrackPalette.FirstOrDefault(c => !_trackColors.Contains(c))
            ?? TrackPalette[_trackColors.Count % TrackPalette.Length];
        _trackColors.Add(nextColor);
        _trackMuted.Add(false);
        _trackCount = _trackColors.Count;

        _cueIndexesDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
        TrackStructureChanged?.Invoke(this, (_trackCount - 1, true));
        TrackCountChanged?.Invoke(this, _trackCount);
    }

    public void RemoveLastTrack()
    {
        RemoveTrackAt(_trackCount - 1);
    }

    public void RemoveTrackAt(int trackIndex)
    {
        // L1 and L2 are the two permanent base tracks. Only user-added tracks
        // (L3 and later) may be removed.
        if (_trackCount <= 2 || trackIndex < 2 || trackIndex >= _trackCount) return;
        EnsureTrackLists();
        // Give the owner a chance to remove cues that belong to this track.
        // Moving them to an adjacent track creates overlapping/"glued" subtitles.
        TrackRemoving?.Invoke(this, trackIndex);
        _selectedIndex = -1;
        _selectedIndexes.Clear();
        var changes = new List<CueTimingChange>();
        foreach (var (cue, index) in _cues.Select((c, i) => (c, i)))
        {
            var oldTrack = cue.TrackIndex;
            var newTrack = oldTrack > trackIndex ? oldTrack - 1 : oldTrack;
            if (newTrack == oldTrack) continue;
            cue.TrackIndex = newTrack;
            changes.Add(new CueTimingChange(index, cue.StartMilliseconds, cue.EndMilliseconds,
                cue.StartMilliseconds, cue.EndMilliseconds, oldTrack, newTrack));
        }

        _trackColors.RemoveAt(trackIndex);
        _trackMuted.RemoveAt(trackIndex);
        _trackCount = Math.Max(1, _trackColors.Count);
        _trackVerticalOffset = Math.Clamp(_trackVerticalOffset, 0, MaxTrackVerticalOffset);

        _cueIndexesDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
        TrackStructureChanged?.Invoke(this, (trackIndex, false));
        TrackCountChanged?.Invoke(this, _trackCount);
        if (changes.Count > 0) CueEdited?.Invoke(this, new TimelineCueEdit(changes));
    }

    public void ShowTrackMenu()
    {
        var menu = new ContextMenu();
        var topItem = new MenuItem
        {
            Header = "在顶部插入新轨道",
            Icon = new Material.Icons.Avalonia.MaterialIcon
            {
                Kind = Material.Icons.MaterialIconKind.ArrowUpCircleOutline,
                Width = 16,
                Height = 16,
                Foreground = Brush.Parse("#71717A")
            }
        };
        topItem.Click += (_, _) => InsertTrackAtTop();

        var bottomItem = new MenuItem
        {
            Header = "在底部插入新轨道",
            Icon = new Material.Icons.Avalonia.MaterialIcon
            {
                Kind = Material.Icons.MaterialIconKind.ArrowDownCircleOutline,
                Width = 16,
                Height = 16,
                Foreground = Brush.Parse("#71717A")
            }
        };
        bottomItem.Click += (_, _) => InsertTrackAtBottom();

        var removeItem = new MenuItem
        {
            Header = "移除最后一条轨道",
            IsEnabled = _trackCount > 2,
            Icon = new Material.Icons.Avalonia.MaterialIcon
            {
                Kind = Material.Icons.MaterialIconKind.DeleteOutline,
                Width = 16,
                Height = 16,
                Foreground = Brush.Parse(_trackCount > 2 ? "#EF4444" : "#A1A1AA")
            }
        };
        if (_trackCount > 2)
        {
            removeItem.Classes.Add("danger");
        }
        removeItem.Click += (_, _) => RemoveLastTrack();

        menu.Items.Add(topItem);
        menu.Items.Add(bottomItem);
        menu.Items.Add(removeItem);

        OpenTimelineContextMenu(menu);
    }

    public SubtitleTimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        EnsureTrackLists();
        _edgeAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeAutoScrollTimer.Tick += (_, _) => ApplyEdgeAutoScroll();
    }

    /// <summary>Marks timing/track indexes stale after the shared cue collection changes.</summary>
    public void NotifyCueDataChanged()
    {
        _cueIndexesDirty = true;
        InvalidateVisual();
    }

    private static SolidColorBrush CachedBrush(string color)
    {
        if (BrushCache.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush(Color.Parse(color));
        BrushCache[color] = brush;
        return brush;
    }

    private static Pen CachedPen(string color, double thickness = 1)
    {
        var key = (color, thickness);
        if (PenCache.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(CachedBrush(color), thickness);
        PenCache[key] = pen;
        return pen;
    }

    private void EnsureCueIndexes()
    {
        if (!_cueIndexesDirty && _cueTrackIndexes.Length == _trackCount) return;

        var tracks = new List<int>[_trackCount];
        for (var track = 0; track < tracks.Length; track++) tracks[track] = new List<int>();
        for (var index = 0; index < _cues.Count; index++)
        {
            var cue = _cues[index];
            if (cue == null) continue;
            var track = Math.Clamp(cue.TrackIndex, 0, _trackCount - 1);
            tracks[track].Add(index);
        }

        _cueTrackIndexes = new CueTrackIndex[_trackCount];
        var conflicts = new List<ConflictSpan>();
        var boundaries = new List<long>(_cues.Count * 2);
        for (var track = 0; track < tracks.Length; track++)
        {
            tracks[track].Sort((left, right) =>
            {
                if (left < 0 || left >= _cues.Count || right < 0 || right >= _cues.Count)
                    return left.CompareTo(right);
                var cLeft = _cues[left];
                var cRight = _cues[right];
                if (cLeft == null || cRight == null) return left.CompareTo(right);
                var comparison = cLeft.StartMilliseconds.CompareTo(cRight.StartMilliseconds);
                return comparison != 0 ? comparison : left.CompareTo(right);
            });
            var indexes = tracks[track].ToArray();
            var starts = new long[indexes.Length];
            var prefixMaximumEnds = new long[indexes.Length];
            var maximumEnd = long.MinValue;
            for (var position = 0; position < indexes.Length; position++)
            {
                var cueIdx = indexes[position];
                if (cueIdx < 0 || cueIdx >= _cues.Count) continue;
                var cue = _cues[cueIdx];
                if (cue == null) continue;
                boundaries.Add(cue.StartMilliseconds);
                if (cue.EndMilliseconds < long.MaxValue) boundaries.Add(cue.EndMilliseconds + 1);
                starts[position] = cue.StartMilliseconds;
                prefixMaximumEnds[position] = Math.Max(maximumEnd, cue.EndMilliseconds);
                if (position > 0 && cue.StartMilliseconds < maximumEnd)
                    conflicts.Add(new ConflictSpan(track, cue.StartMilliseconds,
                        Math.Min(cue.EndMilliseconds, maximumEnd)));
                maximumEnd = prefixMaximumEnds[position];
            }
            _cueTrackIndexes[track] = new CueTrackIndex
            {
                CueIndexes = indexes,
                Starts = starts,
                PrefixMaximumEnds = prefixMaximumEnds
            };
        }
        _conflictSpans = conflicts.ToArray();
        _cueBoundaries = boundaries.Distinct().OrderBy(value => value).ToArray();
        _cueIndexesDirty = false;
    }

    public ActiveCueSnapshot GetActiveCueSnapshot(long milliseconds)
    {
        EnsureCueIndexes();
        var active = new List<int>(_trackCount);
        for (var track = 0; track < _trackCount; track++)
            active.AddRange(EnumerateCuesInRange(track, milliseconds, milliseconds));
        active.Sort();

        var next = UpperBound(_cueBoundaries, milliseconds);
        var validFrom = next > 0 ? _cueBoundaries[next - 1] : long.MinValue;
        var validThrough = next < _cueBoundaries.Length ? _cueBoundaries[next] - 1 : long.MaxValue;
        return new ActiveCueSnapshot(active, validFrom, validThrough);
    }

    private static int UpperBound(long[] values, long target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int LowerBound(long[] values, long target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private IEnumerable<int> EnumerateCuesInRange(int track, long start, long end)
    {
        EnsureCueIndexes();
        if (track < 0 || track >= _cueTrackIndexes.Length) yield break;
        var index = _cueTrackIndexes[track];
        var stop = UpperBound(index.Starts, end);
        var begin = LowerBound(index.PrefixMaximumEnds, start);
        for (var position = begin; position < stop; position++)
        {
            var cueIndex = index.CueIndexes[position];
            var layout = GetCueLayout(cueIndex);
            if (layout.End >= start && layout.Start <= end) yield return cueIndex;
        }

        // A dragged cue may have moved outside its indexed base interval or track.
        foreach (var (cueIndex, preview) in _dragPreview)
        {
            if (preview.Track != track || preview.End < start || preview.Start > end) continue;
            if (!index.CueIndexes.Take(stop).Skip(begin).Contains(cueIndex)) yield return cueIndex;
        }
    }

    public double ViewStartSeconds => _viewStartSeconds;
    public double DurationSeconds => _durationSeconds;
    public double VisibleSeconds => Math.Max(1, Math.Max(0, Bounds.Width - TrackHeaderWidth) / _pixelsPerSecond);
    public double PositionSeconds => _positionSeconds;

    public bool SplitMode
    {
        get => _splitMode;
        set
        {
            if (_splitMode == value) return;
            _splitMode = value;
            _hoveredIndex = -1;
            _splitPreviewX = -1;
            ClearSplitPreview();
            Cursor = value ? EnsureSplitCursor() : new Cursor(StandardCursorType.Arrow);
            InvalidateVisual();
        }
    }

    private Cursor EnsureSplitCursor()
    {
        if (_splitCursor is not null) return _splitCursor;
        // 剪刀光标：取自 IconPacks.Avalonia MaterialDesign 的 content_cut (24x24)
        // 渲染到 32x32 位图后转为自定义光标
        const string path = "M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3z";
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(1.15, 1.15) * Matrix.CreateTranslation(2, 2)))
        {
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#2A3138")),
                new Pen(new SolidColorBrush(Colors.White), 1.2), Geometry.Parse(path));
        }
        _splitCursor = new Cursor(bitmap, new PixelPoint(16, 16));
        return _splitCursor;
    }

    public void ScrollTo(double viewStartSeconds)
    {
        var visible = VisibleSeconds;
        _viewStartSeconds = Math.Clamp(viewStartSeconds, 0, Math.Max(0, _durationSeconds - visible));
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDocument(IReadOnlyList<EditorSubtitleCue> cues, IReadOnlyList<float>? peaks, double durationSeconds)
    {
        _cues = cues;
        _peaks = peaks ?? Array.Empty<float>();
        _waveformDurationSeconds = _peaks.Count > 0 ? Math.Max(0.001, durationSeconds) : 0;
        _durationSeconds = Math.Max(1, durationSeconds);
        _selectedIndex = -1;
        _selectedIndexes.Clear();
        _viewStartSeconds = 0;
        _cueIndexesDirty = true;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetWaveform(IReadOnlyList<float> peaks, double durationSeconds)
    {
        _peaks = peaks;
        _waveformDurationSeconds = Math.Max(0.001, durationSeconds);
        _durationSeconds = Math.Max(_durationSeconds, durationSeconds);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetStyleGroups(SubtitleStyleDefinition main, SubtitleStyleDefinition secondary)
    {
        _mainStyle = main.Clone();
        _secondaryStyle = secondary.Clone();
        InvalidateVisual();
    }

    public void SetPosition(double seconds, bool keepVisible = true)
    {
        // mpv reports its previous position for a short time after a seek.  Do
        // not let those delayed reports pull the playhead away from the pointer.
        if (_dragMode == DragMode.Scrub && keepVisible) return;
        _positionSeconds = Math.Clamp(seconds, 0, _durationSeconds);
        var previousViewStart = _viewStartSeconds;
        if (keepVisible && _dragMode == DragMode.None)
        {
            var visibleSeconds = Math.Max(1, Bounds.Width / _pixelsPerSecond);
            if (_positionSeconds < _viewStartSeconds + visibleSeconds * .08 ||
                _positionSeconds > _viewStartSeconds + visibleSeconds * .88)
                _viewStartSeconds = Math.Clamp(_positionSeconds - visibleSeconds * .2, 0,
                    Math.Max(0, _durationSeconds - visibleSeconds));
        }
        InvalidateVisual();
        if (Math.Abs(previousViewStart - _viewStartSeconds) > .0001)
            ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelectedIndex(int index)
    {
        _selectedIndex = index;
        _selectedIndexes.Clear();
        if (index >= 0 && index < _cues.Count) _selectedIndexes.Add(index);
        if (index >= 0 && index < _cues.Count)
        {
            var cue = _cues[index];
            var visible = Math.Max(1, Bounds.Width / _pixelsPerSecond);
            var start = cue.StartMilliseconds / 1000d;
            if (start < _viewStartSeconds || start > _viewStartSeconds + visible)
                _viewStartSeconds = Math.Clamp(start - visible * .2, 0, Math.Max(0, _durationSeconds - visible));
        }
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAllCues()
    {
        _selectedIndexes.Clear();
        foreach (var index in Enumerable.Range(0, _cues.Count)) _selectedIndexes.Add(index);
        _selectedIndex = _cues.Count > 0 ? 0 : -1;
        InvalidateVisual();
        NotifySelectionChanged();
    }

    public void SelectTrackCues(int track)
    {
        _selectedIndexes.Clear();
        foreach (var (cue, index) in _cues.Select((cue, index) => (cue, index)))
            if (cue.TrackIndex == track) _selectedIndexes.Add(index);
        _selectedIndex = _selectedIndexes.Order().FirstOrDefault(-1);
        InvalidateVisual();
        NotifySelectionChanged();
    }

    public void SetSelectedIndexes(IEnumerable<int> indexes)
    {
        _selectedIndexes.Clear();
        foreach (var index in indexes)
            if (index >= 0 && index < _cues.Count) _selectedIndexes.Add(index);
        _selectedIndex = _selectedIndexes.Order().FirstOrDefault(-1);
        InvalidateVisual();
        NotifySelectionChanged();
    }

    public void SetZoom(double pixelsPerSecond)
    {
        var center = _viewStartSeconds + Bounds.Width / Math.Max(1, _pixelsPerSecond) / 2;
        _pixelsPerSecond = Math.Clamp(pixelsPerSecond, 30, 260);
        var visible = Bounds.Width / Math.Max(1, _pixelsPerSecond);
        _viewStartSeconds = Math.Clamp(center - visible / 2, 0, Math.Max(0, _durationSeconds - visible));
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh() => InvalidateVisual();

    public Rect GetCueVisualBounds(int index)
    {
        if (index < 0 || index >= _cues.Count) return default;
        var layout = GetCueLayout(index);
        var left = TimeToX(layout.Start / 1000d);
        var right = TimeToX(layout.End / 1000d);
        var track = Math.Clamp(layout.Track, 0, _trackCount - 1);
        return new Rect(left + 1, LaneTop + track * LaneHeight - _trackVerticalOffset + 3, Math.Max(19, right - left - 2), CueBlockHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        try
        {
            var bounds = Bounds;
            context.DrawRectangle(CachedBrush("#FFFFFF"), null, bounds);
            DrawRuler(context, bounds);

            using (context.PushClip(new Rect(0, LaneTop, bounds.Width, ViewportTrackHeight)))
            {
                DrawTrackRows(context, bounds);
                if (_showWaveform) DrawWaveform(context, bounds);
                DrawCueBlocks(context, bounds);
                DrawMarqueeSelection(context);
                if (_dragMode == DragMode.None) DrawConflictMarkers(context);
            }

            DrawPlayhead(context, bounds);
            DrawSplitModeOverlay(context, bounds);
            DrawVerticalScrollBar(context, bounds);
        }
        catch
        {
            // Safeguard against render loop crash
        }
    }

    private void DrawSplitModeOverlay(DrawingContext context, Rect bounds)
    {
        if (!_splitMode) return;
        if (_splitPreviewX >= 0)
            context.DrawLine(SplitGuidePen,
                new Point(_splitPreviewX, 0), new Point(_splitPreviewX, bounds.Height));
        var pill = new Rect(bounds.Width - 236, 5, 228, 18);
        context.DrawRectangle(CachedBrush("#CC1F77C8"), null, pill, 9, 9);
        DrawFixedText(context, "剪刀模式 · 移动预览 · 点击切分 · V 退出",
            new Point(pill.Left + 12, pill.Top + 3.5), 10, "#FFFFFF", pill.Width - 20);
    }

    private void DrawRuler(DrawingContext context, Rect bounds)
    {
        context.DrawRectangle(CachedBrush("#F7F8FA"), null, new Rect(0, 0, bounds.Width, 30));

        // Left header with plus button
        context.DrawRectangle(CachedBrush("#F1F3F5"), null, new Rect(0, 0, TrackHeaderWidth, 30));
        context.DrawLine(CachedPen("#E1E5EA"),
            new Point(TrackHeaderWidth, 0), new Point(TrackHeaderWidth, 30));

        var plusRect = new Rect(4, 4, 24, 22);
        if (_isPlusHovered)
        {
            context.DrawRectangle(CachedBrush("#E2E6EA"), null, plusRect, 4, 4);
        }
        var plusPen = CachedPen(_isPlusHovered ? "#1F252C" : "#55606E", 1.8);
        context.DrawLine(plusPen, new Point(10, 15), new Point(22, 15));
        context.DrawLine(plusPen, new Point(16, 9), new Point(16, 21));

        // Ruler ticks and labels
        var minor = _pixelsPerSecond >= 100 ? .5 : 1d;
        var first = Math.Floor(_viewStartSeconds / minor) * minor;
        var pen = CachedPen("#C8CDD4");
        for (var t = first; ; t += minor)
        {
            var x = TimeToX(t);
            if (x > bounds.Width) break;
            if (x < TrackHeaderWidth) continue;
            var major = Math.Abs(t % 5) < .001;
            context.DrawLine(pen, new Point(x, major ? 13 : 20), new Point(x, 30));
            if (!major) continue;
            var label = TimeSpan.FromSeconds(Math.Max(0, t)).ToString(@"mm\:ss");
            DrawFixedText(context, label, new Point(x + 4, 3), 10, "#68717D", 54);
        }
    }

    private void DrawWaveform(DrawingContext context, Rect bounds)
    {
        if (!_showWaveform) return;
        var top = LaneTop;
        var bottom = LaneTop + ViewportTrackHeight;
        var middle = (top + bottom) / 2;
        var halfHeight = (bottom - top) * 0.44;

        var rightLimit = bounds.Width - (MaxTrackVerticalOffset > 0 ? VerticalScrollBarWidth : 0);

        if (_peaks.Count == 0)
        {
            return;
        }

        var pen = CachedPen("#607C94A8", 1.6);
        for (var x = (int)TrackHeaderWidth; x < (int)rightLimit; x += 2)
        {
            var startTime = XToTime(x);
            if (startTime >= _waveformDurationSeconds) break;
            var endTime = Math.Min(_waveformDurationSeconds, XToTime(x + 2));
            var firstSample = (int)Math.Clamp(Math.Floor(startTime / _waveformDurationSeconds * _peaks.Count), 0, _peaks.Count - 1);
            var lastSample = (int)Math.Clamp(Math.Ceiling(endTime / _waveformDurationSeconds * _peaks.Count) - 1, firstSample, _peaks.Count - 1);
            var peak = 0f;
            for (var sample = firstSample; sample <= lastSample; sample++)
                peak = Math.Max(peak, _peaks[sample]);
            var amplitude = Math.Sqrt(Math.Clamp(peak, 0, 1)) * halfHeight;
            context.DrawLine(pen, new Point(x, middle - amplitude), new Point(x, middle + amplitude));
        }
    }

    private void DrawTrackRows(DrawingContext context, Rect bounds)
    {
        var scrollBarWidth = MaxTrackVerticalOffset > 0 ? VerticalScrollBarWidth : 0;
        var width = Math.Max(0, bounds.Width - TrackHeaderWidth - scrollBarWidth);
        var dividerPen = CachedPen("#EAECEF");

        // 1. Draw active track rows and their divider lines
        for (var track = 0; track < _trackCount; track++)
        {
            var top = LaneTop + track * LaneHeight - _trackVerticalOffset;
            if (top + LaneHeight < LaneTop || top > LaneTop + ViewportTrackHeight) continue;

            var isMuted = IsTrackMuted(track);
            var isHeaderHovered = _hoveredTrackHeader == track;

            // Track Header (clean left column with clickable colored Dot)
            var headerRect = new Rect(0, top, TrackHeaderWidth, LaneHeight);
            context.DrawRectangle(CachedBrush(isMuted ? (isHeaderHovered ? "#E2E4E8" : "#EBECEF") : (isHeaderHovered ? "#E6ECF1" : "#F4F6F8")),
                CachedPen("#E2E6EA"), headerRect);

            // Persistent colored track capsule (also serves as the mute/open target).
            var dotColor = GetTrackColor(track);
            var capsuleRect = new Rect(3, top + 4, TrackHeaderWidth - 6, LaneHeight - 8);
            var capsuleFill = isMuted ? "#C8CDD3" : GetTrackFillColor(track);
            context.DrawRectangle(CachedBrush(capsuleFill),
                isHeaderHovered ? CachedPen("#80FFFFFF") : null,
                capsuleRect, 4, 4);
            DrawCenteredText(context, $"L{track + 1}", capsuleRect, 10, "#FFFFFF");

            // Track Row Divider Line (Bottom of row)
            context.DrawLine(dividerPen, new Point(TrackHeaderWidth, top + LaneHeight),
                new Point(TrackHeaderWidth + width, top + LaneHeight));

            // Track Row Body (Translucent highlight if selected or muted)
            var bodyRect = new Rect(TrackHeaderWidth, top, width, LaneHeight);
            if (_selectedIndex >= 0 && _selectedIndex < _cues.Count && GetCueLayout(_selectedIndex).Track == track)
            {
                context.DrawRectangle(CachedBrush("#203399F3"), null, bodyRect);
            }

            if (isMuted)
            {
                context.DrawRectangle(CachedBrush("#20000000"), null, bodyRect);
                DrawText(context, "← 此轨道已关闭", new Point(TrackHeaderWidth + 14, top + 7), 10.5, "#D9822B", 180);
            }
        }

        // 2. Fill any unused visible rows with quiet placeholders.
        var visibleSlotCapacity = Math.Max(1, (int)Math.Ceiling(ViewportTrackHeight / LaneHeight));
        for (var slot = _trackCount; slot < visibleSlotCapacity; slot++)
        {
            var top = LaneTop + slot * LaneHeight - _trackVerticalOffset;
            if (top + LaneHeight < LaneTop || top > LaneTop + ViewportTrackHeight) continue;

            var emptyHeaderRect = new Rect(0, top, TrackHeaderWidth, LaneHeight);
            context.DrawRectangle(CachedBrush("#F6F7F9"),
                CachedPen("#EBECEF"), emptyHeaderRect);

            context.DrawLine(dividerPen, new Point(TrackHeaderWidth, top + LaneHeight),
                new Point(TrackHeaderWidth + width, top + LaneHeight));
        }
    }

    private void DrawCueBlocks(DrawingContext context, Rect bounds)
    {
        var visibleStart = (long)Math.Floor(_viewStartSeconds * 1000);
        var visibleEnd = (long)Math.Ceiling((_viewStartSeconds + VisibleSeconds) * 1000);
        for (var track = 0; track < _trackCount; track++)
        {
          foreach (var i in EnumerateCuesInRange(track, visibleStart, visibleEnd))
          {
            if (i < 0 || i >= _cues.Count) continue;
            var cue = _cues[i];
            if (cue == null) continue;
            var layout = GetCueLayout(i);
            var left = TimeToX(layout.Start / 1000d);
            var right = TimeToX(layout.End / 1000d);
            if (right < TrackHeaderWidth || left > bounds.Width) continue;
            var layoutTrack = Math.Clamp(layout.Track, 0, _trackCount - 1);
            var top = LaneTop + layoutTrack * LaneHeight - _trackVerticalOffset;
            if (top + LaneHeight < LaneTop || top > LaneTop + ViewportTrackHeight) continue;

            var isMuted = IsTrackMuted(layoutTrack);
            var rect = new Rect(Math.Max(TrackHeaderWidth + 1, left + 1), top + 3,
                Math.Max(19, right - Math.Max(TrackHeaderWidth, left) - 2), CueBlockHeight);
            var selected = _selectedIndexes.Contains(i);
            var hovered = i == _hoveredIndex;
            var fillColor = isMuted ? "#1F9E9E9E" : GetTrackFillColor(layoutTrack);
            var strokeColor = isMuted ? "#809E9E9E" : GetTrackColor(layoutTrack);
            context.DrawRectangle(CachedBrush(fillColor),
                CachedPen(strokeColor, 1.5), rect, 4, 4);
            if (hovered && !isMuted)
            {
                context.DrawRectangle(CachedBrush(strokeColor), null,
                    new Rect(rect.Left + 1, rect.Top + 1, 8, rect.Height - 2), 2, 2);
                context.DrawRectangle(CachedBrush(strokeColor), null,
                    new Rect(rect.Right - 9, rect.Top + 1, 8, rect.Height - 2), 2, 2);
            }
            var text = string.IsNullOrWhiteSpace(cue.Translated) ? cue.Original : cue.Translated;
            var playing = _positionSeconds * 1000 >= layout.Start && _positionSeconds * 1000 <= layout.End;
            DrawText(context, text.Replace('\n', ' '), new Point(rect.Left + 10, rect.Top + 7), 10.5,
                isMuted ? "#808080" : (playing ? strokeColor : "#FFFFFF"), Math.Max(0, rect.Width - 15));
            if (selected)
                context.DrawRectangle(null, CachedPen("#FF6B2B", 2.5),
                    new Rect(rect.Left + 0.5, rect.Top + 0.5, Math.Max(1, rect.Width - 1), rect.Height - 1), 4, 4);
          }
        }
        
        if (_snapGuideMilliseconds is { } snap)
        {
            var x = TimeToX(snap / 1000d);
            if (x >= TrackHeaderWidth)
            {
                context.DrawLine(CachedPen("#F0A43A"),
                    new Point(x, LaneTop), new Point(x, LaneTop + ViewportTrackHeight));
            }
        }
    }

    private void DrawMarqueeSelection(DrawingContext context)
    {
        if (_dragMode != DragMode.Marquee) return;
        var rect = GetMarqueeRect();
        context.DrawRectangle(CachedBrush("#243399F3"), CachedPen("#3399F3", 1.4), rect, 3, 3);
    }

    private void DrawConflictMarkers(DrawingContext context)
    {
        EnsureCueIndexes();
        var warning = CachedBrush("#FCAF1A");
        var pen = CachedPen("#FCAF1A", 2);
        var visibleStart = (long)Math.Floor(_viewStartSeconds * 1000);
        var visibleEnd = (long)Math.Ceiling((_viewStartSeconds + VisibleSeconds) * 1000);
        foreach (var conflict in _conflictSpans)
        {
            if (conflict.End < visibleStart || conflict.Start > visibleEnd) continue;
            var top = LaneTop + conflict.Track * LaneHeight - _trackVerticalOffset;
            if (top + LaneHeight < LaneTop || top > LaneTop + ViewportTrackHeight) continue;
            var x1 = Math.Max(TrackHeaderWidth, TimeToX(conflict.Start / 1000d));
            var x2 = Math.Min(Bounds.Width, TimeToX(conflict.End / 1000d));
            if (x2 < TrackHeaderWidth || x1 > Bounds.Width) continue;
            context.DrawLine(pen, new Point(x1, top), new Point(x1, top + CueBlockHeight));
            context.DrawLine(pen, new Point(x1, top + 2), new Point(x2, top + 2));
            DrawFixedText(context, "!", new Point(x1 + 3, top + 5), 11, "#FCAF1A", 12);
        }
    }

    private Rect GetVerticalScrollBarThumbRect(Rect bounds)
    {
        var maxOffset = MaxTrackVerticalOffset;
        if (maxOffset <= 0) return default;
        var barX = bounds.Width - 10;
        var barY = LaneTop + 3;
        var barHeight = bounds.Height - LaneTop - 6;
        if (barHeight <= 10) return default;

        var visibleTrackHeight = bounds.Height - LaneTop;
        var totalTracksHeight = _trackCount * LaneHeight;
        var thumbHeight = Math.Clamp(barHeight * (visibleTrackHeight / totalTracksHeight), 22, barHeight - 4);
        var scrollRatio = Math.Clamp(_trackVerticalOffset / maxOffset, 0, 1);
        var thumbY = barY + scrollRatio * (barHeight - thumbHeight);
        return new Rect(barX, thumbY, 6, thumbHeight);
    }

    private void DrawVerticalScrollBar(DrawingContext context, Rect bounds)
    {
        if (MaxTrackVerticalOffset <= 0) return;
        var barX = bounds.Width - 11;
        var barY = LaneTop + 3;
        var barHeight = bounds.Height - LaneTop - 6;
        if (barHeight <= 10) return;

        // Subtle capsule track background
        context.DrawRectangle(CachedBrush("#10000000"), null,
            new Rect(barX - 1, barY, 8, barHeight), 4, 4);

        // Rounded thumb
        var thumbRect = GetVerticalScrollBarThumbRect(bounds);
        if (thumbRect != default)
        {
            var thumbBrush = CachedBrush(_isVScrollHovered || _dragMode == DragMode.VScroll ? "#7A8796" : "#B4BDC7");
            context.DrawRectangle(thumbBrush, null, thumbRect, 3, 3);
        }
    }

    private void DrawPlayhead(DrawingContext context, Rect bounds)
    {
        var x = TimeToX(_positionSeconds);
        if (x < TrackHeaderWidth || x > bounds.Width) return;
        var brush = CachedBrush("#FFCB57");
        context.DrawLine(CachedPen("#FFCB57", 1.5), new Point(x, 0), new Point(x, LaneTop + ViewportTrackHeight));
        using (context.PushTransform(Matrix.CreateTranslation(x, 0)))
        {
            context.DrawGeometry(brush, null, PlayheadMarkerGeometry);
        }
    }

    private void DrawText(DrawingContext context, string text, Point point, double size, string color, double width)
    {
        if (width <= 1) return;
        var layout = GetTextLayout(text, size, color, width, centered: false);
        layout.Draw(context, point);
    }

    private void DrawFixedText(DrawingContext context, string text, Point point, double size, string color, double width)
    {
        if (width <= 1) return;
        var layout = GetTextLayout(text, size, color, width, centered: false);
        layout.Draw(context, point);
    }

    private void DrawCenteredText(DrawingContext context, string text, Rect bounds, double size, string color)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        var layout = GetTextLayout(text, size, color, bounds.Width, centered: true);
        layout.Draw(context, new Point(bounds.Left, bounds.Top + Math.Max(0, (bounds.Height - layout.Height) / 2)));
    }

    private TextLayout GetTextLayout(string text, double size, string color, double width, bool centered)
    {
        var key = (text, size, color, width, centered);
        if (!_textLayouts.TryGetValue(key, out var layout))
        {
            if (_textLayouts.Count >= TextLayoutCacheLimit) _textLayouts.Clear();
            layout = new TextLayout(text, centered ? TimelineSemiboldTypeface : TimelineTypeface, size,
                CachedBrush(color), centered ? TextAlignment.Center : TextAlignment.Left,
                TextWrapping.NoWrap, centered ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                maxWidth: width, maxHeight: size + 5);
            _textLayouts[key] = layout;
        }
        return layout;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var currentPoint = e.GetCurrentPoint(this);
        if (currentPoint.Properties.IsRightButtonPressed)
        {
            Focus();
            var rightClickPoint = e.GetPosition(this);
            if (rightClickPoint.X <= TrackHeaderWidth && rightClickPoint.Y <= 30)
            {
                ShowTrackMenu();
            }
            else if (rightClickPoint.X <= TrackHeaderWidth && rightClickPoint.Y >= LaneTop)
            {
                var trackIndex = (int)Math.Floor((rightClickPoint.Y + _trackVerticalOffset - LaneTop) / LaneHeight);
                if (trackIndex >= 0 && trackIndex < _trackCount) ShowTrackHeaderContextMenu(trackIndex);
            }
            else
            {
                ShowCueContextMenu(rightClickPoint);
            }
            e.Handled = true;
            return;
        }
        if (!currentPoint.Properties.IsLeftButtonPressed) return;
        CloseActiveContextMenu();
        Focus();
        var point = e.GetPosition(this);

        // Click on Vertical ScrollBar on the right
        if (MaxTrackVerticalOffset > 0 && point.X >= Bounds.Width - 14 && point.Y >= LaneTop && point.Y <= LaneTop + ViewportTrackHeight)
        {
            var thumb = GetVerticalScrollBarThumbRect(Bounds);
            if (thumb != default && thumb.Contains(point))
            {
                _dragMode = DragMode.VScroll;
                _vScrollDragOriginY = point.Y;
                _vScrollOriginOffset = _trackVerticalOffset;
                e.Pointer.Capture(this);
            }
            else
            {
                var barY = LaneTop + 3;
                var barHeight = ViewportTrackHeight - 6;
                var clickRatio = Math.Clamp((point.Y - barY) / barHeight, 0, 1);
                TrackVerticalOffset = clickRatio * MaxTrackVerticalOffset;
            }
            e.Handled = true;
            return;
        }

        // Clicked Plus Button on Header
        if (point.X <= TrackHeaderWidth && point.Y <= 30)
        {
            InsertTrackAtBottom();
            e.Handled = true;
            return;
        }

        // Clicked Track Header / Colored Dot to toggle closed/open
        if (point.X <= TrackHeaderWidth && point.Y >= LaneTop && point.Y <= Bounds.Height)
        {
            var trackIndex = (int)Math.Floor((point.Y + _trackVerticalOffset - LaneTop) / LaneHeight);
            if (trackIndex >= 0 && trackIndex < _trackCount)
            {
                ToggleTrackMuted(trackIndex);
                e.Handled = true;
                return;
            }
        }

        if (point.X < TrackHeaderWidth)
        {
            e.Handled = true;
            return;
        }

        // The ruler is reserved for scrubbing. Marquee selection only starts
        // inside the subtitle lanes below it.
        if (point.Y < LaneTop)
        {
            _dragMode = DragMode.Scrub;
            _lastScrubDispatchTimestamp = 0;
            e.Pointer.Capture(this);
            UpdateScrub(point.X, forceDispatch: true);
            e.Handled = true;
            return;
        }

        var hit = point.Y >= LaneTop - 4 ? HitCue(point) : -1;
        if (_splitMode)
        {
            // 剪刀模式：点击字幕块即在点击位置切分；空白处不响应
            if (hit >= 0)
            {
                CueSplitRequested?.Invoke(this, (hit, (long)Math.Round(XToTime(point.X) * 1000)));
                ClearSplitPreview();
            }
            e.Handled = true;
            return;
        }
        if (hit < 0)
        {
            _marqueeOrigin = point;
            _marqueeCurrent = point;
            _marqueeModifiers = e.KeyModifiers;
            _marqueeHasDragged = false;
            e.Pointer.Capture(this);
            BeginMarqueeSelection();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_selectedIndexes.Add(hit)) _selectedIndexes.Remove(hit);
            _selectedIndex = _selectedIndexes.Contains(hit) ? hit : _selectedIndexes.FirstOrDefault(-1);
            SelectionChanged?.Invoke(this, _selectedIndexes.Order().ToArray());
            SelectedCueChanged?.Invoke(this, _selectedIndex);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        else if (!_selectedIndexes.Contains(hit) || _selectedIndexes.Count <= 1)
        {
            _selectedIndex = hit;
            _selectedIndexes.Clear();
            _selectedIndexes.Add(hit);
        }
        else
        {
            _selectedIndex = hit;
        }
        SelectionChanged?.Invoke(this, _selectedIndexes.Order().ToArray());
        SelectedCueChanged?.Invoke(this, hit);
        if (e.ClickCount >= 2)
        {
            CueEditRequested?.Invoke(this, hit);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var cue = _cues[hit];
        _dragOrigin = point;
        _dragOriginViewStartSeconds = _viewStartSeconds;
        _dragSnapshot.Clear();
        var left = TimeToX(cue.StartMilliseconds / 1000d);
        var right = TimeToX(cue.EndMilliseconds / 1000d);
        _dragMode = Math.Abs(point.X - left) <= 8 ? DragMode.TrimStart :
            Math.Abs(point.X - right) <= 8 ? DragMode.TrimEnd : DragMode.Move;
        var movingGroup = _dragMode == DragMode.Move && _selectedIndexes.Count > 1
            ? _cues.Select((item, index) => (item, index)).Where(pair => _selectedIndexes.Contains(pair.index))
            : _cues.Select((item, index) => (item, index)).Where(pair => pair.index == hit);
        _dragSnapshot.AddRange(movingGroup.Select(pair =>
            (pair.index, pair.item.StartMilliseconds, pair.item.EndMilliseconds, pair.item.TrackIndex)));
        _dragPreview.Clear();
        foreach (var item in _dragSnapshot)
            _dragPreview[item.Index] = (item.Start, item.End, item.Track);
        var movingIndexes = _dragSnapshot.Select(item => item.Index).ToHashSet();
        _dragSnapTargets = _cues.Where((_, index) => !movingIndexes.Contains(index))
            .SelectMany(item => new[] { item.StartMilliseconds, item.EndMilliseconds })
            .Append((long)Math.Round(_positionSeconds * 1000))
            .Distinct().OrderBy(value => value).ToArray();
        _snapGuideMilliseconds = null;
        e.Pointer.Capture(this);
        CueInteractionStarted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    private void ShowTrackHeaderContextMenu(int trackIndex)
    {
        var menu = new ContextMenu { MinWidth = 178, FontSize = 12.5 };
        var selectTrack = CreateTimelineMenuItem(
            $"全选当前轨道 (L{trackIndex + 1})", Material.Icons.MaterialIconKind.SelectAll);
        selectTrack.IsEnabled = _cues.Any(cue => cue.TrackIndex == trackIndex);
        selectTrack.Click += (_, _) => SelectTrackCues(trackIndex);
        var deleteTrack = CreateTimelineMenuItem(
            $"删除 L{trackIndex + 1} 轨道", Material.Icons.MaterialIconKind.DeleteOutline, destructive: true);
        deleteTrack.IsEnabled = trackIndex >= 2 && _trackCount > 2;
        deleteTrack.Click += (_, _) => RemoveTrackAt(trackIndex);
        menu.Items.Add(selectTrack);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteTrack);
        OpenTimelineContextMenu(menu);
    }

    private void ShowCueContextMenu(Point point)
    {
        var hit = point.Y >= LaneTop - 4 ? HitCue(point) : -1;
        var menu = new ContextMenu { MinWidth = 188, FontSize = 12.5 };
        if (hit >= 0)
        {
            if (!_selectedIndexes.Contains(hit))
            {
                _selectedIndex = hit;
                _selectedIndexes.Clear();
                _selectedIndexes.Add(hit);
                SelectionChanged?.Invoke(this, [hit]);
                SelectedCueChanged?.Invoke(this, hit);
                InvalidateVisual();
            }

            var edit = CreateTimelineMenuItem("编辑当前字幕块", Material.Icons.MaterialIconKind.PencilOutline);
            edit.Click += (_, _) => CueEditRequested?.Invoke(this, hit);
            var split = CreateTimelineMenuItem("在此处分割字幕块", Material.Icons.MaterialIconKind.ContentCut);
            var splitPosition = (long)Math.Round(XToTime(point.X) * 1000);
            split.Click += (_, _) => CueSplitRequested?.Invoke(this, (hit, splitPosition));
            var track = Math.Clamp(_cues[hit].TrackIndex, 0, _trackCount - 1);
            var selectTrack = CreateTimelineMenuItem(
                $"全选当前轨道 (L{track + 1})", Material.Icons.MaterialIconKind.SelectAll);
            selectTrack.Click += (_, _) => SelectTrackCues(track);
            var selectedForDeletion = _selectedIndexes.Order().ToArray();
            var delete = CreateTimelineMenuItem(
                selectedForDeletion.Length > 1 ? $"删除已选 {selectedForDeletion.Length} 个字幕块" : "删除当前字幕块",
                Material.Icons.MaterialIconKind.DeleteOutline, destructive: true);
            delete.Click += (_, _) =>
            {
                if (selectedForDeletion.Length > 1) CuesDeleteRequested?.Invoke(this, selectedForDeletion);
                else CueDeleteRequested?.Invoke(this, hit);
            };
            menu.Items.Add(edit);
            menu.Items.Add(split);
            menu.Items.Add(selectTrack);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
        }
        else
        {
            var position = (long)Math.Round(XToTime(Math.Max(TrackHeaderWidth, point.X)) * 1000);
            var track = point.Y >= LaneTop
                ? Math.Clamp((int)Math.Floor((point.Y + _trackVerticalOffset - LaneTop) / LaneHeight), 0, _trackCount - 1)
                : 0;
            var insert = CreateTimelineMenuItem("在此处插入字幕块", Material.Icons.MaterialIconKind.PlusBoxOutline);
            insert.Click += (_, _) => CueInsertRequested?.Invoke(this, (position, track));
            var selectAll = CreateTimelineMenuItem("全选字幕块", Material.Icons.MaterialIconKind.SelectAll);
            selectAll.IsEnabled = _cues.Count > 0;
            selectAll.Click += (_, _) =>
            {
                SelectAllCues();
                SelectAllRequested?.Invoke(this, EventArgs.Empty);
            };
            var selectTrack = CreateTimelineMenuItem(
                $"全选当前轨道 (L{track + 1})", Material.Icons.MaterialIconKind.SelectAll);
            selectTrack.IsEnabled = _cues.Any(cue => cue.TrackIndex == track);
            selectTrack.Click += (_, _) => SelectTrackCues(track);
            menu.Items.Add(insert);
            menu.Items.Add(new Separator());
            menu.Items.Add(selectTrack);
            menu.Items.Add(selectAll);
        }
        OpenTimelineContextMenu(menu);
    }

    private void OpenTimelineContextMenu(ContextMenu menu)
    {
        CloseActiveContextMenu();
        _activeContextMenu = menu;
        _contextMenuOwner = TopLevel.GetTopLevel(this);
        _contextMenuOwner?.AddHandler(PointerPressedEvent, DismissContextMenuOnOwnerClick,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Closed += (_, _) => ClearActiveContextMenu(menu);
        menu.Open(this);
    }

    private void DismissContextMenuOnOwnerClick(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            CloseActiveContextMenu();
    }

    private void CloseActiveContextMenu() => _activeContextMenu?.Close();

    private void ClearActiveContextMenu(ContextMenu menu)
    {
        if (!ReferenceEquals(_activeContextMenu, menu)) return;
        _contextMenuOwner?.RemoveHandler(PointerPressedEvent, DismissContextMenuOnOwnerClick);
        _contextMenuOwner = null;
        _activeContextMenu = null;
    }

    private static MenuItem CreateTimelineMenuItem(
        string header, Material.Icons.MaterialIconKind iconKind, bool destructive = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new Material.Icons.Avalonia.MaterialIcon
            {
                Kind = iconKind,
                Width = 16,
                Height = 16,
                Foreground = Brush.Parse(destructive ? "#E5484D" : "#71717A")
            }
        };
        if (destructive) item.Classes.Add("danger");
        return item;
    }

    private void BeginMarqueeSelection()
    {
        _dragMode = DragMode.Marquee;
        _marqueeOriginTimeSeconds = XToTime(_marqueeOrigin.X);
        _marqueeOriginContentY = _marqueeOrigin.Y + _trackVerticalOffset;
        _marqueeBaseSelection.Clear();
        if (_marqueeModifiers.HasFlag(KeyModifiers.Control))
            _marqueeBaseSelection.UnionWith(_selectedIndexes);
        else
            _selectedIndexes.Clear();
        UpdateMarqueeSelection(_marqueeOrigin);
    }

    private void StartScrub(Point point)
    {
        _selectedIndex = -1;
        _selectedIndexes.Clear();
        SelectionChanged?.Invoke(this, Array.Empty<int>());
        SelectedCueChanged?.Invoke(this, -1);
        _dragMode = DragMode.Scrub;
        _lastScrubDispatchTimestamp = 0;
        UpdateScrub(point.X, forceDispatch: true);
        InvalidateVisual();
    }

    private void UpdateMarqueeSelection(Point point)
    {
        _marqueeCurrent = point;
        var currentTime = XToTime(Math.Clamp(point.X, TrackHeaderWidth, Bounds.Width));
        var startMilliseconds = (long)Math.Floor(Math.Min(_marqueeOriginTimeSeconds, currentTime) * 1000);
        var endMilliseconds = (long)Math.Ceiling(Math.Max(_marqueeOriginTimeSeconds, currentTime) * 1000);
        var currentContentY = point.Y + _trackVerticalOffset;
        var selectionTop = Math.Min(_marqueeOriginContentY, currentContentY);
        var selectionBottom = Math.Max(_marqueeOriginContentY, currentContentY);

        _selectedIndexes.Clear();
        _selectedIndexes.UnionWith(_marqueeBaseSelection);
        for (var index = 0; index < _cues.Count; index++)
        {
            var cue = _cues[index];
            var cueTop = LaneTop + Math.Clamp(cue.TrackIndex, 0, _trackCount - 1) * LaneHeight;
            var cueBottom = cueTop + LaneHeight;
            if (cue.EndMilliseconds >= startMilliseconds && cue.StartMilliseconds <= endMilliseconds &&
                cueBottom >= selectionTop && cueTop <= selectionBottom)
                _selectedIndexes.Add(index);
        }
        _selectedIndex = _selectedIndexes.Order().FirstOrDefault(-1);
        InvalidateVisual();
    }

    private Rect GetMarqueeRect()
    {
        var currentTime = XToTime(Math.Clamp(_marqueeCurrent.X, TrackHeaderWidth, Bounds.Width));
        var x1 = TimeToX(_marqueeOriginTimeSeconds);
        var x2 = TimeToX(currentTime);
        var y1 = _marqueeOriginContentY - _trackVerticalOffset;
        var y2 = _marqueeCurrent.Y;
        var left = Math.Clamp(Math.Min(x1, x2), TrackHeaderWidth, Bounds.Width);
        var right = Math.Clamp(Math.Max(x1, x2), TrackHeaderWidth, Bounds.Width);
        var top = Math.Clamp(Math.Min(y1, y2), LaneTop, LaneTop + ViewportTrackHeight);
        var bottom = Math.Clamp(Math.Max(y1, y2), LaneTop, LaneTop + ViewportTrackHeight);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void NotifySelectionChanged()
    {
        var selected = _selectedIndexes.Order().ToArray();
        SelectionChanged?.Invoke(this, selected);
        SelectedCueChanged?.Invoke(this, selected.FirstOrDefault(-1));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_dragMode == DragMode.Marquee)
        {
            var delta = point - _marqueeOrigin;
            if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) >= 2)
                _marqueeHasDragged = true;
            UpdateEdgeAutoScroll(point, e.KeyModifiers);
            UpdateMarqueeSelection(point);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.VScroll)
        {
            var barHeight = ViewportTrackHeight - 6;
            var visibleTrackHeight = ViewportTrackHeight;
            var totalTracksHeight = _trackCount * LaneHeight;
            var thumbHeight = Math.Clamp(barHeight * (visibleTrackHeight / totalTracksHeight), 24, barHeight - 4);
            var trackTravel = Math.Max(1, barHeight - thumbHeight);
            var deltaY = point.Y - _vScrollDragOriginY;
            var offsetDelta = (deltaY / trackTravel) * MaxTrackVerticalOffset;
            TrackVerticalOffset = _vScrollOriginOffset + offsetDelta;
            e.Handled = true;
            return;
        }

        if (MaxTrackVerticalOffset > 0 && point.X >= Bounds.Width - 14 && point.Y >= LaneTop)
        {
            if (!_isVScrollHovered)
            {
                _isVScrollHovered = true;
                InvalidateVisual();
            }
        }
        else if (_isVScrollHovered)
        {
            _isVScrollHovered = false;
            InvalidateVisual();
        }

        if (point.X <= TrackHeaderWidth)
        {
            var wasPlus = _isPlusHovered;
            var wasTrack = _hoveredTrackHeader;
            _isPlusHovered = point.Y <= 30;
            _hoveredTrackHeader = point.Y >= LaneTop && point.Y <= Bounds.Height
                ? (int)Math.Floor((point.Y + _trackVerticalOffset - LaneTop) / LaneHeight)
                : -1;
            if (_hoveredTrackHeader >= _trackCount) _hoveredTrackHeader = -1;
            if (wasPlus != _isPlusHovered || wasTrack != _hoveredTrackHeader) InvalidateVisual();
            Cursor = (_isPlusHovered || _hoveredTrackHeader >= 0)
                ? new Cursor(StandardCursorType.Hand)
                : new Cursor(StandardCursorType.Arrow);
            if (_dragMode == DragMode.None) return;
        }
        else
        {
            if (_isPlusHovered || _hoveredTrackHeader >= 0)
            {
                _isPlusHovered = false;
                _hoveredTrackHeader = -1;
                InvalidateVisual();
            }
        }

        if (_splitMode && _dragMode == DragMode.None)
        {
            // 剪刀模式：跟随指针画切分参考线并高亮目标块，光标保持剪刀
            _splitPreviewX = point.X;
            var splitHit = point.Y >= LaneTop - 4 ? HitCue(point) : -1;
            if (_hoveredIndex != splitHit) _hoveredIndex = splitHit;
            if (splitHit >= 0)
            {
                var splitMilliseconds = (long)Math.Round(XToTime(point.X) * 1000);
                if (_splitPreviewIndex != splitHit || _splitPreviewMilliseconds != splitMilliseconds)
                {
                    _splitPreviewIndex = splitHit;
                    _splitPreviewMilliseconds = splitMilliseconds;
                    SplitPreviewChanged?.Invoke(this, (splitHit, splitMilliseconds));
                }
            }
            else
            {
                ClearSplitPreview();
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.Scrub)
        {
            UpdateEdgeAutoScroll(point, e.KeyModifiers);
            UpdateScrub(point.X, forceDispatch: false);
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.None)
        {
            var hoverPoint = point;
            var hit = hoverPoint.Y >= LaneTop - 4 ? HitCue(hoverPoint) : -1;
            if (_hoveredIndex != hit)
            {
                _hoveredIndex = hit;
                InvalidateVisual();
            }
            if (hit >= 0)
            {
                var cue = _cues[hit];
                var left = TimeToX(cue.StartMilliseconds / 1000d);
                var right = TimeToX(cue.EndMilliseconds / 1000d);
                Cursor = Math.Abs(hoverPoint.X - left) <= 8 || Math.Abs(hoverPoint.X - right) <= 8
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.Arrow);
            }
            else Cursor = new Cursor(StandardCursorType.Arrow);
            return;
        }
        if (_selectedIndex < 0 || _selectedIndex >= _cues.Count) return;
        UpdateEdgeAutoScroll(point, e.KeyModifiers);
        UpdateCueDragPreview(point, e.KeyModifiers);
        e.Handled = true;
    }

    private void UpdateEdgeAutoScroll(Point point, KeyModifiers modifiers)
    {
        _edgeAutoScrollPoint = point;
        _edgeAutoScrollModifiers = modifiers;
        if (_dragMode is not (DragMode.Scrub or DragMode.Move or DragMode.TrimStart or DragMode.TrimEnd or DragMode.Marquee))
        {
            StopEdgeAutoScroll();
            return;
        }

        const double edgeZone = 52;
        var leftEdge = TrackHeaderWidth + edgeZone;
        var rightInset = MaxTrackVerticalOffset > 0 ? 14 : 0;
        var rightEdge = Bounds.Width - rightInset - edgeZone;
        if (point.X < leftEdge || point.X > rightEdge)
        {
            if (!_edgeAutoScrollTimer.IsEnabled) _edgeAutoScrollTimer.Start();
        }
        else
        {
            StopEdgeAutoScroll();
        }
    }

    private void ApplyEdgeAutoScroll()
    {
        if (_dragMode is not (DragMode.Scrub or DragMode.Move or DragMode.TrimStart or DragMode.TrimEnd or DragMode.Marquee))
        {
            StopEdgeAutoScroll();
            return;
        }

        const double edgeZone = 52;
        var leftEdge = TrackHeaderWidth + edgeZone;
        var rightInset = MaxTrackVerticalOffset > 0 ? 14 : 0;
        var rightEdge = Bounds.Width - rightInset - edgeZone;
        var direction = _edgeAutoScrollPoint.X < leftEdge
            ? -Math.Clamp((leftEdge - _edgeAutoScrollPoint.X) / edgeZone, 0, 1)
            : _edgeAutoScrollPoint.X > rightEdge
                ? Math.Clamp((_edgeAutoScrollPoint.X - rightEdge) / edgeZone, 0, 1)
                : 0;
        if (Math.Abs(direction) < .001)
        {
            StopEdgeAutoScroll();
            return;
        }

        var previous = _viewStartSeconds;
        var secondsPerSecond = Math.Max(2.5, VisibleSeconds * .7);
        _viewStartSeconds = Math.Clamp(
            _viewStartSeconds + direction * secondsPerSecond * _edgeAutoScrollTimer.Interval.TotalSeconds,
            0,
            Math.Max(0, _durationSeconds - VisibleSeconds));
        if (Math.Abs(previous - _viewStartSeconds) < .000001) return;

        if (_dragMode == DragMode.Scrub)
            UpdateScrub(Math.Clamp(_edgeAutoScrollPoint.X, TrackHeaderWidth, Bounds.Width - rightInset), forceDispatch: false);
        else if (_dragMode == DragMode.Marquee)
            UpdateMarqueeSelection(_edgeAutoScrollPoint);
        else
            UpdateCueDragPreview(_edgeAutoScrollPoint, _edgeAutoScrollModifiers);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopEdgeAutoScroll()
    {
        if (_edgeAutoScrollTimer.IsEnabled) _edgeAutoScrollTimer.Stop();
    }

    private void UpdateCueDragPreview(Point point, KeyModifiers modifiers)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _cues.Count || _dragSnapshot.Count == 0) return;
        var freezeTime = modifiers.HasFlag(KeyModifiers.Shift);
        var disableSnap = modifiers.HasFlag(KeyModifiers.Alt);
        var pointerDeltaSeconds = (point.X - _dragOrigin.X) / _pixelsPerSecond;
        var viewportDeltaSeconds = _viewStartSeconds - _dragOriginViewStartSeconds;
        var delta = freezeTime ? 0 : (long)Math.Round((pointerDeltaSeconds + viewportDeltaSeconds) * 1000);
        var minimum = Math.Max(50L, (long)Math.Round(19 / _pixelsPerSecond * 1000));
        _snapGuideMilliseconds = null;
        switch (_dragMode)
        {
            case DragMode.Move:
                var earliest = _dragSnapshot.Min(item => item.Start);
                var effectiveDelta = Math.Max(-earliest, delta);
                var trackOffset = (int)Math.Round((point.Y - _dragOrigin.Y) / LaneHeight);
                trackOffset = Math.Clamp(trackOffset, -_dragSnapshot.Min(item => item.Track),
                    _trackCount - 1 - _dragSnapshot.Max(item => item.Track));
                if (!disableSnap) effectiveDelta = SnapMoveDelta(effectiveDelta);
                effectiveDelta = ClampMoveToNeighbours(effectiveDelta, trackOffset);
                foreach (var item in _dragSnapshot)
                    _dragPreview[item.Index] = (item.Start + effectiveDelta, item.End + effectiveDelta,
                        Math.Clamp(item.Track + trackOffset, 0, _trackCount - 1));
                break;
            case DragMode.TrimStart:
                var startItem = _dragSnapshot[0];
                var newStart = disableSnap ? startItem.Start + delta : SnapEdge(startItem.Start + delta, startItem.Index);
                var previousEnd = _cues.Where((_, index) => index != startItem.Index)
                    .Where(item => item.TrackIndex == startItem.Track && item.StartMilliseconds < startItem.Start)
                    .Select(item => item.EndMilliseconds).DefaultIfEmpty(0).Max();
                _dragPreview[startItem.Index] =
                    (Math.Clamp(newStart, previousEnd, startItem.End - minimum), startItem.End, startItem.Track);
                break;
            case DragMode.TrimEnd:
                var endItem = _dragSnapshot[0];
                var newEnd = disableSnap ? endItem.End + delta : SnapEdge(endItem.End + delta, endItem.Index);
                var nextStart = _cues.Where((_, index) => index != endItem.Index)
                    .Where(item => item.TrackIndex == endItem.Track && item.StartMilliseconds > endItem.Start)
                    .Select(item => item.StartMilliseconds).DefaultIfEmpty(long.MaxValue).Min();
                _dragPreview[endItem.Index] =
                    (endItem.Start, Math.Clamp(newEnd, endItem.Start + minimum, nextStart), endItem.Track);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPlusHovered = false;
        _hoveredTrackHeader = -1;
        if (_dragMode != DragMode.None) return;
        _hoveredIndex = -1;
        if (_splitMode)
        {
            // 剪刀模式下保持剪刀光标，仅清除参考线
            _splitPreviewX = -1;
            ClearSplitPreview();
            InvalidateVisual();
            return;
        }
        Cursor = new Cursor(StandardCursorType.Arrow);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        StopEdgeAutoScroll();
        if (_dragMode == DragMode.Marquee)
        {
            var point = e.GetPosition(this);
            if (!_marqueeHasDragged)
            {
                StartScrub(point);
                _dragMode = DragMode.None;
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }
            UpdateMarqueeSelection(point);
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);
            NotifySelectionChanged();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.VScroll)
        {
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.Scrub)
        {
            UpdateScrub(e.GetPosition(this).X, forceDispatch: true);
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.None || _selectedIndex < 0 || _selectedIndex >= _cues.Count) return;
        if (_dragMode == DragMode.VScroll)
        {
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.Scrub)
        {
            UpdateScrub(e.GetPosition(this).X, forceDispatch: true);
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.None || _selectedIndex < 0 || _selectedIndex >= _cues.Count) return;
        e.Pointer.Capture(null);
        var changes = _dragSnapshot
            .Select(item =>
            {
                var preview = _dragPreview.GetValueOrDefault(item.Index, (item.Start, item.End, item.Track));
                return new CueTimingChange(item.Index, item.Start, item.End,
                    preview.Start, preview.End, item.Track, preview.Track);
            })
            .Where(change => change.OldStart != change.NewStart || change.OldEnd != change.NewEnd ||
                             change.OldTrack != change.NewTrack)
            .ToArray();
        foreach (var change in changes)
        {
            if (change.Index >= 0 && change.Index < _cues.Count)
            {
                var targetCue = _cues[change.Index];
                if (targetCue != null)
                {
                    targetCue.StartMilliseconds = change.NewStart;
                    targetCue.EndMilliseconds = change.NewEnd;
                    targetCue.TrackIndex = change.NewTrack;
                }
            }
        }
        _dragMode = DragMode.None;
        _snapGuideMilliseconds = null;
        _dragPreview.Clear();
        _dragSnapTargets = Array.Empty<long>();
        if (changes.Length > 0)
        {
            _cueIndexesDirty = true;
            CueEdited?.Invoke(this, new TimelineCueEdit(changes));
        }
        else if (_selectedIndex >= 0 && _selectedIndex < _cues.Count)
        {
            var cue = _cues[_selectedIndex];
            if (cue != null)
            {
                var centerSeconds = (cue.StartMilliseconds + cue.EndMilliseconds) / 2000.0;
                _positionSeconds = centerSeconds;
                SeekRequested?.Invoke(this, centerSeconds);
            }
        }
        CueInteractionCompleted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    private void ClearSplitPreview()
    {
        if (_splitPreviewIndex < 0 && _splitPreviewMilliseconds < 0) return;
        _splitPreviewIndex = -1;
        _splitPreviewMilliseconds = -1;
        SplitPreviewChanged?.Invoke(this, (-1, -1));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        StopEdgeAutoScroll();
        if (_dragMode == DragMode.Marquee)
        {
            _dragMode = DragMode.None;
            NotifySelectionChanged();
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Alt+滚轮：以指针位置为中心缩放时间轴
            var x = e.GetPosition(this).X;
            var anchor = XToTime(x);
            var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
            _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * factor, 30, 260);
            var visible = VisibleSeconds;
            _viewStartSeconds = Math.Clamp(anchor - (x - TrackHeaderWidth) / _pixelsPerSecond, 0,
                Math.Max(0, _durationSeconds - visible));
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(this);
        var scrollTracks = MaxTrackVerticalOffset > 0 &&
                           (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || point.X >= Bounds.Width - 14);
        if (scrollTracks && Math.Abs(e.Delta.Y) > 0.001)
        {
            TrackVerticalOffset -= e.Delta.Y * LaneHeight * 0.8;
            e.Handled = true;
            return;
        }

        var visible2 = VisibleSeconds;
        _viewStartSeconds = Math.Clamp(_viewStartSeconds - e.Delta.Y * visible2 * .12,
            0, Math.Max(0, _durationSeconds - visible2));
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private int HitCue(Point point)
    {
        if (point.Y < LaneTop || point.Y > LaneTop + ViewportTrackHeight) return -1;
        var track = (int)Math.Floor((point.Y + _trackVerticalOffset - LaneTop) / LaneHeight);
        if (track < 0 || track >= _trackCount) return -1;
        var tolerance = Math.Max(1L, (long)Math.Ceiling(5 / _pixelsPerSecond * 1000));
        var milliseconds = (long)Math.Round(XToTime(point.X) * 1000);
        var hit = -1;
        foreach (var i in EnumerateCuesInRange(track, milliseconds - tolerance, milliseconds + tolerance))
        {
            var layout = GetCueLayout(i);
            if (point.X >= TimeToX(layout.Start / 1000d) - 5 &&
                point.X <= TimeToX(layout.End / 1000d) + 5 && i > hit) hit = i;
        }
        return hit;
    }

    private double TimeToX(double time) => TrackHeaderWidth + (time - _viewStartSeconds) * _pixelsPerSecond;
    private double XToTime(double x) => Math.Clamp(_viewStartSeconds + (x - TrackHeaderWidth) / _pixelsPerSecond, 0, _durationSeconds);

    private void UpdateScrub(double x, bool forceDispatch)
    {
        _positionSeconds = XToTime(x);
        InvalidateVisual();

        var now = Stopwatch.GetTimestamp();
        // The playhead remains pointer-rate, while expensive exact mpv seeks are
        // capped at about 30 Hz. The release event always sends the final position.
        if (!forceDispatch && _lastScrubDispatchTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastScrubDispatchTimestamp, now).TotalMilliseconds < 33) return;
        _lastScrubDispatchTimestamp = now;
        SeekRequested?.Invoke(this, _positionSeconds);
    }

    private (long Start, long End, int Track) GetCueLayout(int index)
    {
        if (_dragPreview.TryGetValue(index, out var preview)) return preview;
        if (index < 0 || index >= _cues.Count) return (0, 0, 0);
        var cue = _cues[index];
        if (cue == null) return (0, 0, 0);
        return (cue.StartMilliseconds, cue.EndMilliseconds, cue.TrackIndex);
    }

    private long SnapMoveDelta(long delta)
    {
        if (_dragSnapshot.Count == 0 || _dragSnapTargets.Length == 0) return delta;
        var threshold = Math.Max(20L, (long)Math.Round(4 / _pixelsPerSecond * 1000));
        var edges = _dragSnapshot.SelectMany(item => new[] { item.Start, item.End }).ToArray();
        long? correction = null;
        long? guide = null;
        foreach (var edge in edges)
        {
            var proposed = edge + delta;
            var index = Array.BinarySearch(_dragSnapTargets, proposed);
            if (index < 0) index = ~index;
            for (var candidateIndex = Math.Max(0, index - 1);
                 candidateIndex <= Math.Min(_dragSnapTargets.Length - 1, index); candidateIndex++)
            {
                var target = _dragSnapTargets[candidateIndex];
                var candidate = target - proposed;
                if (Math.Abs(candidate) > threshold ||
                    correction is not null && Math.Abs(candidate) >= Math.Abs(correction.Value)) continue;
                correction = candidate;
                guide = target;
            }
        }
        if (correction is null) return delta;
        _snapGuideMilliseconds = guide;
        return delta + correction.Value;
    }

    private long SnapEdge(long proposed, int movingIndex)
    {
        var threshold = Math.Max(20L, (long)Math.Round(4 / _pixelsPerSecond * 1000));
        if (_dragSnapTargets.Length == 0) return proposed;
        var index = Array.BinarySearch(_dragSnapTargets, proposed);
        if (index < 0) index = ~index;
        var nearest = _dragSnapTargets[Math.Clamp(index, 0, _dragSnapTargets.Length - 1)];
        if (index > 0 && Math.Abs(_dragSnapTargets[index - 1] - proposed) < Math.Abs(nearest - proposed))
            nearest = _dragSnapTargets[index - 1];
        if (Math.Abs(nearest - proposed) > threshold) return proposed;
        _snapGuideMilliseconds = nearest;
        return nearest;
    }

    private long ClampMoveToNeighbours(long delta, int trackOffset)
    {
        if (_dragSnapshot.Count == 0) return delta;
        var moving = _dragSnapshot.Select(item => item.Index).ToHashSet();
        var result = delta;
        foreach (var item in _dragSnapshot)
        {
            var targetTrack = Math.Clamp(item.Track + trackOffset, 0, _trackCount - 1);
            foreach (var other in _cues.Where((cue, index) => !moving.Contains(index) && cue != null && cue.TrackIndex == targetTrack))
            {
                var start = item.Start + result;
                var end = item.End + result;
                if (start >= other.EndMilliseconds || end <= other.StartMilliseconds) continue;
                result = result >= 0
                    ? Math.Min(result, other.StartMilliseconds - item.End)
                    : Math.Max(result, other.EndMilliseconds - item.Start);
            }
        }
        var minStart = _dragSnapshot.Count > 0 ? _dragSnapshot.Min(item => item.Start) : 0;
        return Math.Max(-minStart, result);
    }
}
