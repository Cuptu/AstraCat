using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AstraCat;

public partial class MainWindow
{
    private readonly ObservableCollection<EditorSubtitleCue> _workspaceCues = new();
    private readonly MpvPlayerService _workspacePlayer = new();
    private readonly SemaphoreSlim _workspacePlayerGate = new(1, 1);
    private readonly SemaphoreSlim _workspaceSubtitleReloadGate = new(1, 1);
    private readonly SemaphoreSlim _workspaceAutoSaveGate = new(1, 1);
    private readonly Stack<WorkspaceHistoryCommand> _workspaceUndo = new();
    private readonly Stack<WorkspaceHistoryCommand> _workspaceRedo = new();
    private readonly List<WorkspaceCueSnapshot> _workspaceClipboard = new();
    private readonly DispatcherTimer _workspaceAutoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _workspaceHasPendingSave;
    private string? _workspaceLastSaveError;
    private long _workspaceSaveRevision;
    private CancellationTokenSource _workspaceLoading = new();
    private CancellationTokenSource _workspaceWaveformLoading = new();
    private string? _workspaceMediaPath;
    private string? _workspacePlayerMediaPath;
    private string? _workspaceSubtitlePath;
    private string? _workspacePreparedProjectId;
    private bool _workspaceSidebarCollapsed;
    private WindowState _workspaceWindowStateBeforeFullscreen = WindowState.Normal;
    private bool _workspaceFullscreenLayout;
    private bool _workspaceDropLoading;
    private string? _workspaceAudioPreviewSignature;
    private long _workspaceAudioPreviewValidFrom = long.MaxValue;
    private long _workspaceAudioPreviewValidThrough = long.MinValue;
    private double _workspaceDuration;
    private int _workspaceSelectedCueIndex = -1;
    private int _workspaceSelectedStyleTrack;
    private string? _workspaceSelectedStyleId;
    private bool _workspaceTimelineInteractionActive;
    private int _workspaceInlineEditingCueIndex = -1;
    private bool _workspaceInlineEditingTranslated;
    private double _workspaceVideoAspect = 16d / 9;
    private int _workspaceSubtitleReloadGeneration;
    private string _workspaceActiveRightView = "list";
    private long _workspaceActiveCueValidFrom = long.MaxValue;
    private long _workspaceActiveCueValidThrough = long.MinValue;
    private readonly HashSet<int> _workspaceActiveCueIndexes = new();

    private sealed record WorkspaceCueSnapshot(long Start, long End, string Original, string Translated,
        int Track, string? GroupId, string GroupName);

    private sealed record WorkspaceHistoryCommand(Action Undo, Action Redo);

    private sealed class WorkspaceCueState
    {
        public int Index { get; set; }
        public int TrackIndex { get; set; }
        public string? GroupId { get; set; }
        public string GroupName { get; set; } = string.Empty;
    }

    private void InitializeWorkspace()
    {
        WorkspaceTimeline.SeekRequested += (_, seconds) => _ = SeekWorkspaceAsync(seconds);
        WorkspaceTimeline.SelectedCueChanged += (_, index) => SelectWorkspaceCue(index, fromTimeline: true);
        WorkspaceTimeline.CueEditRequested += (_, index) => OpenWorkspaceCueInlineEditor(index);
        WorkspaceTimeline.CueInsertRequested += (_, request) =>
            InsertWorkspaceCue(request.PositionMilliseconds, request.Track);
        WorkspaceTimeline.CueDeleteRequested += (_, index) => DeleteWorkspaceCue(index);
        WorkspaceTimeline.CuesDeleteRequested += (_, indexes) => DeleteWorkspaceCues(indexes);
        WorkspaceTimeline.SelectionChanged += (_, indexes) =>
        {
            if (indexes.Count <= 1) return;
            WorkspaceSaveStateText.Text = $"已选择 {indexes.Count} 个字幕块";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#1F77C8");
        };
        WorkspaceTimeline.SelectAllRequested += (_, _) =>
        {
            WorkspaceSaveStateText.Text = $"已选择全部 {_workspaceCues.Count} 个字幕块";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#1F77C8");
        };
        WorkspaceTimeline.ViewportChanged += (_, _) =>
        {
            PositionWorkspaceCueInlineEditor();
            UpdateWorkspaceTimelineScrollBar();
        };
        WorkspaceTimeline.SizeChanged += (_, _) =>
        {
            WorkspaceTimeline.TrackVerticalOffset = WorkspaceTimeline.TrackVerticalOffset;
            PositionWorkspaceCueInlineEditor();
            UpdateWorkspaceTimelineScrollBar();
        };
        WorkspaceTimeline.CueEdited += WorkspaceTimeline_OnCueEdited;
        WorkspaceTimeline.CueInteractionStarted += (_, _) => _workspaceTimelineInteractionActive = true;
        WorkspaceTimeline.CueInteractionCompleted += (_, _) =>
        {
            _workspaceTimelineInteractionActive = false;
            WorkspaceTimeline.Refresh();
        };
        WorkspaceTimeline.CueSplitRequested += (_, split) => SplitWorkspaceCueAt(split.Index, split.PositionMilliseconds);
        WorkspaceTimeline.SplitPreviewChanged += (_, preview) => UpdateWorkspaceSplitPreview(preview.Index, preview.PositionMilliseconds);
        _workspacePlayer.PositionChanged += (_, seconds) => Dispatcher.UIThread.Post(() =>
        {
            WorkspaceTimeline.SetPosition(seconds);
            WorkspaceCurrentTimeText.Text = FormatWorkspaceTime(seconds);
            UpdateWorkspaceAudioSubtitlePreview(seconds);
            SyncActivePlaybackCueToList(seconds);
        }, DispatcherPriority.Render);
        _workspacePlayer.DurationChanged += (_, seconds) => Dispatcher.UIThread.Post(() =>
        {
            _workspaceDuration = Math.Max(_workspaceDuration, seconds);
            WorkspaceDurationText.Text = FormatWorkspaceTime(_workspaceDuration);
        });
        _workspacePlayer.PauseChanged += (_, paused) => Dispatcher.UIThread.Post(() =>
        {
            WorkspacePlayIcon.Kind = paused ? Material.Icons.MaterialIconKind.Play : Material.Icons.MaterialIconKind.Pause;
            WorkspacePlaybackStateText.Text = paused ? "已暂停" : "播放中";
            WorkspacePlaybackStateText.Foreground = Avalonia.Media.Brush.Parse("#6F7883");
        });
        _workspacePlayer.VideoAvailabilityChanged += (_, hasVideo) => Dispatcher.UIThread.Post(() =>
        {
            var isAudioOnly = IsAudioOnlyMedia(_workspaceMediaPath);
            WorkspaceVideoHost.IsVisible = !isAudioOnly;
            WorkspaceAudioOnlyPlaceholder.IsVisible = isAudioOnly;
            if (hasVideo) ApplyWorkspaceVideoAspect(_workspacePlayer.VideoAspect);
        });
        _workspacePlayer.VideoAspectChanged += (_, aspect) => Dispatcher.UIThread.Post(() =>
            ApplyWorkspaceVideoAspect(aspect));
        if (WorkspaceListTrackFilterCombo != null)
            WorkspaceListTrackFilterCombo.SelectedIndex = 0;
        _workspacePlayer.PlaybackError += (_, message) => Dispatcher.UIThread.Post(() =>
        {
            WorkspacePlaybackStateText.Text = message;
            WorkspacePlaybackStateText.Foreground = Avalonia.Media.Brush.Parse("#E15959");
        });
        _workspaceAutoSaveTimer.Tick += async (_, _) =>
        {
            _workspaceAutoSaveTimer.Stop();
            await SaveWorkspaceSubtitleAsync();
        };
        WorkspaceTimeline.TrackMutedChanged += (_, _) => _ = ApplyWorkspaceSubtitleStyleAsync();
        WorkspaceTimeline.TrackRemoving += (_, trackIndex) =>
        {
            var removedCues = _workspaceCues.Where(cue => cue.TrackIndex == trackIndex).ToArray();
            foreach (var cue in removedCues)
            {
                cue.PropertyChanged -= WorkspaceCue_OnPropertyChanged;
                _workspaceCues.Remove(cue);
            }
            ReindexWorkspaceCues();
            ResetWorkspaceStructuralHistory();
            _workspaceSelectedCueIndex = -1;
            WorkspaceTimeline.SetSelectedIndex(-1);
        };
        WorkspaceTimeline.TrackStructureChanged += (_, change) =>
        {
            if (_activeProjectId is null) return;
            var project = _projects.FirstOrDefault(p => p.Id == _activeProjectId);
            if (project is null) return;
            project.SubtitleTrackStyleIds ??= [];
            if (change.Added)
            {
                var insertAt = Math.Clamp(change.Index, 0, project.SubtitleTrackStyleIds.Count);
                var style = CreateWorkspaceTrackStyle(project, insertAt);
                project.SubtitleStyles.Add(style);
                project.SubtitleTrackStyleIds.Insert(insertAt, style.Id);
            }
            else if (change.Index >= 0 && change.Index < project.SubtitleTrackStyleIds.Count)
            {
                var removedStyleId = project.SubtitleTrackStyleIds[change.Index];
                project.SubtitleTrackStyleIds.RemoveAt(change.Index);
                if (removedStyleId is not ("main" or "secondary") &&
                    !project.SubtitleTrackStyleIds.Contains(removedStyleId))
                {
                    var orphan = project.SubtitleStyles.FirstOrDefault(style => style.Id == removedStyleId);
                    if (orphan is not null) project.SubtitleStyles.Remove(orphan);
                }
            }
        };
        WorkspaceTimeline.TrackCountChanged += (_, count) =>
        {
            if (_activeProjectId is not null)
            {
                var project = _projects.FirstOrDefault(p => p.Id == _activeProjectId);
                if (project is not null)
                {
                    project.SubtitleTrackCount = count;
                    EnsureProjectStyleLibrary(project);
                    RebuildWorkspaceStyleCards(project);
                    SaveProjects();
                }
            }
            RefreshWorkspaceTrackFilterItems(count);
            if (!_workspaceTimelineHeightManuallyAdjusted)
            {
                var requiredTotal = WorkspaceTimeline.RequiredHeight + 34 + 26 + 4;
                var availableTotal = WorkspaceRootGrid.Bounds.Height > 0
                    ? Math.Max(100, WorkspaceRootGrid.Bounds.Height - 204)
                    : requiredTotal;
                WorkspaceRootGrid.RowDefinitions[2].Height = new GridLength(
                    Math.Min(requiredTotal, availableTotal), GridUnitType.Pixel);
            }
            else
            {
                WorkspaceTimeline.InvalidateMeasure();
                WorkspaceTimelineArea.InvalidateMeasure();
                WorkspaceRootGrid.InvalidateMeasure();
            }
            SaveWorkspaceSubtitle();
        };
        WorkspaceMiddleGrid.SizeChanged += (_, _) =>
        {
            if (!_userAdjustedVideoColumnWidth && ProjectWorkspaceView.IsVisible)
                AutoFitWorkspaceVideoColumn();
        };
        WorkspaceColumnSplitter.PointerPressed += (_, _) => _userAdjustedVideoColumnWidth = true;
        KeyDown += Workspace_OnKeyDown;
    }

    private async void Workspace_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ProjectWorkspaceView.IsVisible) return;
        // 焦点在文本框内输入字母时不触发模式切换
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && WorkspaceTimeline.SplitMode)
        {
            SetWorkspaceSplitMode(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            WorkspaceTimeline.SelectAllCues();
            WorkspaceSaveStateText.Text = $"已选择全部 {_workspaceCues.Count} 个字幕块";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#1F77C8");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyWorkspaceCues();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyWorkspaceCues();
            var selectedIndexes = WorkspaceTimeline.SelectedCueIndexes;
            if (selectedIndexes.Count > 0) DeleteWorkspaceCues(selectedIndexes);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            PasteWorkspaceCues();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) RedoWorkspaceEdit();
            else UndoWorkspaceEdit();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            RedoWorkspaceEdit();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && WorkspaceTimeline.SelectedCueIndexes.Count > 0)
        {
            var selectedIndexes = WorkspaceTimeline.SelectedCueIndexes;
            if (selectedIndexes.Count > 1) DeleteWorkspaceCues(selectedIndexes);
            else DeleteWorkspaceCue(selectedIndexes[0]);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            NavigateWorkspaceCue(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Left or Key.Right &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var frameCount = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            NudgeWorkspaceByFrames(e.Key == Key.Right ? frameCount : -frameCount);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            await ToggleWorkspacePlaybackAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.None)
        {
            SetWorkspaceSplitMode(true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.None && WorkspaceTimeline.SplitMode)
        {
            SetWorkspaceSplitMode(false);
            e.Handled = true;
        }
    }

    private void WorkspaceSplitCue_OnClick(object? sender, RoutedEventArgs e) =>
        SetWorkspaceSplitMode(!WorkspaceTimeline.SplitMode);

    private void SetWorkspaceSplitMode(bool active)
    {
        WorkspaceTimeline.SplitMode = active;
        WorkspaceSplitCueButton.Background = Brush.Parse(active ? "#DDEBFF" : "Transparent");
        WorkspaceSplitCueButton.Foreground = Brush.Parse(active ? "#1F77C8" : "#59636F");
        WorkspaceSaveStateText.Text = active ? "剪刀模式：移动鼠标预览，单击切分，V 退出" : "已恢复普通模式";
        WorkspaceSaveStateText.Foreground = Brush.Parse(active ? "#1F77C8" : "#6F7883");
        if (!active)
        {
            WorkspaceSplitPreviewOverlay.IsVisible = false;
            WorkspaceSplitPreviewText.Text = string.Empty;
        }
    }

    private void NavigateWorkspaceCue(int direction)
    {
        if (_workspaceCues.Count == 0) return;
        var positionMilliseconds = (long)Math.Round(WorkspaceTimeline.PositionSeconds * 1000);
        var candidates = _workspaceCues.Select((cue, index) => new
            {
                Index = index,
                Center = (cue.StartMilliseconds + cue.EndMilliseconds) / 2L
            })
            .OrderBy(item => item.Center)
            .ThenBy(item => item.Index)
            .ToArray();
        var target = direction > 0
            ? candidates.FirstOrDefault(item => item.Center > positionMilliseconds + 1)
            : candidates.LastOrDefault(item => item.Center < positionMilliseconds - 1);
        target ??= direction > 0 ? candidates[^1] : candidates[0];
        SelectWorkspaceCue(target.Index, fromTimeline: false);
        _ = SeekWorkspaceAsync(target.Center / 1000d);
    }

    private void NudgeWorkspaceByFrames(int frameCount)
    {
        var frameRate = _workspacePlayer.VideoFrameRate;
        if (!double.IsFinite(frameRate) || frameRate < 1) frameRate = 25;
        var target = Math.Clamp(
            WorkspaceTimeline.PositionSeconds + frameCount / frameRate,
            0,
            Math.Max(0, _workspaceDuration));
        _ = SeekWorkspaceAsync(target);
    }

    private void UpdateWorkspaceSplitPreview(int index, long positionMilliseconds)
    {
        if (!WorkspaceTimeline.SplitMode || index < 0 || index >= _workspaceCues.Count)
        {
            WorkspaceSplitPreviewOverlay.IsVisible = false;
            WorkspaceSplitPreviewText.Text = string.Empty;
            return;
        }
        var cue = _workspaceCues[index];
        var duration = Math.Max(1, cue.EndMilliseconds - cue.StartMilliseconds);
        var ratio = Math.Clamp((positionMilliseconds - cue.StartMilliseconds) / (double)duration, 0, 1);
        var (left, right) = SplitTextProportionally(cue.DisplayText, ratio);
        WorkspaceSplitPreviewText.Text = $"{left}  /  {right}";
        WorkspaceSplitPreviewOverlay.IsVisible = true;
    }

    private void SplitWorkspaceCueAt(int splitIndex, long positionMs)
    {
        try
        {
            if (splitIndex < 0 || splitIndex >= _workspaceCues.Count) return;
            var before = CaptureWorkspaceSnapshot();
            var cue = _workspaceCues[splitIndex];
            if (cue == null) return;
            var originalEnd = cue.EndMilliseconds;
            const long minimumPartMs = 100;
            if (positionMs - cue.StartMilliseconds < minimumPartMs || originalEnd - positionMs < minimumPartMs)
            {
                WorkspaceSaveStateText.Text = "距离字幕边缘太近，无法切分";
                WorkspaceSaveStateText.Foreground = Brush.Parse("#E09A42");
                return;
            }
            var ratio = (positionMs - cue.StartMilliseconds) / (double)Math.Max(1L, originalEnd - cue.StartMilliseconds);
            var (originalFirst, originalSecond) = SplitTextProportionally(cue.Original, ratio);
            var (translatedFirst, translatedSecond) = SplitTextProportionally(cue.Translated, ratio);
            cue.EndMilliseconds = positionMs;
            cue.Original = originalFirst;
            cue.Translated = translatedFirst;
            var created = new EditorSubtitleCue
            {
                StartMilliseconds = positionMs,
                EndMilliseconds = originalEnd,
                Original = originalSecond,
                Translated = translatedSecond,
                TrackIndex = cue.TrackIndex
            };
            created.PropertyChanged += WorkspaceCue_OnPropertyChanged;
            _workspaceCues.Insert(splitIndex + 1, created);
            ReindexWorkspaceCues();
            PushWorkspaceSnapshotHistory(before, CaptureWorkspaceSnapshot());
            SelectWorkspaceCue(splitIndex + 1, fromTimeline: true);
            WorkspaceTimeline.Refresh();
            ScheduleWorkspaceAutoSave();
        }
        catch (Exception ex)
        {
            WorkspaceSaveStateText.Text = $"切分失败：{ex.Message}";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#E15959");
        }
    }

    private static (string First, string Second) SplitTextProportionally(string? text, double ratio)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 2) return (text ?? string.Empty, string.Empty);
        // Split text at the character nearest the timing ratio and trim boundary whitespace.
        var splitAt = Math.Clamp((int)Math.Round(text.Length * ratio), 1, text.Length - 1);
        return (text[..splitAt].TrimEnd(), text[splitAt..].TrimStart());
    }

    private void InsertWorkspaceCue(long positionMilliseconds, int track)
    {
        try
        {
            var before = CaptureWorkspaceSnapshot();
            var durationMilliseconds = Math.Max(500L, (long)Math.Round(_workspaceDuration * 1000));
            var start = Math.Clamp(positionMilliseconds, 0, Math.Max(0, durationMilliseconds - 250));
            var nextStart = _workspaceCues
                .Where(cue => cue != null && cue.TrackIndex == track && cue.StartMilliseconds > start)
                .Select(cue => cue.StartMilliseconds)
                .DefaultIfEmpty(durationMilliseconds)
                .Min();
            var end = Math.Min(start + 2000, nextStart);
            if (end - start < 250)
            {
                WorkspaceSaveStateText.Text = "这里没有足够空间插入字幕块";
                WorkspaceSaveStateText.Foreground = Brush.Parse("#E09A42");
                return;
            }

            var cue = new EditorSubtitleCue
            {
                StartMilliseconds = start,
                EndMilliseconds = end,
                Original = string.Empty,
                Translated = string.Empty,
                TrackIndex = Math.Clamp(track, 0, Math.Max(0, WorkspaceTimeline.TrackCount - 1))
            };
            cue.PropertyChanged += WorkspaceCue_OnPropertyChanged;
            var insertIndex = _workspaceCues
                .Select((item, index) => (item, index))
                .FirstOrDefault(pair => pair.item != null && pair.item.StartMilliseconds > start).index;
            if (insertIndex == 0 && (_workspaceCues.Count == 0 || _workspaceCues[0].StartMilliseconds <= start))
                insertIndex = _workspaceCues.Count;
            _workspaceCues.Insert(insertIndex, cue);
            ReindexWorkspaceCues();
            PushWorkspaceSnapshotHistory(before, CaptureWorkspaceSnapshot());
            SelectWorkspaceCue(insertIndex, fromTimeline: true);
            WorkspaceTimeline.Refresh();
            WorkspaceSaveStateText.Text = "已插入字幕块，请输入字幕内容";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#1F77C8");
            OpenWorkspaceCueInlineEditor(insertIndex);
        }
        catch (Exception ex)
        {
            WorkspaceSaveStateText.Text = $"插入失败：{ex.Message}";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#E15959");
        }
    }

    private void DeleteWorkspaceCue(int index)
        => DeleteWorkspaceCues([index]);

    private void DeleteWorkspaceCues(IReadOnlyList<int> indexes)
    {
        try
        {
            var targets = indexes.Distinct().Where(index => index >= 0 && index < _workspaceCues.Count)
                .OrderDescending().ToArray();
            if (targets.Length == 0) return;
            var before = CaptureWorkspaceSnapshot();
            if (_workspaceInlineEditingCueIndex >= 0) CloseWorkspaceCueInlineEditor();
            foreach (var index in targets)
            {
                _workspaceCues[index].PropertyChanged -= WorkspaceCue_OnPropertyChanged;
                _workspaceCues.RemoveAt(index);
            }
            ReindexWorkspaceCues();
            var nextIndex = _workspaceCues.Count == 0 ? -1 : Math.Min(targets.Min(), _workspaceCues.Count - 1);
            _workspaceSelectedCueIndex = nextIndex;
            WorkspaceTimeline.SetSelectedIndex(nextIndex);
            WorkspaceTimeline.Refresh();
            SaveWorkspaceSubtitle();
            PushWorkspaceSnapshotHistory(before, CaptureWorkspaceSnapshot());
            WorkspaceSaveStateText.Text = targets.Length == 1 ? "已删除当前字幕块" : $"已删除 {targets.Length} 个字幕块";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#6F7883");
        }
        catch (Exception ex)
        {
            WorkspaceSaveStateText.Text = $"批量删除失败：{ex.Message}";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#E15959");
        }
    }

    private void CopyWorkspaceCues()
    {
        var indexes = WorkspaceTimeline.SelectedCueIndexes;
        if (indexes.Count == 0 && _workspaceSelectedCueIndex >= 0) indexes = [_workspaceSelectedCueIndex];
        _workspaceClipboard.Clear();
        foreach (var index in indexes.Order())
        {
            if (index < 0 || index >= _workspaceCues.Count) continue;
            _workspaceClipboard.Add(CreateWorkspaceCueSnapshot(_workspaceCues[index]));
        }
        if (_workspaceClipboard.Count == 0) return;
        WorkspaceSaveStateText.Text = $"已复制 {_workspaceClipboard.Count} 个字幕块";
        WorkspaceSaveStateText.Foreground = Brush.Parse("#1F77C8");
    }

    private void PasteWorkspaceCues()
    {
        if (_workspaceClipboard.Count == 0)
        {
            WorkspaceSaveStateText.Text = "没有可粘贴的字幕块";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#E09A42");
            return;
        }

        var before = CaptureWorkspaceSnapshot();
        var firstStart = _workspaceClipboard.Min(item => item.Start);
        var lastEnd = _workspaceClipboard.Max(item => item.End);
        var duration = Math.Max(1L, lastEnd - firstStart);
        var mediaEnd = Math.Max(duration, (long)Math.Round(_workspaceDuration * 1000));
        var requestedStart = (long)Math.Round(WorkspaceTimeline.PositionSeconds * 1000);
        var pasteStart = Math.Clamp(requestedStart, 0, Math.Max(0, mediaEnd - duration));
        var groupIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = new List<EditorSubtitleCue>(_workspaceClipboard.Count);

        foreach (var item in _workspaceClipboard)
        {
            string? groupId = null;
            if (!string.IsNullOrWhiteSpace(item.GroupId))
            {
                if (!groupIds.TryGetValue(item.GroupId, out groupId))
                {
                    groupId = $"copy-{Guid.NewGuid():N}";
                    groupIds[item.GroupId] = groupId;
                }
            }
            var cue = new EditorSubtitleCue
            {
                StartMilliseconds = pasteStart + item.Start - firstStart,
                EndMilliseconds = pasteStart + item.End - firstStart,
                Original = item.Original,
                Translated = item.Translated,
                TrackIndex = Math.Clamp(item.Track, 0, Math.Max(0, WorkspaceTimeline.TrackCount - 1)),
                GroupId = groupId,
                GroupName = item.GroupName
            };
            cue.PropertyChanged += WorkspaceCue_OnPropertyChanged;
            var insertIndex = 0;
            while (insertIndex < _workspaceCues.Count &&
                   (_workspaceCues[insertIndex].StartMilliseconds < cue.StartMilliseconds ||
                    _workspaceCues[insertIndex].StartMilliseconds == cue.StartMilliseconds &&
                    _workspaceCues[insertIndex].TrackIndex <= cue.TrackIndex))
                insertIndex++;
            _workspaceCues.Insert(insertIndex, cue);
            created.Add(cue);
        }

        ReindexWorkspaceCues();
        var pastedIndexes = created.Select(cue => _workspaceCues.IndexOf(cue)).Where(index => index >= 0).Order().ToArray();
        _workspaceSelectedCueIndex = pastedIndexes.FirstOrDefault(-1);
        WorkspaceTimeline.SetSelectedIndexes(pastedIndexes);
        SaveWorkspaceSubtitle();
        PushWorkspaceSnapshotHistory(before, CaptureWorkspaceSnapshot());
        WorkspaceSaveStateText.Text = $"已粘贴 {pastedIndexes.Length} 个字幕块";
        WorkspaceSaveStateText.Foreground = Brush.Parse("#1F77C8");
    }

    private void ReindexWorkspaceCues()
    {
        for (var index = 0; index < _workspaceCues.Count; index++)
            _workspaceCues[index].Index = index + 1;
        WorkspaceSubtitleListEmpty.IsVisible = _workspaceCues.Count == 0;
        if (_workspaceActiveRightView == "list")
            WorkspaceRightPanelCount.Text = $"{_workspaceCues.Count} 条";
        InvalidateWorkspaceCueTimingIndex(clearActiveState: true);
    }

    private void InvalidateWorkspaceCueTimingIndex(bool clearActiveState = false)
    {
        if (clearActiveState)
        {
            foreach (var activeIndex in _workspaceActiveCueIndexes)
                if (activeIndex >= 0 && activeIndex < _workspaceCues.Count) _workspaceCues[activeIndex].IsActive = false;
            _workspaceActiveCueIndexes.Clear();
            _workspaceActiveListGroupKey = null;
        }
        _workspaceActiveCueValidFrom = long.MaxValue;
        _workspaceActiveCueValidThrough = long.MinValue;
        WorkspaceTimeline.NotifyCueDataChanged();
    }

    private void ResetWorkspaceStructuralHistory()
    {
        _workspaceUndo.Clear();
        _workspaceRedo.Clear();
        RefreshWorkspaceHistoryButtons();
    }

    private WorkspaceCueSnapshot[] CaptureWorkspaceSnapshot() =>
        _workspaceCues.Select(CreateWorkspaceCueSnapshot).ToArray();

    private static WorkspaceCueSnapshot CreateWorkspaceCueSnapshot(EditorSubtitleCue cue) =>
        new(cue.StartMilliseconds, cue.EndMilliseconds, cue.Original, cue.Translated,
            cue.TrackIndex, cue.GroupId, cue.GroupName);

    private static EditorSubtitleCue CreateWorkspaceCue(WorkspaceCueSnapshot item) => new()
    {
        StartMilliseconds = item.Start,
        EndMilliseconds = item.End,
        Original = item.Original,
        Translated = item.Translated,
        TrackIndex = item.Track,
        GroupId = item.GroupId,
        GroupName = item.GroupName
    };

    private void PushWorkspaceSnapshotHistory(
        IReadOnlyList<WorkspaceCueSnapshot> before,
        IReadOnlyList<WorkspaceCueSnapshot> after)
    {
        var beforeCopy = before.ToArray();
        var afterCopy = after.ToArray();
        _workspaceUndo.Push(new WorkspaceHistoryCommand(
            () => RestoreWorkspaceSnapshot(beforeCopy),
            () => RestoreWorkspaceSnapshot(afterCopy)));
        _workspaceRedo.Clear();
        RefreshWorkspaceHistoryButtons();
    }

    private void RestoreWorkspaceSnapshot(IReadOnlyList<WorkspaceCueSnapshot> snapshot)
    {
        if (_workspaceInlineEditingCueIndex >= 0) CloseWorkspaceCueInlineEditor();
        foreach (var cue in _workspaceCues)
            cue.PropertyChanged -= WorkspaceCue_OnPropertyChanged;
        _workspaceCues.Clear();
        foreach (var item in snapshot)
        {
            var cue = CreateWorkspaceCue(item);
            cue.PropertyChanged += WorkspaceCue_OnPropertyChanged;
            _workspaceCues.Add(cue);
        }
        ReindexWorkspaceCues();
        _workspaceSelectedCueIndex = -1;
        WorkspaceTimeline.SetSelectedIndex(-1);
        WorkspaceTimeline.Refresh();
        SaveWorkspaceSubtitle();
    }

    private string WorkspaceCuesPath(string projectId) => Path.Combine(ProjectDirectory(projectId), "workspace-cues.json");

    private void PopulateWorkspaceCuesFromSegments(IEnumerable<SubtitleSegment> segments)
    {
        foreach (var segment in segments)
        {
            var groupId = $"bilingual-{segment.Index}";
            if (!string.IsNullOrWhiteSpace(segment.Translated))
            {
                var translatedCue = new EditorSubtitleCue
                {
                    Index = _workspaceCues.Count + 1,
                    StartMilliseconds = segment.StartMilliseconds,
                    EndMilliseconds = segment.EndMilliseconds,
                    Translated = segment.Translated,
                    TrackIndex = 0,
                    GroupId = groupId,
                    GroupName = "中英双语"
                };
                translatedCue.PropertyChanged += WorkspaceCue_OnPropertyChanged;
                _workspaceCues.Add(translatedCue);
            }
            if (!string.IsNullOrWhiteSpace(segment.Original))
            {
                var originalCue = new EditorSubtitleCue
                {
                    Index = _workspaceCues.Count + 1,
                    StartMilliseconds = segment.StartMilliseconds,
                    EndMilliseconds = segment.EndMilliseconds,
                    Original = segment.Original,
                    TrackIndex = string.IsNullOrWhiteSpace(segment.Translated) ? 0 : 1,
                    GroupId = groupId,
                    GroupName = string.IsNullOrWhiteSpace(segment.Translated) ? string.Empty : "中英双语"
                };
                originalCue.PropertyChanged += WorkspaceCue_OnPropertyChanged;
                _workspaceCues.Add(originalCue);
            }
        }
    }

    private async Task PrepareWorkspaceAsync(CaptionProject project)
    {
        if (!string.Equals(_workspaceMediaPath, project.SourceVideoPath, StringComparison.OrdinalIgnoreCase) &&
            _workspacePlayer.IsRunning)
        {
            _workspacePlayerMediaPath = null;
        }
        _workspaceMediaPath = project.SourceVideoPath;
        var audioOnly = IsAudioOnlyMedia(_workspaceMediaPath);
        WorkspaceVideoHost.IsVisible = !audioOnly;
        WorkspaceAudioOnlyPlaceholder.IsVisible = audioOnly;
        WorkspaceVideoColumn.Height = double.NaN;
        WorkspaceMediaNameText.Text = string.IsNullOrWhiteSpace(_workspaceMediaPath)
            ? "尚未导入媒体"
            : Path.GetFileName(_workspaceMediaPath);

        WorkspaceSubtitleListBox.ItemsSource = null;
        foreach (var cue in _workspaceCues) cue.PropertyChanged -= WorkspaceCue_OnPropertyChanged;
        _workspaceCues.Clear();

        var loadedFromSavedCues = false;
        var workspaceCuesFile = WorkspaceCuesPath(project.Id);
        var editedSrtFile = Path.Combine(ProjectDirectory(project.Id), "edited.srt");

        // 1. 优先加载工作区完整保存记录 (workspace-cues.json)
        if (File.Exists(workspaceCuesFile))
        {
            try
            {
                var savedCues = JsonSerializer.Deserialize<List<WorkspaceAutoSaveCue>>(
                    await File.ReadAllTextAsync(workspaceCuesFile));
                if (savedCues != null && savedCues.Count > 0)
                {
                    foreach (var sc in savedCues)
                    {
                        var cue = new EditorSubtitleCue
                        {
                            Index = sc.Index,
                            StartMilliseconds = sc.Start,
                            EndMilliseconds = sc.End,
                            Original = sc.Original ?? string.Empty,
                            Translated = sc.Translated ?? string.Empty,
                            TrackIndex = sc.TrackIndex,
                            GroupId = sc.GroupId,
                            GroupName = sc.GroupName ?? string.Empty
                        };
                        cue.PropertyChanged += WorkspaceCue_OnPropertyChanged;
                        _workspaceCues.Add(cue);
                    }
                    _workspaceSubtitlePath = editedSrtFile;
                    loadedFromSavedCues = true;
                }
            }
            catch { }
        }

        // 2. 如果没有 workspace-cues.json，但存在 edited.srt，从 edited.srt 恢复
        if (!loadedFromSavedCues && File.Exists(editedSrtFile))
        {
            try
            {
                var segments = ParseSrt(await File.ReadAllTextAsync(editedSrtFile));
                if (segments.Count > 0)
                {
                    PopulateWorkspaceCuesFromSegments(segments);
                    LoadWorkspaceCueState(project.Id);
                    _workspaceSubtitlePath = editedSrtFile;
                    loadedFromSavedCues = true;
                }
            }
            catch { }
        }

        // 3. 如果工作区尚无保存记录，按翻译/处理/识别流水线加载初始字幕
        if (!loadedFromSavedCues)
        {
            var preparedSubtitles = await Task.Run(() =>
            {
                var subtitlePath = new[]
                    {
                        Path.Combine(ProjectDirectory(project.Id), "translated.srt"),
                        project.ProcessedSubtitlePath,
                        project.SubtitlePath
                    }
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
                var segments = new List<SubtitleSegment>();
                var translationCachePath = ProjectTranslationCachePath(project.Id);
                if (File.Exists(translationCachePath))
                {
                    try
                    {
                        segments = JsonSerializer.Deserialize<List<SubtitleSegment>>(
                            File.ReadAllText(translationCachePath)) ?? [];
                    }
                    catch { }
                }
                if (segments.Count == 0 && !string.IsNullOrWhiteSpace(subtitlePath))
                {
                    try { segments = ParseSrt(File.ReadAllText(subtitlePath)); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return (Path: subtitlePath, Segments: segments);
            });

            _workspaceSubtitlePath = preparedSubtitles.Path;
            PopulateWorkspaceCuesFromSegments(preparedSubtitles.Segments);
            LoadWorkspaceCueState(project.Id);
        }

        EnsureProjectStyleLibrary(project);
        SaveProjects();
        UpdateWorkspaceStyleGroupRows(project);
        WorkspaceSubtitleListBox.ItemsSource = _workspaceCues;
        SetWorkspaceRightView("list");
        WorkspaceSubtitleListEmpty.IsVisible = _workspaceCues.Count == 0;
        if (_workspaceActiveRightView == "list")
        {
            WorkspaceRightPanelCount.Text = $"{_workspaceCues.Count} 条";
        }

        _workspaceDuration = Math.Max(1, _workspaceCues.Count == 0 ? 60 : _workspaceCues.Max(cue => cue.EndMilliseconds) / 1000d);
        _workspaceSelectedCueIndex = -1;
        InvalidateWorkspaceCueTimingIndex(clearActiveState: true);
        WorkspaceTimeline.SetDocument(_workspaceCues, null, _workspaceDuration);
        WorkspaceTimeline.TrackCount = project.SubtitleTrackCount > 0 ? project.SubtitleTrackCount : 2;
        RefreshWorkspaceTrackFilterItems(WorkspaceTimeline.TrackCount);
        UpdateWorkspaceStyleGroupRows(project);
        WorkspaceTimeline.SetStyleGroups(project.MainSubtitleStyle, project.SecondarySubtitleStyle);
        WorkspaceDurationText.Text = FormatWorkspaceTime(_workspaceDuration);
        WorkspaceCurrentTimeText.Text = "00:00.000";
        _workspaceUndo.Clear();
        _workspaceRedo.Clear();
        RefreshWorkspaceHistoryButtons();
        _workspacePreparedProjectId = project.Id;
        _workspaceAudioPreviewSignature = null;
        UpdateWorkspaceAudioSubtitlePreview(0, force: true);
    }

    private async Task ActivateWorkspaceAsync()
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        // Let Avalonia present the workspace shell before parsing subtitles or
        // starting native media components.
        await Task.Yield();
        if (!string.Equals(_workspacePreparedProjectId, project.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_workspaceMediaPath, project.SourceVideoPath, StringComparison.OrdinalIgnoreCase))
            await PrepareWorkspaceAsync(project);
        _ = InitializeWorkspaceMediaAsync(project);
    }

    private async Task InitializeWorkspaceMediaAsync(CaptionProject project)
    {
        var playerTask = StartWorkspacePlayerAsync();
        var waveformTask = LoadWorkspaceWaveformAsync(project);
        await Task.WhenAll(playerTask, waveformTask);
    }

    private async Task StartWorkspacePlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(_workspaceMediaPath) || !File.Exists(_workspaceMediaPath)) return;
        await _workspacePlayerGate.WaitAsync();
        try
        {
            if (_workspacePlayer.IsRunning && string.Equals(_workspacePlayerMediaPath, _workspaceMediaPath, StringComparison.OrdinalIgnoreCase))
            {
                await ApplyWorkspaceSubtitleStyleAsync();
                return;
            }
            string? initialAssPath = null;
            var audioOnly = IsAudioOnlyMedia(_workspaceMediaPath);
            if (!audioOnly && !string.IsNullOrWhiteSpace(_activeProjectId))
            {
                var project = _projects.FirstOrDefault(p => p.Id == _activeProjectId);
                if (project != null)
                {
                    initialAssPath = CreateWorkspaceAss(project);
                }
            }
            else if (!audioOnly && !string.IsNullOrWhiteSpace(_workspaceSubtitlePath) && File.Exists(_workspaceSubtitlePath))
            {
                initialAssPath = _workspaceSubtitlePath;
            }

            WorkspacePlaybackStateText.Text = "正在启动 MPV…";
            WorkspacePlaybackStateText.Foreground = Avalonia.Media.Brush.Parse("#AAB2BD");
            await _workspacePlayer.StartAsync(WorkspaceVideoHost, _workspaceMediaPath, initialAssPath, _workspaceLoading.Token);
            _workspacePlayerMediaPath = _workspaceMediaPath;
            await ApplyWorkspaceSubtitleStyleAsync();
            WorkspacePlaybackStateText.Text = "已暂停";
            WorkspacePlaybackStateText.Foreground = Avalonia.Media.Brush.Parse("#AAB2BD");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WorkspacePlaybackStateText.Text = ex.Message;
            WorkspacePlaybackStateText.Foreground = Avalonia.Media.Brush.Parse("#E15959");
        }
        finally
        {
            _workspacePlayerGate.Release();
        }
    }

    private string _workspaceWaveformSummaryText = "等待生成波形";

    private void WorkspaceToggleWaveform_OnClick(object? sender, RoutedEventArgs e)
    {
        WorkspaceTimeline.ShowWaveform = !WorkspaceTimeline.ShowWaveform;
        WorkspaceWaveformEyeIcon.Kind = WorkspaceTimeline.ShowWaveform
            ? Material.Icons.MaterialIconKind.EyeOutline
            : Material.Icons.MaterialIconKind.EyeOffOutline;
        WorkspaceWaveformEyeIcon.Foreground = WorkspaceTimeline.ShowWaveform
            ? Avalonia.Media.Brush.Parse("#59636F")
            : Avalonia.Media.Brush.Parse("#A0A7B0");
        WorkspaceWaveformStateText.Text = WorkspaceTimeline.ShowWaveform
            ? _workspaceWaveformSummaryText
            : "波形已隐藏";
        PositionWorkspaceCueInlineEditor();
    }

    private async Task LoadWorkspaceWaveformAsync(CaptionProject project)
    {
        if (string.IsNullOrWhiteSpace(project.SourceVideoPath) || !File.Exists(project.SourceVideoPath)) return;
        var previous = _workspaceWaveformLoading;
        var current = new CancellationTokenSource();
        _workspaceWaveformLoading = current;
        previous.Cancel();
        previous.Dispose();
        try
        {
            WorkspaceWaveformStateText.Text = "正在分析波形";
            var cache = Path.Combine(_deployment.RuntimeRoot, "cache", "waveforms");
            var data = await WaveformService.LoadAsync(project.SourceVideoPath, cache, current.Token);
            if (current.IsCancellationRequested || !ReferenceEquals(_workspaceWaveformLoading, current)) return;
            _workspaceDuration = Math.Max(_workspaceDuration, data.DurationSeconds);
            WorkspaceTimeline.SetWaveform(data.Peaks, data.DurationSeconds);
            WorkspaceDurationText.Text = FormatWorkspaceTime(_workspaceDuration);
            _workspaceWaveformSummaryText = $"轻量波形 · {data.Peaks.Length:N0} 峰值";
            if (WorkspaceTimeline.ShowWaveform)
                WorkspaceWaveformStateText.Text = _workspaceWaveformSummaryText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _workspaceWaveformSummaryText = $"波形不可用：{ex.Message}";
            if (WorkspaceTimeline.ShowWaveform)
                WorkspaceWaveformStateText.Text = _workspaceWaveformSummaryText;
        }
    }

    private async void WorkspaceOpenMedia_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        var path = await PickProjectVideoAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        project.SourceVideoPath = path;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        await PrepareWorkspaceAsync(project);
        await ActivateWorkspaceAsync();
    }

    private static bool IsWorkspaceMediaDrop(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" or ".flv" or
            ".m4a" or ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".opus";

    private static bool IsWorkspaceSubtitleDrop(string path) =>
        Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase);

    private void Workspace_OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_workspaceDropLoading && e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Workspace_OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_workspaceDropLoading || _activeProjectId is null) return;

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var mediaPath = paths.FirstOrDefault(IsWorkspaceMediaDrop);
        var subtitlePath = paths.FirstOrDefault(IsWorkspaceSubtitleDrop);
        if (mediaPath is null && subtitlePath is null)
        {
            WorkspaceSaveStateText.Text = "仅支持视频、音频和 SRT 字幕文件";
            WorkspaceSaveStateText.Foreground = Avalonia.Media.Brush.Parse("#E15959");
            return;
        }

        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        _workspaceDropLoading = true;
        WorkspaceSaveStateText.Text = "正在加载拖入的文件…";
        WorkspaceSaveStateText.Foreground = Avalonia.Media.Brush.Parse("#3399F3");
        try
        {
            List<SubtitleSegment>? importedSegments = null;
            if (subtitlePath is not null)
            {
                importedSegments = await Task.Run(() => ParseSrt(File.ReadAllText(subtitlePath)));
                if (importedSegments.Count == 0)
                    throw new InvalidDataException("没有识别到有效的 SRT 字幕段落");
            }

            _workspaceAutoSaveTimer.Stop();
            if (_workspaceCues.Count > 0)
                await SaveWorkspaceSubtitleAsync();

            if (mediaPath is not null)
                project.SourceVideoPath = mediaPath;
            if (subtitlePath is not null && importedSegments is not null)
            {
                project.SubtitlePath = subtitlePath;
                project.ProcessedSubtitlePath = null;
                _projectTranslationSegments.Clear();
                _projectTranslationSegments.AddRange(importedSegments);
                SaveProjectTranslationCache(project.Id);
                var cueStatePath = WorkspaceCueStatePath(project.Id);
                if (File.Exists(cueStatePath)) File.Delete(cueStatePath);
                var cuesPath = WorkspaceCuesPath(project.Id);
                if (File.Exists(cuesPath)) File.Delete(cuesPath);
                var editedPath = Path.Combine(ProjectDirectory(project.Id), "edited.srt");
                if (File.Exists(editedPath)) File.Delete(editedPath);
            }

            project.UpdatedAt = DateTimeOffset.Now;
            _workspacePreparedProjectId = null;
            SaveProjects();
            RefreshProjectWorkflow(project);
            RefreshProjectTranslation(project);
            RefreshProjectProcessing(project);
            RebuildProjectSidebar();
            SetProjectSelection(project.Id);

            await PrepareWorkspaceAsync(project);
            await InitializeWorkspaceMediaAsync(project);

            var loaded = mediaPath is not null && subtitlePath is not null
                ? $"已加载视频和 {importedSegments!.Count} 条字幕"
                : mediaPath is not null
                    ? $"已加载媒体：{Path.GetFileName(mediaPath)}"
                    : $"已加载 {importedSegments!.Count} 条字幕";
            WorkspaceSaveStateText.Text = loaded;
            WorkspaceSaveStateText.Foreground = Avalonia.Media.Brush.Parse("#278A68");
        }
        catch (Exception exception)
        {
            WorkspaceSaveStateText.Text = $"拖入失败：{ShortMessage(exception.Message)}";
            WorkspaceSaveStateText.Foreground = Avalonia.Media.Brush.Parse("#E15959");
        }
        finally
        {
            _workspaceDropLoading = false;
        }
    }

    private async void WorkspacePlayPause_OnClick(object? sender, RoutedEventArgs e) =>
        await ToggleWorkspacePlaybackAsync();

    private async Task ToggleWorkspacePlaybackAsync()
    {
        if (!_workspacePlayer.IsRunning)
        {
            await StartWorkspacePlayerAsync();
            if (_workspacePlayer.IsRunning) await _workspacePlayer.SetPauseAsync(false);
        }
        else
        {
            await _workspacePlayer.TogglePauseAsync();
        }
    }

    private async void WorkspaceBack_OnClick(object? sender, RoutedEventArgs e) =>
        await _workspacePlayer.SeekRelativeAsync(-2);

    private async void WorkspaceForward_OnClick(object? sender, RoutedEventArgs e) =>
        await _workspacePlayer.SeekRelativeAsync(2);

    private async Task SeekWorkspaceAsync(double seconds)
    {
        WorkspaceTimeline.SetPosition(seconds, keepVisible: false);
        if (_workspacePlayer.IsRunning) await _workspacePlayer.SeekAsync(seconds);
    }

    private bool _userAdjustedVideoColumnWidth;
    private bool _workspaceTimelineHeightManuallyAdjusted;

    private void WorkspaceColumnSplitter_OnDoubleTapped(object? sender, TappedEventArgs e) =>
        AutoFitWorkspaceVideoColumn(resetManual: true);

    private void WorkspaceTimelineSplitter_OnDoubleTapped(object? sender, TappedEventArgs e) =>
        ResetWorkspaceTimelineHeight();

    private void WorkspaceTimelineSplitter_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _workspaceTimelineHeightManuallyAdjusted = true;

    private void ApplyWorkspaceVideoAspect(double aspect)
    {
        if (aspect <= 0.05) return;
        _workspaceVideoAspect = aspect;
        if (ProjectWorkspaceView.IsVisible && !_userAdjustedVideoColumnWidth)
            AutoFitWorkspaceVideoColumn();
    }

    private void AutoFitWorkspaceVideoColumn(bool resetManual = false)
    {
        if (resetManual) _userAdjustedVideoColumnWidth = false;
        if (!ProjectWorkspaceView.IsVisible) return;
        if (IsAudioOnlyMedia(_workspaceMediaPath))
        {
            WorkspaceMiddleGrid.ColumnDefinitions[0].Width = new GridLength(1.2, GridUnitType.Star);
            WorkspaceMiddleGrid.ColumnDefinitions[1].Width = new GridLength(4, GridUnitType.Pixel);
            WorkspaceMiddleGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        var totalWidth = WorkspaceMiddleGrid.Bounds.Width;
        var totalHeight = WorkspaceMiddleGrid.Bounds.Height;
        if (totalWidth <= 100 || totalHeight <= 100) return;

        // Video playback control bar at bottom is 44px
        var videoSurfaceHeight = Math.Max(60, totalHeight - 44);
        var aspect = _workspaceVideoAspect > 0.05 ? _workspaceVideoAspect : (16d / 9d);
        var idealVideoWidth = videoSurfaceHeight * aspect;

        var minStyleWidth = 260.0;
        var minVideoWidth = 200.0;
        var splitterWidth = 4.0;
        var maxVideoWidth = Math.Max(minVideoWidth, totalWidth - minStyleWidth - splitterWidth);

        var targetVideoWidth = Math.Clamp(idealVideoWidth, minVideoWidth, maxVideoWidth);

        WorkspaceMiddleGrid.ColumnDefinitions[0].Width = new GridLength(targetVideoWidth, GridUnitType.Pixel);
        WorkspaceMiddleGrid.ColumnDefinitions[1].Width = new GridLength(splitterWidth, GridUnitType.Pixel);
        WorkspaceMiddleGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
    }

    private void ResetWorkspaceTimelineHeight()
    {
        _workspaceTimelineHeightManuallyAdjusted = false;
        WorkspaceRootGrid.RowDefinitions[0].Height = new GridLength(1.0, GridUnitType.Star);
        WorkspaceRootGrid.RowDefinitions[1].Height = new GridLength(4, GridUnitType.Pixel);
        WorkspaceRootGrid.RowDefinitions[2].Height = GridLength.Auto;
    }

    private void UpdateWorkspaceTimelineScrollBar()
    {
        var visible = WorkspaceTimeline.VisibleSeconds;
        var duration = WorkspaceTimeline.DurationSeconds;
        var max = Math.Max(0, duration - visible);
        WorkspaceTimelineScrollBar.Maximum = max;
        WorkspaceTimelineScrollBar.ViewportSize = visible;
        WorkspaceTimelineScrollBar.Value = Math.Clamp(WorkspaceTimeline.ViewStartSeconds, 0, max);
    }

    private void WorkspaceTimelineScrollBar_OnScroll(object? sender, ScrollEventArgs e)
    {
        // 拖动滚动条快速定位时间轴视口；程序赋值 Value 不会触发 Scroll，无回环
        WorkspaceTimeline.ScrollTo(e.NewValue);
    }

    private bool _workspaceSyncingListSelection;
    private string? _workspaceActiveListGroupKey;
    private CancellationTokenSource _workspaceListScrollAnimation = new();

    private void SelectWorkspaceCue(int index, bool fromTimeline)
    {
        if (index < 0)
        {
            _workspaceSelectedCueIndex = -1;
            if (_workspaceInlineEditingCueIndex >= 0)
                CloseWorkspaceCueInlineEditor();
            if (!fromTimeline) WorkspaceTimeline.SetSelectedIndex(-1);
            if (WorkspaceSubtitleListBox != null)
            {
                try
                {
                    _workspaceSyncingListSelection = true;
                    WorkspaceSubtitleListBox.SelectedItems?.Clear();
                    foreach (var c in _workspaceCues) c.IsActive = false;
                }
                finally
                {
                    _workspaceSyncingListSelection = false;
                }
            }
            return;
        }

        if (index >= _workspaceCues.Count) return;
        if (_workspaceInlineEditingCueIndex >= 0 && _workspaceInlineEditingCueIndex != index)
            CloseWorkspaceCueInlineEditor();
        _workspaceSelectedCueIndex = index;
        if (!fromTimeline) WorkspaceTimeline.SetSelectedIndex(index);
        SelectWorkspaceStyleGroup(_workspaceCues[index].TrackIndex, applyToPlayer: true);

        var currentCue = _workspaceCues[index];
        var midTime = (currentCue.StartMilliseconds + currentCue.EndMilliseconds) / 2;

        if (WorkspaceSubtitleListBox != null)
        {
            try
            {
                _workspaceSyncingListSelection = true;
                WorkspaceSubtitleListBox.SelectedItems?.Clear();

                for (var i = 0; i < _workspaceCues.Count; i++)
                {
                    var c = _workspaceCues[i];
                    var isCompanion = c.StartMilliseconds <= midTime && c.EndMilliseconds >= midTime;
                    if (i == index || isCompanion)
                    {
                        c.IsActive = true;
                        WorkspaceSubtitleListBox.SelectedItems?.Add(c);
                    }
                    else
                    {
                        c.IsActive = false;
                    }
                }

                if (fromTimeline && _workspaceActiveRightView == "list")
                {
                    QueueWorkspaceSubtitleScroll(index);
                }
            }
            finally
            {
                _workspaceSyncingListSelection = false;
            }
        }
    }

    private void QueueWorkspaceSubtitleScroll(int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= _workspaceCues.Count) return;
        if (_workspaceCues.Any(c => c.IsEditing)) return;
        var scrollViewer = WorkspaceSubtitleListBox.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null || scrollViewer.Viewport.Height <= 0) return;
        _workspaceListScrollAnimation.Cancel();
        _workspaceListScrollAnimation.Dispose();
        _workspaceListScrollAnimation = new CancellationTokenSource();
        _ = AnimateWorkspaceSubtitleScrollAsync(scrollViewer, targetIndex, _workspaceListScrollAnimation.Token);
    }

    private async Task AnimateWorkspaceSubtitleScrollAsync(ScrollViewer scrollViewer, int targetIndex, CancellationToken token)
    {
        var totalCount = Math.Max(1, WorkspaceSubtitleListBox.ItemCount > 0
            ? WorkspaceSubtitleListBox.ItemCount
            : _workspaceCues.Count);
        var extentHeight = Math.Max(scrollViewer.Extent.Height, totalCount * 52d);
        var averageItemHeight = extentHeight / totalCount;
        var targetCenterY = (targetIndex + 0.5) * averageItemHeight;
        var maximum = Math.Max(0, extentHeight - scrollViewer.Viewport.Height);
        var targetY = Math.Clamp(targetCenterY - scrollViewer.Viewport.Height / 2d, 0, maximum);
        var startY = scrollViewer.Offset.Y;
        if (Math.Abs(targetY - startY) < 1) return;
        var timer = Stopwatch.StartNew();
        const double durationMs = 240;
        try
        {
            while (timer.Elapsed.TotalMilliseconds < durationMs)
            {
                token.ThrowIfCancellationRequested();
                var progress = Math.Clamp(timer.Elapsed.TotalMilliseconds / durationMs, 0, 1);
                var eased = 1 - Math.Pow(1 - progress, 3);
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, startY + (targetY - startY) * eased);
                await Task.Delay(16, token);
            }
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetY);
        }
        catch (OperationCanceledException) { }
    }

    private void SyncActivePlaybackCueToList(double seconds)
    {
        if (_workspaceActiveRightView != "list" || _workspaceCues.Count == 0 || _workspaceTimelineInteractionActive) return;
        var ms = (long)(seconds * 1000);
        if (ms >= _workspaceActiveCueValidFrom && ms <= _workspaceActiveCueValidThrough) return;
        if (_workspaceCues.Any(c => c.IsEditing)) return;

        var snapshot = WorkspaceTimeline.GetActiveCueSnapshot(ms);
        _workspaceActiveCueValidFrom = snapshot.ValidFromMilliseconds;
        _workspaceActiveCueValidThrough = snapshot.ValidThroughMilliseconds;
        var nextActiveIndexes = snapshot.CueIndexes;
        var nextActiveSet = nextActiveIndexes.Count == 0 ? null : nextActiveIndexes.ToHashSet();
        foreach (var oldIndex in _workspaceActiveCueIndexes)
        {
            if (nextActiveSet?.Contains(oldIndex) == true || oldIndex < 0 || oldIndex >= _workspaceCues.Count) continue;
            _workspaceCues[oldIndex].IsActive = false;
        }
        foreach (var activeIndex in nextActiveIndexes)
            if (activeIndex >= 0 && activeIndex < _workspaceCues.Count) _workspaceCues[activeIndex].IsActive = true;
        _workspaceActiveCueIndexes.Clear();
        foreach (var activeIndex in nextActiveIndexes) _workspaceActiveCueIndexes.Add(activeIndex);

        if (nextActiveIndexes.Count > 0)
        {
            var firstActiveIndex = nextActiveIndexes[0];
            var first = _workspaceCues[firstActiveIndex];
            var groupKey = !string.IsNullOrWhiteSpace(first.GroupId)
                ? first.GroupId
                : $"{first.StartMilliseconds}:{first.EndMilliseconds}";
            if (string.Equals(groupKey, _workspaceActiveListGroupKey, StringComparison.Ordinal)) return;
            _workspaceActiveListGroupKey = groupKey;
            try
            {
                _workspaceSyncingListSelection = true;
                WorkspaceSubtitleListBox.SelectedItems?.Clear();
                foreach (var activeIndex in nextActiveIndexes)
                {
                    WorkspaceSubtitleListBox.SelectedItems?.Add(_workspaceCues[activeIndex]);
                }
            }
            finally
            {
                _workspaceSyncingListSelection = false;
            }

            if (firstActiveIndex >= 0 && _workspacePlayer != null && _workspacePlayer.IsRunning && !_workspacePlayer.IsPaused)
            {
                QueueWorkspaceSubtitleScroll(firstActiveIndex);
            }
        }
        else
        {
            _workspaceActiveListGroupKey = null;
            if (WorkspaceSubtitleListBox.SelectedItems?.Count > 0)
            {
                try
                {
                    _workspaceSyncingListSelection = true;
                    WorkspaceSubtitleListBox.SelectedItems?.Clear();
                }
                finally
                {
                    _workspaceSyncingListSelection = false;
                }
            }
        }
    }

    private void WorkspaceTimeline_OnCueEdited(object? sender, TimelineCueEdit edit)
    {
        InvalidateWorkspaceCueTimingIndex();
        _workspaceUndo.Push(new WorkspaceHistoryCommand(
            () => ApplyWorkspaceTimelineEdit(edit, useNewValues: false),
            () => ApplyWorkspaceTimelineEdit(edit, useNewValues: true)));
        _workspaceRedo.Clear();
        RefreshWorkspaceHistoryButtons();
        ScheduleWorkspaceAutoSave();
    }

    private void WorkspaceCue_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The timeline already invalidates once per pointer frame.  Refreshing and
        // restarting the autosave timer for every property notification made a
        // captured drag feel sticky, especially when moving a subtitle group.
        if (e.PropertyName is nameof(EditorSubtitleCue.StartMilliseconds) or nameof(EditorSubtitleCue.EndMilliseconds)
            or nameof(EditorSubtitleCue.TrackIndex))
            InvalidateWorkspaceCueTimingIndex();
        if (_workspaceTimelineInteractionActive) return;
        WorkspaceTimeline.Refresh();
        ScheduleWorkspaceAutoSave();
    }

    private void OpenWorkspaceCueInlineEditor(int index)
    {
        if (index < 0 || index >= _workspaceCues.Count) return;
        var cue = _workspaceCues[index];
        _workspaceInlineEditingCueIndex = index;
        _workspaceInlineEditingTranslated = !string.IsNullOrWhiteSpace(cue.Translated);
        WorkspaceCueEditTextBox.Text = _workspaceInlineEditingTranslated ? cue.Translated : cue.Original;
        WorkspaceCueEditCountText.Text = (WorkspaceCueEditTextBox.Text?.Length ?? 0).ToString(CultureInfo.InvariantCulture);
        WorkspaceCueInlineEditor.IsVisible = true;
        PositionWorkspaceCueInlineEditor();
        Dispatcher.UIThread.Post(() =>
        {
            WorkspaceCueEditTextBox.Focus();
            WorkspaceCueEditTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void PositionWorkspaceCueInlineEditor()
    {
        if (!WorkspaceCueInlineEditor.IsVisible || _workspaceInlineEditingCueIndex < 0) return;
        var anchor = WorkspaceTimeline.GetCueVisualBounds(_workspaceInlineEditingCueIndex);
        if (anchor.Width <= 0 || anchor.Height <= 0) return;
        var availableWidth = Math.Max(240, WorkspaceTimeline.Bounds.Width);
        var editorWidth = Math.Min(520, Math.Max(300, availableWidth - 12));
        WorkspaceCueInlineEditor.Width = editorWidth;
        var left = Math.Clamp(anchor.Left, 6, Math.Max(6, availableWidth - editorWidth - 6));
        var top = anchor.Bottom + 4;
        if (top + WorkspaceCueInlineEditor.Height > WorkspaceTimeline.Bounds.Height - 4)
            top = Math.Max(4, anchor.Top - WorkspaceCueInlineEditor.Height - 4);
        Canvas.SetLeft(WorkspaceCueInlineEditor, left);
        Canvas.SetTop(WorkspaceCueInlineEditor, top);
    }

    private void WorkspaceCueEditText_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        WorkspaceCueEditCountText.Text = (WorkspaceCueEditTextBox.Text?.Length ?? 0)
            .ToString(CultureInfo.InvariantCulture);

    private void WorkspaceCueEditText_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWorkspaceCueInlineEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            CommitWorkspaceCueInlineEditor();
            e.Handled = true;
        }
    }

    private void WorkspaceCueEditConfirm_OnClick(object? sender, RoutedEventArgs e) =>
        CommitWorkspaceCueInlineEditor();

    private void WorkspaceCueEditCancel_OnClick(object? sender, RoutedEventArgs e) =>
        CloseWorkspaceCueInlineEditor();

    private void CommitWorkspaceCueInlineEditor()
    {
        var index = _workspaceInlineEditingCueIndex;
        if (index < 0 || index >= _workspaceCues.Count)
        {
            CloseWorkspaceCueInlineEditor();
            return;
        }
        var cue = _workspaceCues[index];
        var text = WorkspaceCueEditTextBox.Text?.Trim() ?? string.Empty;
        if (_workspaceInlineEditingTranslated) cue.Translated = text;
        else cue.Original = text;
        WorkspaceTimeline.Refresh();
        SaveWorkspaceSubtitle();
        CloseWorkspaceCueInlineEditor();
    }

    private void CloseWorkspaceCueInlineEditor()
    {
        _workspaceInlineEditingCueIndex = -1;
        WorkspaceCueInlineEditor.IsVisible = false;
        WorkspaceTimeline.Focus();
    }

    private void WorkspaceUndo_OnClick(object? sender, RoutedEventArgs e)
        => UndoWorkspaceEdit();

    private void UndoWorkspaceEdit()
    {
        if (_workspaceUndo.Count == 0) return;
        var command = _workspaceUndo.Pop();
        command.Undo();
        _workspaceRedo.Push(command);
        RefreshWorkspaceHistoryButtons();
        ScheduleWorkspaceAutoSave();
    }

    private void WorkspaceRedo_OnClick(object? sender, RoutedEventArgs e)
        => RedoWorkspaceEdit();

    private void RedoWorkspaceEdit()
    {
        if (_workspaceRedo.Count == 0) return;
        var command = _workspaceRedo.Pop();
        command.Redo();
        _workspaceUndo.Push(command);
        RefreshWorkspaceHistoryButtons();
        ScheduleWorkspaceAutoSave();
    }

    private void ApplyWorkspaceTimelineEdit(TimelineCueEdit edit, bool useNewValues)
    {
        foreach (var change in edit.Changes)
            ApplyWorkspaceEdit(change.Index,
                useNewValues ? change.NewStart : change.OldStart,
                useNewValues ? change.NewEnd : change.OldEnd,
                useNewValues ? change.NewTrack : change.OldTrack);
    }

    private void ApplyWorkspaceEdit(int index, long start, long end, int track = -1)
    {
        if (index < 0 || index >= _workspaceCues.Count) return;
        _workspaceCues[index].StartMilliseconds = start;
        _workspaceCues[index].EndMilliseconds = end;
        if (track >= 0) _workspaceCues[index].TrackIndex = Math.Clamp(track, 0, Math.Max(0, WorkspaceTimeline.TrackCount - 1));
        WorkspaceTimeline.Refresh();
    }

    private void RefreshWorkspaceHistoryButtons()
    {
        WorkspaceUndoAction.IsEnabled = _workspaceUndo.Count > 0;
        WorkspaceRedoAction.IsEnabled = _workspaceRedo.Count > 0;
    }

    private void ScheduleWorkspaceAutoSave()
    {
        MarkWorkspaceDirty();
        _workspaceAutoSaveTimer.Stop();
        _workspaceAutoSaveTimer.Start();
    }

    private void MarkWorkspaceDirty()
    {
        _workspaceHasPendingSave = true;
        Interlocked.Increment(ref _workspaceSaveRevision);
        WorkspaceSaveStateText.Text = "正在自动保存…";
        WorkspaceSaveStateText.Foreground = Brush.Parse("#6F7883");
    }

    private async void WorkspaceExport_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null || string.IsNullOrWhiteSpace(project.SourceVideoPath) || !File.Exists(project.SourceVideoPath))
        {
            WorkspaceSaveStateText.Text = "请先导入可用的视频或音频";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#E15959");
            return;
        }

        SaveWorkspaceSubtitle();
        var styledSubtitle = CreateWorkspaceAss(project);
        var sourceDirectory = Path.GetDirectoryName(project.SourceVideoPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var suggested = Path.Combine(sourceDirectory, $"{SanitizeFileName(project.Name)}-导出.mp4");
        var result = await WorkspaceExportDialog.ShowAsync(this,
            new WorkspaceExportRequest(project.SourceVideoPath, project.Name, suggested, styledSubtitle, project.SubtitlePath));
        if (result?.Succeeded == true)
        {
            WorkspaceSaveStateText.Text = $"导出完成：{Path.GetFileName(result.OutputPath)}";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#4FAE83");
        }
        else if (result is { Cancelled: false, ErrorMessage: not null })
        {
            WorkspaceSaveStateText.Text = $"导出失败：{result.ErrorMessage}";
            WorkspaceSaveStateText.Foreground = Brush.Parse("#E15959");
        }
    }

    private void WorkspaceFullscreen_OnClick(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _workspaceWindowStateBeforeFullscreen;
            ApplyWorkspaceFullscreenLayout(false);
            WorkspaceFullscreenIcon.Kind = Material.Icons.MaterialIconKind.Fullscreen;
            if (sender is Control exitButton) ToolTip.SetTip(exitButton, "全屏工作区");
            return;
        }

        _workspaceWindowStateBeforeFullscreen = WindowState;
        ApplyWorkspaceFullscreenLayout(true);
        WindowState = WindowState.FullScreen;
        WorkspaceFullscreenIcon.Kind = Material.Icons.MaterialIconKind.FullscreenExit;
        if (sender is Control enterButton) ToolTip.SetTip(enterButton, "退出全屏");
    }

    private void SaveWorkspaceSubtitle()
    {
        MarkWorkspaceDirty();
        _workspaceAutoSaveTimer.Stop();
        _ = SaveWorkspaceSubtitleAsync();
    }

    private static string FormatWorkspaceTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private string CreateWorkspaceAss(CaptionProject project)
    {
        EnsureProjectStyleLibrary(project);
        var cacheDirectory = Path.Combine(_deployment.RuntimeRoot, "cache", "subtitles", project.Id);
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, "preview.ass");
        var trackCount = Math.Max(1, project.SubtitleTrackCount);
        var trackStyles = Enumerable.Range(0, trackCount)
            .Select(track => GetWorkspaceStyleForTrack(project, track))
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]").AppendLine("ScriptType: v4.00+")
            .AppendLine("PlayResX: 1920").AppendLine("PlayResY: 1080").AppendLine("ScaledBorderAndShadow: yes")
            .AppendLine().AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        for (var track = 0; track < trackStyles.Length; track++)
            builder.AppendLine(AssStyle($"Track{track + 1}", trackStyles[track]));
        builder.AppendLine().AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        foreach (var cue in _workspaceCues)
        {
            if (WorkspaceTimeline.IsTrackMuted(cue.TrackIndex)) continue;
            var start = FormatAssTime(cue.StartMilliseconds);
            var end = FormatAssTime(cue.EndMilliseconds);
            var track = Math.Clamp(cue.TrackIndex, 0, trackStyles.Length - 1);
            var text = cue.DisplayText;
            AppendStyledAssDialogue(builder, start, end, $"Track{track + 1}", trackStyles[track], text, track);
        }
        WriteTextAtomically(path, builder.ToString());
        return path;
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var stagingPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(stagingPath, content, new UTF8Encoding(false));
            File.Move(stagingPath, path, true);
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
        }
    }

    internal static string BuildSubtitleStylePreviewAss(SubtitleStyleDefinition style, string text)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]").AppendLine("ScriptType: v4.00+")
            .AppendLine("PlayResX: 1920").AppendLine("PlayResY: 1080").AppendLine("ScaledBorderAndShadow: yes")
            .AppendLine().AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .AppendLine(AssStyle("Preview", style))
            .AppendLine().AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        AppendStyledAssDialogue(builder, "0:00:00.00", "9:59:59.99", "Preview", style, text);
        return builder.ToString();
    }

    private static string AssStyle(string name, SubtitleStyleDefinition style) =>
        string.Join(',', "Style: " + name, style.FontFamily, style.FontSize.ToString("0.##", CultureInfo.InvariantCulture),
            AssColor(style.TextColor), AssColor(style.TextColor),
            style.Boxed ? AssBackColor(style) : AssColor(style.OutlineColor), AssBackColor(style),
            style.Bold ? "-1" : "0", style.Italic ? "-1" : "0", style.Underline ? "-1" : "0", "0", "100", "100", "0", "0",
            style.Boxed ? "3" : "1", (style.Boxed ? style.BoxPadding : style.OutlineWidth).ToString("0.##", CultureInfo.InvariantCulture),
            (style.Boxed ? 0 : style.ShadowDistance).ToString("0.##", CultureInfo.InvariantCulture), AssAlignment(style.Alignment),
            style.HorizontalMargin.ToString("0", CultureInfo.InvariantCulture),
            style.HorizontalMargin.ToString("0", CultureInfo.InvariantCulture),
            style.VerticalMargin.ToString("0", CultureInfo.InvariantCulture), "1");

    private static string AssColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6) return "&H00FFFFFF";
        return $"&H00{value[4..6]}{value[2..4]}{value[0..2]}";
    }

    private static string AssBackColor(SubtitleStyleDefinition style)
    {
        var value = style.BoxColor.TrimStart('#');
        if (value.Length != 6) value = "000000";
        var alpha = (byte)Math.Round(255 * (1 - Math.Clamp(style.BoxOpacity, 0, 100) / 100));
        return $"&H{alpha:X2}{value[4..6]}{value[2..4]}{value[0..2]}";
    }

    private static void AppendStyledAssDialogue(StringBuilder builder, string start, string end,
        string styleName, SubtitleStyleDefinition style, string text, int layer = 0)
    {
        builder.Append("Dialogue: ").Append(Math.Max(0, layer)).Append(',').Append(start).Append(',').Append(end).Append(',').Append(styleName)
            .Append(",,0,0,0,,").AppendLine(EscapeAssText(text));
    }

    private static int AssAlignment(string alignment) =>
        alignment.StartsWith("顶部", StringComparison.Ordinal) ? (alignment.EndsWith("居左", StringComparison.Ordinal) ? 7 : alignment.EndsWith("居右", StringComparison.Ordinal) ? 9 : 8) :
        alignment.StartsWith("中部", StringComparison.Ordinal) ? (alignment.EndsWith("居左", StringComparison.Ordinal) ? 4 : alignment.EndsWith("居右", StringComparison.Ordinal) ? 6 : 5) :
        alignment.EndsWith("居左", StringComparison.Ordinal) ? 1 : alignment.EndsWith("居右", StringComparison.Ordinal) ? 3 : 2;

    private static string FormatAssTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"h\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    private static string EscapeAssText(string text) => text.Replace("\r", string.Empty).Replace("\n", "\\N").Replace("{", "\\{");

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "AstraCat" : name.Trim();
    }

    private async void WorkspaceExit_OnClick(object? sender, RoutedEventArgs e)
    {
        WorkspaceVideoHost.UpdateNativeVisibility(false);
        WorkspaceVideoHost.IsVisible = false;
        ProjectWorkspaceView.IsVisible = false;
        await SwitchProjectSectionAsync(_workspaceReturnSection);
    }

    private static bool IsAudioOnlyMedia(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Path.GetExtension(path).ToLowerInvariant() is ".m4a" or ".mp3" or ".wav" or ".flac" or
            ".aac" or ".ogg" or ".opus" or ".wma";
    }

        private void SetWorkspaceMode(bool active)
    {
        if (!active)
        {
            WorkspaceVideoHost?.HideImmediate();
            if (WorkspaceVideoHost != null) WorkspaceVideoHost.IsVisible = false;
            if (WorkspaceTimeline.SplitMode) SetWorkspaceSplitMode(false);
            if (_workspaceFullscreenLayout)
            {
                if (WindowState == WindowState.FullScreen)
                    WindowState = _workspaceWindowStateBeforeFullscreen;
                WorkspaceFullscreenIcon.Kind = Material.Icons.MaterialIconKind.Fullscreen;
            }
        }
        else
        {
            var audioOnly = IsAudioOnlyMedia(_workspaceMediaPath);
            WorkspaceVideoHost.IsVisible = !audioOnly;
            WorkspaceAudioOnlyPlaceholder.IsVisible = audioOnly;
            WorkspaceVideoHost.UpdateNativeVisibility(!audioOnly);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_userAdjustedVideoColumnWidth) AutoFitWorkspaceVideoColumn();
            }, DispatcherPriority.Render);
        }
        ApplyWorkspaceFullscreenLayout(false);
    }

    private Task TransitionWorkspaceModeAsync(bool active, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SetWorkspaceMode(active);
        return Task.CompletedTask;
    }

    private void ApplyWorkspaceFullscreenLayout(bool fullscreen)
    {
        _workspaceFullscreenLayout = fullscreen;
        ProjectHeader.IsVisible = !fullscreen;
        ProjectHeaderDivider.IsVisible = !fullscreen;
        SetSidebarCollapsed(fullscreen);
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        _workspaceSidebarCollapsed = collapsed;
        SidebarBorder.Opacity = 1;
        SidebarDivider.Opacity = 1;
        SidebarBorder.IsVisible = !collapsed;
        SidebarDivider.IsVisible = !collapsed;
        AppShellGrid.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(collapsed ? 0 : 72);
        AppShellGrid.ColumnDefinitions[1].Width = new Avalonia.Controls.GridLength(collapsed ? 0 : 1);
    }

    private bool _isEditingSubtitleStyle;

    private void WorkspaceViewTab_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        SetWorkspaceRightView(tag);
    }

    private int _workspaceSelectedTrackFilter = -1;

    private void SetWorkspaceRightView(string view)
    {
        _workspaceActiveRightView = view;
        var isStyle = view == "style";
        WorkspaceStyleCardView.IsVisible = isStyle;
        WorkspaceListCardView.IsVisible = !isStyle;
        WorkspaceListHeaderActions.IsVisible = !isStyle;
        WorkspaceStyleHeaderActions.IsVisible = isStyle;

        if (WorkspaceViewStyleTabIcon != null && WorkspaceViewStyleTabIndicator != null)
        {
            WorkspaceViewStyleTabIcon.Foreground = Brush.Parse(isStyle ? "#0089FF" : "#8C97A4");
            WorkspaceViewStyleTabIndicator.Background = Brush.Parse(isStyle ? "#0089FF" : "Transparent");
        }

        if (WorkspaceViewListTabIcon != null && WorkspaceViewListTabIndicator != null)
        {
            WorkspaceViewListTabIcon.Foreground = Brush.Parse(isStyle ? "#8C97A4" : "#0089FF");
            WorkspaceViewListTabIndicator.Background = Brush.Parse(isStyle ? "Transparent" : "#0089FF");
        }

        WorkspaceRightPanelTitle.Text = isStyle ? "字幕样式" : "字幕列表";
        if (isStyle)
        {
            var project = _activeProjectId is null ? null : _projects.FirstOrDefault(item => item.Id == _activeProjectId);
            WorkspaceRightPanelCount.Text = $"{project?.SubtitleStyles?.Count ?? 0} 个";
        }
        else
        {
            WorkspaceRightPanelCount.Text = $"{_workspaceCues.Count} 条";
        }

        if (!isStyle)
        {
            ApplyWorkspaceSubtitleFilter();
            if (_workspaceSelectedCueIndex >= 0 && _workspaceSelectedCueIndex < _workspaceCues.Count)
            {
                WorkspaceSubtitleListBox.SelectedIndex = _workspaceSelectedCueIndex;
                WorkspaceSubtitleListBox.ScrollIntoView(_workspaceCues[_workspaceSelectedCueIndex]);
            }
        }
    }

    private void WorkspaceListTrackFilterCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceSubtitleListBox == null || _workspaceCues == null) return;
        var combo = sender as ComboBox ?? WorkspaceListTrackFilterCombo;
        if (combo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            int.TryParse(tag, out var trackFilter))
        {
            _workspaceSelectedTrackFilter = trackFilter;
            ApplyWorkspaceSubtitleFilter();
        }
    }

    private readonly List<int> _workspaceSearchMatches = new();
    private int _workspaceCurrentMatchIndex = -1;

    private void WorkspaceSubtitleSearchToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (WorkspaceSubtitleSearchReplacePanel.IsVisible && !WorkspaceSubtitleReplaceRow.IsVisible)
        {
            CloseWorkspaceSearchReplace();
        }
        else
        {
            OpenWorkspaceSearchReplace(showReplace: false);
        }
    }

    private void WorkspaceSubtitleReplaceToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (WorkspaceSubtitleSearchReplacePanel.IsVisible && WorkspaceSubtitleReplaceRow.IsVisible)
        {
            CloseWorkspaceSearchReplace();
        }
        else
        {
            OpenWorkspaceSearchReplace(showReplace: true);
        }
    }

    private void OpenWorkspaceSearchReplace(bool showReplace)
    {
        if (_workspaceActiveRightView != "list") SetWorkspaceRightView("list");
        WorkspaceSubtitleSearchReplacePanel.IsVisible = true;
        WorkspaceSubtitleSearchReplacePanel.IsHitTestVisible = true;
        WorkspaceSubtitleSearchRow.IsVisible = true;
        WorkspaceSubtitleReplaceRow.IsVisible = showReplace;

        // Trigger transition from translateY(-8px) scale(0.96) to normal
        Dispatcher.UIThread.Post(() =>
        {
            WorkspaceSubtitleSearchReplacePanel.Opacity = 1;
            WorkspaceSubtitleSearchReplacePanel.RenderTransform = TransformOperations.Parse("translate(0px, 0px)");
        }, DispatcherPriority.Render);

        if (showReplace)
        {
            WorkspaceSubtitleReplaceToggleBtn.Background = Brush.Parse("#E0F2FE");
            WorkspaceSubtitleReplaceToggleBtn.Foreground = Brush.Parse("#0089FF");
            WorkspaceSubtitleSearchToggleBtn.Background = Brush.Parse("#F0F2F4");
            WorkspaceSubtitleSearchToggleBtn.Foreground = Brush.Parse("#55606E");
            if (string.IsNullOrEmpty(WorkspaceSubtitleSearchBox.Text))
                WorkspaceSubtitleSearchBox.Focus();
            else
                WorkspaceSubtitleReplaceBox.Focus();
        }
        else
        {
            WorkspaceSubtitleSearchToggleBtn.Background = Brush.Parse("#E0F2FE");
            WorkspaceSubtitleSearchToggleBtn.Foreground = Brush.Parse("#0089FF");
            WorkspaceSubtitleReplaceToggleBtn.Background = Brush.Parse("#F0F2F4");
            WorkspaceSubtitleReplaceToggleBtn.Foreground = Brush.Parse("#55606E");
            WorkspaceSubtitleSearchBox.Focus();
        }

        ApplyWorkspaceSubtitleFilter();
    }

    private void WorkspaceSubtitleCloseSearch_OnClick(object? sender, RoutedEventArgs e)
    {
        CloseWorkspaceSearchReplace();
    }

    private void CloseWorkspaceSearchReplace()
    {
        WorkspaceSubtitleSearchBox.Text = string.Empty;
        WorkspaceSubtitleReplaceBox.Text = string.Empty;

        WorkspaceSubtitleSearchReplacePanel.Opacity = 0;
        WorkspaceSubtitleSearchReplacePanel.RenderTransform = TransformOperations.Parse("translate(0px, -8px) scale(0.96)");
        WorkspaceSubtitleSearchReplacePanel.IsHitTestVisible = false;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            WorkspaceSubtitleSearchReplacePanel.IsVisible = false;
        };
        timer.Start();

        WorkspaceSubtitleSearchToggleBtn.Background = Brush.Parse("#F0F2F4");
        WorkspaceSubtitleSearchToggleBtn.Foreground = Brush.Parse("#55606E");
        WorkspaceSubtitleReplaceToggleBtn.Background = Brush.Parse("#F0F2F4");
        WorkspaceSubtitleReplaceToggleBtn.Foreground = Brush.Parse("#55606E");
        _workspaceSearchMatches.Clear();
        _workspaceCurrentMatchIndex = -1;
        ApplyWorkspaceSubtitleFilter();
    }

    private void WorkspaceSubtitleSearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                JumpToPrevSearchMatch();
            else
                JumpToNextSearchMatch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseWorkspaceSearchReplace();
            e.Handled = true;
        }
    }

    private void WorkspaceSubtitleReplaceBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            WorkspaceSubtitleReplaceOne_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseWorkspaceSearchReplace();
            e.Handled = true;
        }
    }

    private void WorkspaceSubtitlePrevMatch_OnClick(object? sender, RoutedEventArgs e)
    {
        JumpToPrevSearchMatch();
    }

    private void WorkspaceSubtitleNextMatch_OnClick(object? sender, RoutedEventArgs e)
    {
        JumpToNextSearchMatch();
    }

    private void JumpToNextSearchMatch()
    {
        if (_workspaceSearchMatches.Count == 0) return;
        _workspaceCurrentMatchIndex = (_workspaceCurrentMatchIndex + 1) % _workspaceSearchMatches.Count;
        NavigateToMatch(_workspaceCurrentMatchIndex);
    }

    private void JumpToPrevSearchMatch()
    {
        if (_workspaceSearchMatches.Count == 0) return;
        _workspaceCurrentMatchIndex = (_workspaceCurrentMatchIndex - 1 + _workspaceSearchMatches.Count) % _workspaceSearchMatches.Count;
        NavigateToMatch(_workspaceCurrentMatchIndex);
    }

    private void NavigateToMatch(int matchIdx)
    {
        if (matchIdx < 0 || matchIdx >= _workspaceSearchMatches.Count) return;
        var cueIndex = _workspaceSearchMatches[matchIdx];
        if (cueIndex < 0 || cueIndex >= _workspaceCues.Count) return;

        var cue = _workspaceCues[cueIndex];
        SelectWorkspaceCue(cueIndex, fromTimeline: false);
        if (WorkspaceSubtitleListBox != null)
        {
            WorkspaceSubtitleListBox.SelectedItem = cue;
            WorkspaceSubtitleListBox.ScrollIntoView(cue);
        }
        _ = SeekWorkspaceAsync((cue.StartMilliseconds + cue.EndMilliseconds) / 2000.0);

        if (WorkspaceSubtitleMatchCountText != null)
            WorkspaceSubtitleMatchCountText.Text = $"{matchIdx + 1} / {_workspaceSearchMatches.Count}";
    }

    private void WorkspaceSubtitleSearch_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyWorkspaceSubtitleFilter();
    }

    private void ApplyWorkspaceSubtitleFilter()
    {
        if (_workspaceCues == null || _workspaceSearchMatches == null || WorkspaceSubtitleListBox == null) return;
        _workspaceSearchMatches.Clear();
        var query = WorkspaceSubtitleSearchBox?.Text?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        for (var i = 0; i < _workspaceCues.Count; i++)
        {
            var c = _workspaceCues[i];
            if (_workspaceSelectedTrackFilter >= 0 && c.TrackIndex != _workspaceSelectedTrackFilter)
                continue;

            if (hasQuery && query != null)
            {
                var matchOrig = !string.IsNullOrEmpty(c.Original) && c.Original.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchTrans = !string.IsNullOrEmpty(c.Translated) && c.Translated.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (matchOrig || matchTrans)
                {
                    _workspaceSearchMatches.Add(i);
                }
            }
            else
            {
                _workspaceSearchMatches.Add(i);
            }
        }

        var filtered = _workspaceSearchMatches.Select(idx => _workspaceCues[idx]).ToList();
        WorkspaceSubtitleListBox.ItemsSource = filtered;
        if (WorkspaceSubtitleListEmpty != null)
            WorkspaceSubtitleListEmpty.IsVisible = filtered.Count == 0;

        if (hasQuery && _workspaceSearchMatches.Count > 0)
        {
            if (_workspaceCurrentMatchIndex < 0 || _workspaceCurrentMatchIndex >= _workspaceSearchMatches.Count)
                _workspaceCurrentMatchIndex = 0;
            if (WorkspaceSubtitleMatchCountText != null)
                WorkspaceSubtitleMatchCountText.Text = $"{_workspaceCurrentMatchIndex + 1} / {_workspaceSearchMatches.Count}";
            NavigateToMatch(_workspaceCurrentMatchIndex);
        }
        else
        {
            _workspaceCurrentMatchIndex = -1;
            if (WorkspaceSubtitleMatchCountText != null)
                WorkspaceSubtitleMatchCountText.Text = string.Empty;
        }

        if (_workspaceActiveRightView == "list")
        {
            var filterLabel = _workspaceSelectedTrackFilter switch
            {
                0 => " · 轨1",
                1 => " · 轨2",
                _ => ""
            };
            WorkspaceRightPanelCount.Text = $"{filtered.Count} 条{filterLabel}";
        }
    }

    private void WorkspaceSubtitleReplaceOne_OnClick(object? sender, RoutedEventArgs e)
    {
        var search = WorkspaceSubtitleSearchBox?.Text;
        if (string.IsNullOrEmpty(search)) return;
        var replace = WorkspaceSubtitleReplaceBox?.Text ?? string.Empty;

        if (_workspaceCurrentMatchIndex < 0 || _workspaceCurrentMatchIndex >= _workspaceSearchMatches.Count) return;
        var cueIdx = _workspaceSearchMatches[_workspaceCurrentMatchIndex];
        if (cueIdx < 0 || cueIdx >= _workspaceCues.Count) return;

        var cue = _workspaceCues[cueIdx];
        var replaced = false;
        if (!string.IsNullOrEmpty(cue.Original) && cue.Original.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            cue.Original = Regex.Replace(cue.Original, Regex.Escape(search), replace, RegexOptions.IgnoreCase);
            replaced = true;
        }
        if (!string.IsNullOrEmpty(cue.Translated) && cue.Translated.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            cue.Translated = Regex.Replace(cue.Translated, Regex.Escape(search), replace, RegexOptions.IgnoreCase);
            replaced = true;
        }

        if (replaced)
        {
            WorkspaceTimeline.InvalidateVisual();
            _ = ApplyWorkspaceSubtitleStyleAsync();
            SaveWorkspaceSubtitle();
            ApplyWorkspaceSubtitleFilter();
        }
    }

    private void WorkspaceSubtitleReplaceAll_OnClick(object? sender, RoutedEventArgs e)
    {
        var search = WorkspaceSubtitleSearchBox?.Text;
        if (string.IsNullOrEmpty(search)) return;
        var replace = WorkspaceSubtitleReplaceBox?.Text ?? string.Empty;

        var replacedCount = 0;
        foreach (var cue in _workspaceCues)
        {
            if (_workspaceSelectedTrackFilter >= 0 && cue.TrackIndex != _workspaceSelectedTrackFilter)
                continue;

            if (!string.IsNullOrEmpty(cue.Original) && cue.Original.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                var newOrig = Regex.Replace(cue.Original, Regex.Escape(search), replace, RegexOptions.IgnoreCase);
                if (newOrig != cue.Original)
                {
                    cue.Original = newOrig;
                    replacedCount++;
                }
            }
            if (!string.IsNullOrEmpty(cue.Translated) && cue.Translated.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                var newTrans = Regex.Replace(cue.Translated, Regex.Escape(search), replace, RegexOptions.IgnoreCase);
                if (newTrans != cue.Translated)
                {
                    cue.Translated = newTrans;
                    replacedCount++;
                }
            }
        }

        if (replacedCount > 0)
        {
            WorkspaceTimeline.InvalidateVisual();
            ApplyWorkspaceSubtitleFilter();
            _ = ApplyWorkspaceSubtitleStyleAsync();
            SaveWorkspaceSubtitle();
            if (WorkspaceSubtitleMatchCountText != null)
                WorkspaceSubtitleMatchCountText.Text = $"已替换 {replacedCount} 处";
        }
        else
        {
            if (WorkspaceSubtitleMatchCountText != null)
                WorkspaceSubtitleMatchCountText.Text = "未找到匹配项";
        }
    }

    private void WorkspaceCueEditBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EditorSubtitleCue cue })
        {
            e.Handled = true;
            foreach (var c in _workspaceCues)
            {
                if (c != cue) c.IsEditing = false;
            }
            cue.IsEditing = true;
            _workspaceSyncingListSelection = true;
            try
            {
                WorkspaceSubtitleListBox.SelectedItem = cue;
            }
            finally
            {
                _workspaceSyncingListSelection = false;
            }
        }
    }

    private void WorkspaceCueItem_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EditorSubtitleCue cue })
        {
            e.Handled = true;
            foreach (var c in _workspaceCues)
            {
                if (c != cue) c.IsEditing = false;
            }
            cue.IsEditing = true;
            _workspaceSyncingListSelection = true;
            try
            {
                WorkspaceSubtitleListBox.SelectedItem = cue;
            }
            finally
            {
                _workspaceSyncingListSelection = false;
            }
        }
    }

    private void WorkspaceCueDoneEditing_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EditorSubtitleCue cue })
        {
            cue.IsEditing = false;
            WorkspaceTimeline.InvalidateVisual();
            SaveWorkspaceSubtitle();
            _ = ApplyWorkspaceSubtitleStyleAsync();
        }
    }

    private void WorkspaceCueCancelEditing_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EditorSubtitleCue cue })
        {
            cue.IsEditing = false;
        }
    }

    private void WorkspaceCueListItem_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (sender is Control { DataContext: EditorSubtitleCue cue })
            {
                cue.IsEditing = false;
                WorkspaceTimeline.InvalidateVisual();
                SaveWorkspaceSubtitle();
                _ = ApplyWorkspaceSubtitleStyleAsync();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (sender is Control { DataContext: EditorSubtitleCue cue })
            {
                cue.IsEditing = false;
            }
            e.Handled = true;
        }
    }

    private void WorkspaceCueListItem_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        WorkspaceTimeline.InvalidateVisual();
        _ = ApplyWorkspaceSubtitleStyleAsync();
        SaveWorkspaceSubtitle();
    }

    private void WorkspaceSubtitleList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_workspaceSyncingListSelection) return;
        if (WorkspaceSubtitleListBox.SelectedItem is EditorSubtitleCue cue)
        {
            var idx = _workspaceCues.IndexOf(cue);
            if (idx >= 0 && idx != _workspaceSelectedCueIndex)
            {
                SelectWorkspaceCue(idx, fromTimeline: false);
                _ = SeekWorkspaceAsync((cue.StartMilliseconds + cue.EndMilliseconds) / 2000.0);
            }
        }
    }

    private void SelectWorkspaceStyleGroup(int track, bool applyToPlayer)
    {
        _workspaceSelectedStyleTrack = Math.Clamp(track, 0, Math.Max(0, WorkspaceTimeline.TrackCount - 1));
        var project = _activeProjectId is null ? null : _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is not null)
        {
            EnsureProjectStyleLibrary(project);
            _workspaceSelectedStyleId = GetWorkspaceStyleForTrack(project, _workspaceSelectedStyleTrack).Id;
            RebuildWorkspaceStyleCards(project);
        }
        if (applyToPlayer) _ = ApplyWorkspaceSubtitleStyleAsync();
    }

    private void UpdateWorkspaceAudioSubtitlePreview(double seconds, bool force = false)
    {
        if (!IsAudioOnlyMedia(_workspaceMediaPath) || WorkspaceAudioSubtitlePreviewItems is null) return;
        var project = _activeProjectId is null
            ? null
            : _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;

        var milliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000d);
        if (!force && milliseconds >= _workspaceAudioPreviewValidFrom &&
            milliseconds <= _workspaceAudioPreviewValidThrough) return;

        var snapshot = WorkspaceTimeline.GetActiveCueSnapshot(milliseconds);
        _workspaceAudioPreviewValidFrom = snapshot.ValidFromMilliseconds;
        _workspaceAudioPreviewValidThrough = snapshot.ValidThroughMilliseconds;
        var activeCues = snapshot.CueIndexes
            .Where(index => index >= 0 && index < _workspaceCues.Count)
            .Select(index => _workspaceCues[index])
            .Where(cue => !WorkspaceTimeline.IsTrackMuted(cue.TrackIndex) &&
                          !string.IsNullOrWhiteSpace(cue.DisplayText))
            .OrderBy(cue => cue.TrackIndex)
            .ThenBy(cue => cue.Index)
            .ToArray();
        var signature = activeCues.Length == 0
            ? "empty"
            : string.Join("|", activeCues.Select(cue => $"{cue.Index}:{cue.TrackIndex}:{cue.DisplayText}"));
        if (!force && string.Equals(signature, _workspaceAudioPreviewSignature, StringComparison.Ordinal)) return;
        _workspaceAudioPreviewSignature = signature;

        WorkspaceAudioSubtitlePreviewItems.Children.Clear();
        if (activeCues.Length == 0)
        {
            WorkspaceAudioSubtitlePreviewItems.Children.Add(new TextBlock
            {
                Text = _workspaceCues.Count == 0 ? "尚未加载字幕" : "字幕将在播放时显示",
                Foreground = Avalonia.Media.Brush.Parse("#718090"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
            return;
        }

        foreach (var cue in activeCues)
        {
            var style = GetWorkspaceStyleForTrack(project, cue.TrackIndex);
            var preview = CreateWorkspaceStylePreviewText(style, Avalonia.Media.Brush.Parse(style.TextColor));
            preview.Text = cue.DisplayText;
            preview.TextAlignment = TextAlignment.Center;
            preview.TextWrapping = TextWrapping.Wrap;
            preview.MaxWidth = 580;
            WorkspaceAudioSubtitlePreviewItems.Children.Add(preview);
        }
    }

    private sealed record WorkspaceAutoSaveCue(
        int Index, long Start, long End, string Original, string Translated,
        int TrackIndex, string? GroupId, string? GroupName);

    private async Task<bool> SaveWorkspaceSubtitleAsync()
    {
        if (_activeProjectId is null) return true;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return true;

        var saveRevision = Volatile.Read(ref _workspaceSaveRevision);
        var projectId = project.Id;
        var directory = ProjectDirectory(projectId);
        var subtitlePath = Path.Combine(directory, "edited.srt");
        var cues = _workspaceCues.Select(cue => new WorkspaceAutoSaveCue(
            cue.Index, cue.StartMilliseconds, cue.EndMilliseconds, cue.Original, cue.Translated,
            cue.TrackIndex, cue.GroupId, cue.GroupName)).ToArray();
        var cuesJson = JsonSerializer.Serialize(cues, new JsonSerializerOptions { WriteIndented = true });
        var stateJson = JsonSerializer.Serialize(cues.Select(cue => new WorkspaceCueState
        {
            Index = cue.Index,
            TrackIndex = cue.TrackIndex,
            GroupId = cue.GroupId,
            GroupName = cue.GroupName ?? string.Empty
        }).ToArray(), new JsonSerializerOptions { WriteIndented = true });

        project.SubtitlePath = subtitlePath;
        project.UpdatedAt = DateTimeOffset.Now;
        _workspaceSubtitlePath = subtitlePath;
        var projectsJson = JsonSerializer.Serialize(_projects, new JsonSerializerOptions { WriteIndented = true });

        var srtText = BuildWorkspaceSrt(cues);
        var updatedSegments = ParseSrt(srtText);
        _projectTranslationSegments.Clear();
        _projectTranslationSegments.AddRange(updatedSegments);
        SaveProjectTranslationCache(projectId);

        await _workspaceAutoSaveGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                WriteTextAtomically(subtitlePath, srtText);
                WriteTextAtomically(WorkspaceCueStatePath(projectId), stateJson);
                WriteTextAtomically(WorkspaceCuesPath(projectId), cuesJson);
                var projectStoreDirectory = Path.GetDirectoryName(ProjectStorePath)!;
                Directory.CreateDirectory(projectStoreDirectory);
                WriteTextAtomically(ProjectStorePath, projectsJson);
            });

            if (string.Equals(_activeProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                if (_workspacePlayer.IsRunning && !_isClosing)
                    await ApplyWorkspaceSubtitleStyleAsync();
                else
                    CreateWorkspaceAss(project);
            }
            _workspaceLastSaveError = null;
            if (saveRevision == Volatile.Read(ref _workspaceSaveRevision))
            {
                _workspaceHasPendingSave = false;
                WorkspaceSaveStateText.Text = "已自动保存";
                WorkspaceSaveStateText.Foreground = Brush.Parse("#6F7883");
            }
            return true;
        }
        catch (Exception ex)
        {
            _workspaceHasPendingSave = true;
            _workspaceLastSaveError = ex.Message;
            WorkspaceSaveStateText.Text = $"保存失败：{ex.Message}";
            WorkspaceSaveStateText.Foreground = Avalonia.Media.Brush.Parse("#E15959");
            return false;
        }
        finally
        {
            _workspaceAutoSaveGate.Release();
        }
    }

    private static string BuildWorkspaceSrt(IReadOnlyList<WorkspaceAutoSaveCue> cues)
    {
        var exportCues = cues
            .GroupBy(cue => string.IsNullOrWhiteSpace(cue.GroupId) ? $"cue-{cue.Index}" : cue.GroupId)
            .Select(group => new
            {
                Start = group.Min(cue => cue.Start),
                End = group.Max(cue => cue.End),
                Translated = group.Select(cue => cue.Translated).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty,
                Original = group.Select(cue => cue.Original).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty
            })
            .OrderBy(cue => cue.Start)
            .ToList();
        var builder = new StringBuilder();
        for (var index = 0; index < exportCues.Count; index++)
        {
            var cue = exportCues[index];
            builder.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTime(cue.Start)).Append(" --> ").AppendLine(FormatSrtTime(cue.End));
            var text = string.IsNullOrWhiteSpace(cue.Translated)
                ? cue.Original
                : string.IsNullOrWhiteSpace(cue.Original) ? cue.Translated : $"{cue.Translated}\n{cue.Original}";
            builder.AppendLine(text.Trim()).AppendLine();
        }
        return builder.ToString();
    }

    private void SelectWorkspaceStyle(CaptionProject project, SubtitleStyleDefinition style)
    {
        _workspaceSelectedStyleId = style.Id;
        var boundTrack = project.SubtitleTrackStyleIds.FindIndex(id => id == style.Id);
        if (boundTrack >= 0) _workspaceSelectedStyleTrack = boundTrack;
        RebuildWorkspaceStyleCards(project);
    }

    private void RebuildWorkspaceStyleCards(CaptionProject project)
    {
        if (WorkspaceStyleItemsPanel is null) return;
        WorkspaceStyleItemsPanel.Children.Clear();
        foreach (var style in project.SubtitleStyles)
        {
            var isSelected = style.Id == _workspaceSelectedStyleId;
            var boundTracks = project.SubtitleTrackStyleIds
                .Take(WorkspaceTimeline.TrackCount)
                .Select((id, track) => (id, track))
                .Where(item => item.id == style.Id)
                .Select(item => item.track)
                .ToArray();
            var isBound = boundTracks.Length > 0;
            var isL1Default = style.Id == "main";
            var isL2Default = style.Id == "secondary";
            var bindingBorder = isBound ? WorkspaceTimeline.GetTrackColor(boundTracks[0]) : "#DDE2E8";
            var boundTrackLabel = string.Join("/", boundTracks.Select(track => $"L{track + 1}"));
            var bindingLabel = isL1Default && isBound ? $"{boundTrackLabel}组默认"
                : isL2Default && isBound ? $"{boundTrackLabel}组默认"
                : isL1Default ? "中文默认"
                : isL2Default ? "英文默认"
                : isBound ? $"{boundTrackLabel}组当前"
                : style.Boxed ? "背景框"
                : style.OutlineWidth > 0.05 ? "描边"
                : "纯文字";
            var textColor = Brush.Parse(string.IsNullOrWhiteSpace(style.TextColor) ? "#FFFFFF" : style.TextColor);
            var outlineColor = Brush.Parse(string.IsNullOrWhiteSpace(style.OutlineColor) ? "#22263B" : style.OutlineColor);
            var bindingThickness = isBound ? 3d : 1d;

            var card = new Border
            {
                Height = 116,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(10),
                Background = Brush.Parse(bindingBorder),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(bindingThickness),
                Cursor = new Cursor(StandardCursorType.Hand),
                ClipToBounds = true
            };
            ToolTip.SetTip(card, style.Summary);
            var contextItems = new List<object>();
            for (var track = 0; track < WorkspaceTimeline.TrackCount; track++)
            {
                var targetTrack = track;
                var trackItem = new MenuItem
                {
                    Header = $"绑定到 L{track + 1} 轨道",
                    ToggleType = MenuItemToggleType.CheckBox,
                    IsChecked = boundTracks.Contains(track)
                };
                trackItem.Click += (_, _) => BindWorkspaceStyleToTrack(project, style, targetTrack);
                contextItems.Add(trackItem);
            }
            contextItems.Add(new Separator());
            var deleteStyleItem = new MenuItem
            {
                Header = "删除该字幕样式",
                Foreground = Brush.Parse("#D64545"),
                IsEnabled = style.Id is not "main" and not "secondary" && project.SubtitleStyles.Count > 2
            };
            deleteStyleItem.Click += (_, _) => DeleteWorkspaceStyle(project, style);
            contextItems.Add(deleteStyleItem);
            card.ContextMenu = new ContextMenu { ItemsSource = contextItems };
            var grid = new Grid { RowDefinitions = new RowDefinitions("82,34") };
            var preview = new Grid
            {
                Background = Brush.Parse("#F1F3F5"),
                ClipToBounds = true
            };
            TextOptions.SetTextRenderingMode(preview, TextRenderingMode.Antialias);

            if (style.Boxed)
            {
                preview.Children.Add(new Border
                {
                    Width = 78,
                    Height = 31,
                    CornerRadius = new CornerRadius(Math.Clamp(style.BoxCornerRadius / 3, 0, 8)),
                    Background = Brush.Parse(string.IsNullOrWhiteSpace(style.BoxColor) ? "#000000" : style.BoxColor),
                    Opacity = Math.Clamp(style.BoxOpacity / 100d, 0.15, 1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var textLayer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (style.OutlineWidth > 0.05)
            {
                var radius = Math.Clamp(style.OutlineWidth * 0.78, 1.1, 2.8);
                for (var sample = 0; sample < 16; sample++)
                {
                    var angle = sample * Math.PI * 2 / 16;
                    var x = Math.Cos(angle) * radius;
                    var y = Math.Sin(angle) * radius;
                    var outlineText = CreateWorkspaceStylePreviewText(style, outlineColor);
                    outlineText.RenderTransform = new TranslateTransform(x, y);
                    textLayer.Children.Add(outlineText);
                }
            }
            textLayer.Children.Add(CreateWorkspaceStylePreviewText(style, textColor));
            preview.Children.Add(textLayer);

            var featureBadge = new Border
            {
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 6, 5, 0),
                CornerRadius = new CornerRadius(5),
                Background = Brush.Parse("#18191E"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = bindingLabel,
                    Foreground = Brushes.White,
                    FontSize = 9.5
                }
            };
            preview.Children.Add(featureBadge);
            grid.Children.Add(preview);

            var name = new TextBlock
            {
                Text = style.Name,
                Foreground = Brush.Parse(isSelected ? "#167BCB" : "#303842"),
                FontSize = 11,
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(6, 0)
            };
            Grid.SetRow(name, 1);
            grid.Children.Add(name);
            card.Child = new Border
            {
                CornerRadius = new CornerRadius(Math.Max(0, 10 - bindingThickness)),
                Background = Brushes.White,
                ClipToBounds = true,
                Child = grid
            };
            card.PointerPressed += async (_, e) =>
            {
                if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
                SelectWorkspaceStyle(project, style);
                if (e.ClickCount >= 2)
                {
                    e.Handled = true;
                    await EditWorkspaceStyleAsync(style.Id);
                }
            };
            WorkspaceStyleItemsPanel.Children.Add(card);
        }
        WorkspaceRightPanelCount.Text = $"{project.SubtitleStyles.Count} 个";
    }

    private void BindWorkspaceStyleToTrack(CaptionProject project, SubtitleStyleDefinition style, int track)
    {
        var targetTrack = Math.Clamp(track, 0, Math.Max(0, WorkspaceTimeline.TrackCount - 1));
        AssignWorkspaceStyle(project, targetTrack, style);
        _workspaceSelectedStyleTrack = targetTrack;
        _workspaceSelectedStyleId = style.Id;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        RebuildWorkspaceStyleCards(project);
        WorkspaceTimeline.SetStyleGroups(project.MainSubtitleStyle, project.SecondarySubtitleStyle);
        WorkspaceTimeline.Refresh();
        _ = ApplyWorkspaceSubtitleStyleAsync();
    }

    private static TextBlock CreateWorkspaceStylePreviewText(SubtitleStyleDefinition style, IBrush foreground)
    {
        return new TextBlock
        {
            Text = "字幕 Aa",
            Foreground = foreground,
            FontFamily = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Microsoft YaHei UI" : style.FontFamily),
            FontSize = Math.Clamp(style.FontSize * 0.42, 15, 22),
            FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            TextDecorations = style.Underline ? TextDecorations.Underline : null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static SubtitleStyleDefinition CreateWorkspaceTrackStyle(CaptionProject project, int trackIndex)
    {
        EnsureProjectStyleLibrary(project);
        var source = trackIndex == 1 ? project.SecondarySubtitleStyle : project.MainSubtitleStyle;
        var style = source.Clone();
        style.Id = Guid.NewGuid().ToString("N");
        style.Name = $"L{trackIndex + 1} 轨道样式";
        style.AccentColor = EditorSubtitleCue.TrackColors[Math.Clamp(trackIndex, 0, EditorSubtitleCue.TrackColors.Length - 1)];

        // Give a newly-created track a visibly separate default position. The
        // style remains fully editable, but it will not initially cover another
        // subtitle that is active at the same time.
        var boundStyles = project.SubtitleTrackStyleIds
            .Select(id => project.SubtitleStyles.FirstOrDefault(candidate => candidate.Id == id))
            .Where(candidate => candidate is not null && candidate.Alignment == style.Alignment)
            .Cast<SubtitleStyleDefinition>()
            .ToArray();
        if (boundStyles.Length > 0)
        {
            var spacing = style.FontSize * 1.15 + style.OutlineWidth * 2 + 10;
            style.VerticalMargin = Math.Min(900, boundStyles.Max(candidate => candidate.VerticalMargin) + spacing);
        }
        return style;
    }

    private static void EnsureProjectStyleLibrary(CaptionProject project)
    {
        project.MainSubtitleStyle ??= SubtitleStyleDefinition.MainDefault();
        project.SecondarySubtitleStyle ??= SubtitleStyleDefinition.SecondaryDefault();
        NormalizeWorkspaceDefaultStyle(project.MainSubtitleStyle);
        NormalizeWorkspaceDefaultStyle(project.SecondarySubtitleStyle);
        project.SubtitleStyles ??= [];
        foreach (var style in project.SubtitleStyles)
        {
            NormalizeWorkspaceDefaultStyle(style);
        }
        if (project.SubtitleStyles.All(style => style.Id != project.MainSubtitleStyle.Id))
            project.SubtitleStyles.Add(project.MainSubtitleStyle);
        if (project.SubtitleStyles.All(style => style.Id != project.SecondarySubtitleStyle.Id))
            project.SubtitleStyles.Add(project.SecondarySubtitleStyle);
        var chineseDefault = project.SubtitleStyles.FirstOrDefault(style => style.Id == "main");
        if (chineseDefault is null)
        {
            chineseDefault = SubtitleStyleDefinition.MainDefault();
            project.SubtitleStyles.Add(chineseDefault);
        }
        var englishDefault = project.SubtitleStyles.FirstOrDefault(style => style.Id == "secondary");
        if (englishDefault is null)
        {
            englishDefault = SubtitleStyleDefinition.SecondaryDefault();
            project.SubtitleStyles.Add(englishDefault);
        }
        if (project.SubtitleStyleDefaultsVersion < 1)
        {
            ApplyWorkspaceDefaultPreset(chineseDefault, "中文默认样式");
            ApplyWorkspaceDefaultPreset(englishDefault, "英文默认样式");
            project.MainSubtitleStyle = chineseDefault;
            project.SecondarySubtitleStyle = englishDefault;
            project.MainSubtitleStyleId = chineseDefault.Id;
            project.SecondarySubtitleStyleId = englishDefault.Id;
            project.SubtitleTrackStyleIds ??= [];
            while (project.SubtitleTrackStyleIds.Count < 2) project.SubtitleTrackStyleIds.Add("main");
            project.SubtitleTrackStyleIds[0] = chineseDefault.Id;
            project.SubtitleTrackStyleIds[1] = englishDefault.Id;
            project.SubtitleStyleDefaultsVersion = 1;
        }
        if (project.SubtitleStyleDefaultsVersion < 2)
        {
            chineseDefault.VerticalMargin = 120;
            englishDefault.VerticalMargin = 70;
            project.SubtitleStyleDefaultsVersion = 2;
        }
        if (project.SubtitleStyleDefaultsVersion < 3)
        {
            EnsureWorkspaceDefaultSubtitleOrder(chineseDefault, englishDefault);
            project.SubtitleStyleDefaultsVersion = 3;
        }
        if (project.SubtitleStyleDefaultsVersion < 4)
        {
            chineseDefault.FontSize = 70;
            chineseDefault.VerticalMargin = 120;
            englishDefault.FontSize = 50;
            englishDefault.VerticalMargin = 70;
            project.SubtitleStyleDefaultsVersion = 4;
        }
        if (string.IsNullOrWhiteSpace(project.MainSubtitleStyleId)) project.MainSubtitleStyleId = project.MainSubtitleStyle.Id;
        if (string.IsNullOrWhiteSpace(project.SecondarySubtitleStyleId)) project.SecondarySubtitleStyleId = project.SecondarySubtitleStyle.Id;
        project.SubtitleTrackStyleIds ??= [];
        if (project.SubtitleTrackStyleIds.Count > 0 && project.SubtitleTrackStyleIds[0] == "main" && project.MainSubtitleStyleId != "main")
            project.SubtitleTrackStyleIds[0] = project.MainSubtitleStyleId;
        if (project.SubtitleTrackStyleIds.Count > 1 && project.SubtitleTrackStyleIds[1] == "secondary" && project.SecondarySubtitleStyleId != "secondary")
            project.SubtitleTrackStyleIds[1] = project.SecondarySubtitleStyleId;
        var trackCount = Math.Clamp(project.SubtitleTrackCount, 2, 8);
        project.SubtitleTrackCount = trackCount;
        while (project.SubtitleTrackStyleIds.Count < trackCount)
        {
            project.SubtitleTrackStyleIds.Add(project.SubtitleTrackStyleIds.Count == 1
                ? project.SecondarySubtitleStyleId
                : project.MainSubtitleStyleId);
        }
        if (project.SubtitleTrackStyleIds.Count > trackCount)
            project.SubtitleTrackStyleIds.RemoveRange(trackCount, project.SubtitleTrackStyleIds.Count - trackCount);
        for (var track = 0; track < project.SubtitleTrackStyleIds.Count; track++)
        {
            if (project.SubtitleStyles.All(style => style.Id != project.SubtitleTrackStyleIds[track]))
                project.SubtitleTrackStyleIds[track] = track == 1 ? project.SecondarySubtitleStyleId : project.MainSubtitleStyleId;
        }
        project.MainSubtitleStyleId = project.SubtitleTrackStyleIds[0];
        if (trackCount > 1) project.SecondarySubtitleStyleId = project.SubtitleTrackStyleIds[1];
        project.MainSubtitleStyle = project.SubtitleStyles.First(style => style.Id == project.MainSubtitleStyleId);
        project.SecondarySubtitleStyle = project.SubtitleStyles.FirstOrDefault(style => style.Id == project.SecondarySubtitleStyleId)
                                         ?? project.MainSubtitleStyle;
        project.MainSubtitleStyleId = project.MainSubtitleStyle.Id;
        project.SecondarySubtitleStyleId = project.SecondarySubtitleStyle.Id;
    }

    private static void NormalizeWorkspaceDefaultStyle(SubtitleStyleDefinition style)
    {
        if (style.Id == "main" && style.Name is "主字幕" or "中文字幕" or "Default-L1")
        {
            style.Name = "中文默认样式";
            style.TextColor = "#FFFFFF";
            style.OutlineColor = "#22263B";
        }
        else if (style.Id == "secondary" && style.Name is "副字幕" or "英文字幕" or "Default-L2")
        {
            style.Name = "英文默认样式";
            style.TextColor = "#FFFFFF";
            style.OutlineColor = "#22263B";
        }
    }

    private static void ApplyWorkspaceDefaultPreset(SubtitleStyleDefinition style, string name)
    {
        style.Name = name;
        style.FontFamily = "Microsoft YaHei";
        style.TextColor = "#FFFFFF";
        style.OutlineColor = "#22263B";
        style.Bold = true;
        style.Italic = false;
        style.Underline = false;
        style.Boxed = false;
    }

    private static void EnsureWorkspaceDefaultSubtitleOrder(
        SubtitleStyleDefinition chineseDefault, SubtitleStyleDefinition englishDefault)
    {
        if (!chineseDefault.Alignment.StartsWith("底部", StringComparison.Ordinal) ||
            !englishDefault.Alignment.StartsWith("底部", StringComparison.Ordinal)) return;
        var englishVisualHeight = englishDefault.FontSize * 1.15 + englishDefault.OutlineWidth * 2;
        chineseDefault.VerticalMargin = Math.Max(
            chineseDefault.VerticalMargin,
            englishDefault.VerticalMargin + englishVisualHeight + 10);
    }

    private static SubtitleStyleDefinition GetWorkspaceStyleForTrack(CaptionProject project, int track)
    {
        EnsureProjectStyleLibrary(project);
        var targetTrack = Math.Clamp(track, 0, project.SubtitleTrackStyleIds.Count - 1);
        var styleId = project.SubtitleTrackStyleIds[targetTrack];
        return project.SubtitleStyles.FirstOrDefault(style => style.Id == styleId) ?? project.MainSubtitleStyle;
    }

    private void WorkspaceAddStyle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        EnsureProjectStyleLibrary(project);
        var style = SubtitleStyleDefinition.MainDefault();
        style.Id = Guid.NewGuid().ToString("N");
        var styleNumber = 1;
        var existingNames = project.SubtitleStyles.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        while (existingNames.Contains($"新样式 {styleNumber}")) styleNumber++;
        style.Name = $"新样式 {styleNumber}";
        project.SubtitleStyles.Add(style);
        _workspaceSelectedStyleId = style.Id;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        UpdateWorkspaceStyleGroupRows(project);
    }

    private void WorkspaceCopyStyle_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_activeProjectId is null) return;
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        if (project is null) return;
        EnsureProjectStyleLibrary(project);
        var source = project.SubtitleStyles.FirstOrDefault(style => style.Id == _workspaceSelectedStyleId)
                     ?? GetWorkspaceStyleForTrack(project, _workspaceSelectedStyleTrack);
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = source.Name + " 副本";
        project.SubtitleStyles.Add(copy);
        AssignWorkspaceStyle(project, _workspaceSelectedStyleTrack, copy);
        _workspaceSelectedStyleId = copy.Id;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        UpdateWorkspaceStyleGroupRows(project);
        WorkspaceTimeline.SetStyleGroups(project.MainSubtitleStyle, project.SecondarySubtitleStyle);
        _ = ApplyWorkspaceSubtitleStyleAsync();
    }

    private void DeleteWorkspaceStyle(CaptionProject project, SubtitleStyleDefinition current)
    {
        EnsureProjectStyleLibrary(project);
        if (current.Id is "main" or "secondary" || project.SubtitleStyles.Count <= 2) return;
        current = project.SubtitleStyles.FirstOrDefault(style => style.Id == current.Id)!;
        if (current is null) return;
        project.SubtitleStyles.Remove(current);
        var mainFallback = project.SubtitleStyles.FirstOrDefault(style => style.Id == "main") ?? project.SubtitleStyles[0];
        var secondaryFallback = project.SubtitleStyles.FirstOrDefault(style => style.Id == "secondary")
                                ?? project.SubtitleStyles[Math.Min(1, project.SubtitleStyles.Count - 1)];
        for (var track = 0; track < project.SubtitleTrackStyleIds.Count; track++)
        {
            if (project.SubtitleTrackStyleIds[track] != current.Id) continue;
            AssignWorkspaceStyle(project, track, track == 1 ? secondaryFallback : mainFallback);
        }
        _workspaceSelectedStyleId = GetWorkspaceStyleForTrack(project, _workspaceSelectedStyleTrack).Id;
        project.UpdatedAt = DateTimeOffset.Now;
        SaveProjects();
        UpdateWorkspaceStyleGroupRows(project);
        WorkspaceTimeline.SetStyleGroups(project.MainSubtitleStyle, project.SecondarySubtitleStyle);
        _ = ApplyWorkspaceSubtitleStyleAsync();
    }

    private static void AssignWorkspaceStyle(CaptionProject project, int track, SubtitleStyleDefinition style)
    {
        project.SubtitleTrackStyleIds ??= [];
        var targetTrack = Math.Clamp(track, 0, 7);
        while (project.SubtitleTrackStyleIds.Count <= targetTrack)
            project.SubtitleTrackStyleIds.Add(project.MainSubtitleStyleId);
        project.SubtitleTrackStyleIds[targetTrack] = style.Id;
        if (targetTrack == 0)
        {
            project.MainSubtitleStyle = style;
            project.MainSubtitleStyleId = style.Id;
        }
        else if (targetTrack == 1)
        {
            project.SecondarySubtitleStyle = style;
            project.SecondarySubtitleStyleId = style.Id;
        }
    }

    private void RefreshWorkspaceTrackFilterItems(int trackCount)
    {
        if (WorkspaceListTrackFilterCombo is null) return;
        var selectedTrack = _workspaceSelectedTrackFilter;
        WorkspaceListTrackFilterCombo.Items.Clear();
        WorkspaceListTrackFilterCombo.Items.Add(new ComboBoxItem { Tag = "-1", Content = "全部轨道" });
        for (var track = 0; track < Math.Max(1, trackCount); track++)
        {
            WorkspaceListTrackFilterCombo.Items.Add(new ComboBoxItem
            {
                Tag = track.ToString(CultureInfo.InvariantCulture),
                Content = $"L{track + 1} 轨道"
            });
        }
        var selectedIndex = selectedTrack >= 0 && selectedTrack < trackCount ? selectedTrack + 1 : 0;
        WorkspaceListTrackFilterCombo.SelectedIndex = selectedIndex;
    }

    private async Task EditWorkspaceStyleAsync(string styleId)
    {
        if (_isEditingSubtitleStyle) return;
        _isEditingSubtitleStyle = true;
        try
        {
            var project = _activeProjectId != null ? _projects.FirstOrDefault(item => item.Id == _activeProjectId) : null;
            if (project is null)
            {
                project = _projects.FirstOrDefault() ?? new CaptionProject { Name = "当前项目" };
            }
            EnsureProjectStyleLibrary(project);
            var current = project.SubtitleStyles.FirstOrDefault(style => style.Id == styleId);
            if (current is null) return;
            _workspaceSelectedStyleId = current.Id;
            var boundTrack = project.SubtitleTrackStyleIds.FindIndex(id => id == current.Id);
            var track = boundTrack >= 0 ? boundTrack : _workspaceSelectedStyleTrack;
            RebuildWorkspaceStyleCards(project);

            // Get cue text at current playhead position, or first cue
            var previewPosition = _workspacePlayer.PositionSeconds;
            var sampleText = GetWorkspaceStyleSampleText(track, previewPosition);
            var previewMediaPath = !string.IsNullOrWhiteSpace(_workspaceMediaPath) &&
                                   File.Exists(_workspaceMediaPath) && !IsAudioOnlyMedia(_workspaceMediaPath)
                ? _workspaceMediaPath
                : null;

            var resumeWorkspacePlayback = _workspacePlayer.IsRunning && !_workspacePlayer.IsPaused;
            if (resumeWorkspacePlayback) await _workspacePlayer.SetPauseAsync(true);
            var editor = new SubtitleStyleEditorWindow(current, null, sampleText, previewMediaPath, previewPosition);
            SubtitleStyleDefinition? edited;
            try
            {
                edited = await editor.ShowDialog<SubtitleStyleDefinition?>(this);
            }
            finally
            {
                if (resumeWorkspacePlayback && _workspacePlayer.IsRunning &&
                    ReferenceEquals(_activeProjectSectionView, ProjectWorkspaceView))
                    await _workspacePlayer.SetPauseAsync(false);
            }

            if (edited is null) return;
            edited.Id = current.Id;
            edited.Name = current.Name;
            var styleIndex = project.SubtitleStyles.FindIndex(style => style.Id == current.Id);
            if (styleIndex >= 0) project.SubtitleStyles[styleIndex] = edited;
            if (project.MainSubtitleStyleId == current.Id)
            {
                project.MainSubtitleStyle = edited;
                project.SubtitleFontFamily = edited.FontFamily;
                project.SubtitleFontSize = edited.FontSize;
                project.SubtitleTextColor = edited.TextColor;
                project.SubtitleOutlineColor = edited.OutlineColor;
                project.SubtitleOutlineWidth = edited.OutlineWidth;
            }
            if (project.SecondarySubtitleStyleId == current.Id)
            {
                project.SecondarySubtitleStyle = edited;
            }
            project.UpdatedAt = DateTimeOffset.Now;
            UpdateWorkspaceStyleGroupRows(project);
            WorkspaceTimeline.SetStyleGroups(project.MainSubtitleStyle, project.SecondarySubtitleStyle);
            WorkspaceTimeline.Refresh();
            SaveProjects();
            Dispatcher.UIThread.Post(async () => await ApplyWorkspaceSubtitleStyleAsync(), DispatcherPriority.Background);
        }
        finally
        {
            _isEditingSubtitleStyle = false;
        }
    }

    private string GetWorkspaceStyleSampleText(int track, double positionSeconds)
    {
        var currentMs = (long)(Math.Max(0, positionSeconds) * 1000);
        var activeCue = _workspaceCues.FirstOrDefault(c => c.TrackIndex == track && c.StartMilliseconds <= currentMs && c.EndMilliseconds >= currentMs)
                        ?? _workspaceCues.FirstOrDefault(c => c.TrackIndex == track)
                        ?? _workspaceCues.FirstOrDefault();
        if (activeCue == null) return "AstraCat@字幕样式预览";
        var sample = activeCue.DisplayText;
        return string.IsNullOrWhiteSpace(sample) ? "AstraCat@字幕样式预览" : sample;
    }

    private void UpdateWorkspaceStyleGroupRows(CaptionProject project)
    {
        EnsureProjectStyleLibrary(project);
        if (string.IsNullOrWhiteSpace(_workspaceSelectedStyleId) || project.SubtitleStyles.All(style => style.Id != _workspaceSelectedStyleId))
            _workspaceSelectedStyleId = GetWorkspaceStyleForTrack(project, _workspaceSelectedStyleTrack).Id;
        RebuildWorkspaceStyleCards(project);
    }

    private async Task ApplyWorkspaceSubtitleStyleAsync()
    {
        if (!_workspacePlayer.IsRunning || _activeProjectId is null) return;
        if (IsAudioOnlyMedia(_workspaceMediaPath))
        {
            _workspaceAudioPreviewSignature = null;
            UpdateWorkspaceAudioSubtitlePreview(_workspacePlayer.PositionSeconds, force: true);
            return;
        }
        var generation = Interlocked.Increment(ref _workspaceSubtitleReloadGeneration);
        await _workspaceSubtitleReloadGate.WaitAsync();
        var project = _projects.FirstOrDefault(item => item.Id == _activeProjectId);
        try
        {
            // Collapse resize/style/edit bursts to the newest request. This
            // prevents mpv reading the ASS file while another request rewrites it.
            if (generation != Volatile.Read(ref _workspaceSubtitleReloadGeneration) || project is null) return;
            var assPath = CreateWorkspaceAss(project);
            if (File.Exists(assPath))
            {
                await _workspacePlayer.ReloadSubtitleAsync(assPath);
            }
        }
        catch (Exception ex)
        {
            WorkspacePlaybackStateText.Text = $"样式预览失败：{ex.Message}";
        }
        finally
        {
            _workspaceSubtitleReloadGate.Release();
        }
    }

    private string WorkspaceCueStatePath(string projectId) => Path.Combine(ProjectDirectory(projectId), "workspace-state.json");

    private void LoadWorkspaceCueState(string projectId)
    {
        var path = WorkspaceCueStatePath(projectId);
        if (!File.Exists(path)) return;
        try
        {
            var states = System.Text.Json.JsonSerializer.Deserialize<List<WorkspaceCueState>>(File.ReadAllText(path)) ?? [];
            foreach (var state in states)
            {
                var cue = _workspaceCues.FirstOrDefault(item => item.Index == state.Index);
                if (cue is null) continue;
                // Pre-dual-track workspace state had no bilingual group id.
                // Do not let that legacy record collapse freshly split
                // Chinese/English cues back onto the same track.
                if (!string.IsNullOrWhiteSpace(cue.GroupId) &&
                    !string.Equals(cue.GroupId, state.GroupId, StringComparison.Ordinal)) continue;
                cue.TrackIndex = state.TrackIndex;
                cue.GroupId = state.GroupId;
                cue.GroupName = state.GroupName;
            }
        }
        catch
        {
        }
    }

    private void SaveWorkspaceCueState(string projectId)
    {
        EnsureProjectDirectory(projectId);
        var state = _workspaceCues.Select(cue => new WorkspaceCueState
        {
            Index = cue.Index,
            TrackIndex = cue.TrackIndex,
            GroupId = cue.GroupId,
            GroupName = cue.GroupName
        }).ToArray();
        File.WriteAllText(WorkspaceCueStatePath(projectId),
            System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
