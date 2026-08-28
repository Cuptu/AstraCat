using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace AstraCat;

/// <summary>
/// Schedules additive animation tracks on Avalonia's frame callback. Tracks that
/// target the same property compose as deltas, named groups can be interrupted
/// without snapping, and stage barriers sequence related transitions.
/// </summary>
public sealed class AdditiveAnimationScheduler
{
    private sealed class Group(List<AnimationTrack> tracks)
    {
        public readonly List<AnimationTrack> Tracks = tracks;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, Group> _groups = new();
    private TopLevel? _topLevel;
    private TimeSpan? _lastFrameTime;
    private bool _frameRequested;

    public void Attach(TopLevel topLevel)
    {
        _topLevel = topLevel;
        _lastFrameTime = null;
        if (_groups.Count > 0) RequestNextFrame();
    }

    public Task Start(IEnumerable<AnimationTrack> tracks, string name)
    {
        Stop(name);
        var wasEmpty = _groups.Count == 0;
        var group = new Group(tracks.Select(track => track.Clone()).ToList());
        if (group.Tracks.Count == 0)
        {
            group.Completion.TrySetResult();
            return group.Completion.Task;
        }

        _groups[name] = group;
        if (wasEmpty)
        {
            _lastFrameTime = null;
            RequestNextFrame();
        }
        return group.Completion.Task;
    }

    public void Stop(string name)
    {
        if (!_groups.Remove(name, out var group)) return;
        group.Completion.TrySetCanceled();
    }

    private void RequestNextFrame()
    {
        if (_frameRequested || _topLevel is null || _groups.Count == 0) return;
        _frameRequested = true;
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan frameTime)
    {
        _frameRequested = false;
        var delta = _lastFrameTime.HasValue
            ? Math.Clamp((frameTime - _lastFrameTime.Value).TotalMilliseconds, 0.1, 100)
            : 16.667;
        _lastFrameTime = frameTime;

        if (delta >= 0.1)
        {
            try
            {
                Tick(delta);
            }
            catch (Exception exception)
            {
                foreach (var group in _groups.Values)
                    group.Completion.TrySetException(exception);
                _groups.Clear();
            }
        }

        if (_groups.Count > 0) RequestNextFrame();
        else _lastFrameTime = null;
    }

    private void Tick(double delta)
    {
        foreach (var pair in _groups.ToArray())
        {
            if (!_groups.TryGetValue(pair.Key, out var group) || !ReferenceEquals(group, pair.Value))
                continue;

            Advance(group, delta);
            if (group.Tracks.Count != 0) continue;
            if (_groups.Remove(pair.Key, out var removed) && ReferenceEquals(removed, group))
                group.Completion.TrySetResult();
        }
    }

    private static void Advance(Group group, double delta)
    {
        var canReleaseAfter = true;
        var index = 0;
        while (index < group.Tracks.Count)
        {
            var track = group.Tracks[index];
            if (track.After)
            {
                if (!canReleaseAfter) break;
                canReleaseAfter = false;
                track.After = false;
                continue;
            }

            canReleaseAfter = false;
            track.Elapsed += delta;
            if (track.Elapsed > 0)
            {
                var percent = track.Duration <= 0 ? 1.0 : Math.Clamp(track.Elapsed / track.Duration, 0.0, 1.0);
                var easingDelta = track.Easing.Ease(percent) - track.Easing.Ease(track.PreviousPercent);
                var valueDelta = Math.Round(track.Value * easingDelta, 7);
                track.ApplyDelta(valueDelta);
                track.PreviousPercent = percent;
            }

            if (track.Elapsed >= track.Duration)
                group.Tracks.RemoveAt(index);
            else
                index++;
        }
    }
}

public sealed class AnimationTrack
{
    public required Action<double> ApplyDelta { get; init; }
    public required double Value { get; init; }
    public required double Duration { get; init; }
    public double Delay { get; init; }
    public bool After { get; set; }
    public Easing Easing { get; init; } = new ClampedLinearEasing();

    internal double Elapsed { get; set; }
    internal double PreviousPercent { get; set; }

    internal AnimationTrack Clone() => new()
    {
        ApplyDelta = ApplyDelta,
        Value = Value,
        Duration = Duration,
        Delay = Delay,
        After = After,
        Easing = Easing,
        Elapsed = -Delay,
        PreviousPercent = 0
    };
}
