using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace AstraCat;

/// <summary>
/// Avalonia-owned OpenGL surface rendered directly by libmpv. Unlike the old
/// NativeControlHost implementation, this stays in Avalonia's visual tree and
/// therefore has no child HWND z-order or visibility races.
/// </summary>
public sealed class MpvVideoHost : OpenGlControlBase
{
    private readonly object _sync = new();
    private MpvPlayerService? _player;
    private TaskCompletionSource? _detachCompletion;
    private bool _detachRequested;
    private bool _openGlInitialized;
    private int _renderDispatchPending;
    private long _dispatcherPostCount;
    internal long DispatcherPostCount => Interlocked.Read(ref _dispatcherPostCount);
    internal event EventHandler<string>? Diagnostic;

    public MpvVideoHost()
    {
        AttachedToVisualTree += (_, _) => Diagnostic?.Invoke(this, $"attached {Bounds.Width:0}x{Bounds.Height:0}");
        DetachedFromVisualTree += (_, _) => Diagnostic?.Invoke(this, "detached");
    }

    internal void AttachPlayer(MpvPlayerService player)
    {
        lock (_sync)
        {
            if (_player is not null && !ReferenceEquals(_player, player))
                throw new InvalidOperationException("视频画面仍绑定到另一个播放器。");
            _player = player;
            _detachRequested = false;
        }
        RequestRender();
    }

    internal Task DetachPlayerAsync(MpvPlayerService player)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_player, player)) return Task.CompletedTask;
            if (!player.HasRenderContext)
            {
                _player = null;
                return Task.CompletedTask;
            }

            _detachRequested = true;
            _detachCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RequestRender();
            return _detachCompletion.Task;
        }
    }

    internal void RequestRender()
    {
        if (!_openGlInitialized)
        {
            Diagnostic?.Invoke(this, "render request ignored before OpenGL init");
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
            RequestNextFrameRendering();
        else if (Interlocked.Exchange(ref _renderDispatchPending, 1) == 0)
        {
            Interlocked.Increment(ref _dispatcherPostCount);
            Dispatcher.UIThread.Post(() =>
            {
                if (_openGlInitialized) RequestNextFrameRendering();
            }, DispatcherPriority.Render);
        }
    }

    public void HideImmediate() => IsVisible = false;
    public void ShowImmediate() => IsVisible = true;
    public void UpdateNativeVisibility(bool visible) => IsVisible = visible;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _openGlInitialized = true;
        Diagnostic?.Invoke(this, $"OpenGL initialized ({GlVersion})");
        MpvPlayerService? player;
        lock (_sync) player = _player;
        try { player?.EnsureRenderContext(gl, this); }
        catch (Exception ex) { player?.NotifyRenderFailure(ex); }
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        Interlocked.Exchange(ref _renderDispatchPending, 0);
        MpvPlayerService? player;
        TaskCompletionSource? completion = null;
        lock (_sync)
        {
            player = _player;
            if (_detachRequested)
            {
                _player = null;
                _detachRequested = false;
                completion = _detachCompletion;
                _detachCompletion = null;
            }
        }

        if (completion is not null)
        {
            try
            {
                player?.ReleaseRenderContext();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            return;
        }

        if (player is null) return;
        try { player.EnsureRenderContext(gl, this); }
        catch (Exception ex)
        {
            player.NotifyRenderFailure(ex);
            return;
        }
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));
        player.Render(gl, framebuffer, width, height);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _openGlInitialized = false;
        MpvPlayerService? player;
        TaskCompletionSource? completion = null;
        lock (_sync)
        {
            player = _player;
            if (_detachRequested)
            {
                _player = null;
                _detachRequested = false;
                completion = _detachCompletion;
                _detachCompletion = null;
            }
        }

        try
        {
            player?.ReleaseRenderContext();
            completion?.TrySetResult();
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
        }
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlLost()
    {
        _openGlInitialized = false;
        MpvPlayerService? player;
        lock (_sync) player = _player;
        player?.NotifyOpenGlLost();
        base.OnOpenGlLost();
    }
}
