using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace AstraCat;

public sealed class MpvPlayerService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _nativeSync = new();
    private MpvNative? _native;
    private IntPtr _handle;
    private IntPtr _renderContext;
    private IntPtr _renderFbo;
    private IntPtr _renderFlipY;
    private IntPtr _renderBlockForTargetTime;
    private IntPtr _renderAdvancedControl;
    private IntPtr _renderParameters;
    private CancellationTokenSource _lifetime = new();
    private Task? _eventLoopTask;
    private MpvVideoHost? _host;
    private GlInterface? _activeGl;
    private readonly MpvOpenGlGetProcAddress _getProcAddress;
    private readonly MpvRenderUpdateCallback _renderUpdate;
    private bool _stopping;
    private long _renderCount;
    private long _renderTicks;
    private long _renderUpdateCallbackCount;
    private long _positionNotificationCount;
    private bool _renderContextWasCreated;
    private bool _renderContextLost;
    private string? _currentSubtitlePath;
    private int _positionTrailingScheduled;

    public MpvPlayerService()
    {
        _getProcAddress = GetOpenGlProcAddress;
        _renderUpdate = OnRenderUpdate;
    }

    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public bool IsPaused { get; private set; } = true;
    public bool HasVideo { get; private set; }
    public double VideoAspect { get; private set; } = 16d / 9;
    public double VideoFrameRate { get; private set; } = 25d;
    internal string HardwareDecoder { get; private set; } = "no";
    internal long DecoderDroppedFrames { get; private set; }
    internal long VoDroppedFrames { get; private set; }
    internal double AvSyncSeconds { get; private set; }
    public bool IsRunning => _handle != IntPtr.Zero && !_stopping;
    internal bool HasRenderContext => _renderContext != IntPtr.Zero;
    internal long RenderCount => Interlocked.Read(ref _renderCount);
    internal long RenderTicks => Interlocked.Read(ref _renderTicks);
    internal long RenderUpdateCallbackCount => Interlocked.Read(ref _renderUpdateCallbackCount);
    internal long PositionNotificationCount => Interlocked.Read(ref _positionNotificationCount);

    public event EventHandler<double>? PositionChanged;
    public event EventHandler<double>? DurationChanged;
    public event EventHandler<bool>? PauseChanged;
    public event EventHandler<bool>? VideoAvailabilityChanged;
    public event EventHandler<double>? VideoAspectChanged;
    public event EventHandler<string>? PlaybackError;
    internal event EventHandler<string>? Diagnostic;

    public async Task StartAsync(MpvVideoHost host, string mediaPath, string? subtitlePath = null,
        CancellationToken cancellationToken = default, double? startPositionSeconds = null)
    {
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("媒体文件不存在。", mediaPath);
        var libraryPath = MediaToolLocator.FindLibMpv();
        if (libraryPath is null) throw new FileNotFoundException("未找到 libmpv-2.dll，无法启动内置播放器。");

        await StopAsync();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopping = false;
            Interlocked.Exchange(ref _renderCount, 0);
            Interlocked.Exchange(ref _renderTicks, 0);
            Interlocked.Exchange(ref _renderUpdateCallbackCount, 0);
            Interlocked.Exchange(ref _positionNotificationCount, 0);
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            _native ??= MpvNative.GetShared(libraryPath);
            var api = _native.ClientApiVersion;
            if ((api >> 16) != 2)
                throw new NotSupportedException($"libmpv Client API 不兼容：{api >> 16}.{api & 0xffff}");

            await Task.Run(InitializeCore, cancellationToken);
            _eventLoopTask = Task.Factory.StartNew(
                () => EventLoop(_lifetime.Token),
                _lifetime.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!IsAudioOnlyMedia(mediaPath))
            {
                _host = host;
                host.AttachPlayer(this);
                await WaitForRenderContextAsync(cancellationToken);
            }
            var initialSubtitlePath = IsAudioOnlyMedia(mediaPath) ? null : subtitlePath;
            await Task.Run(() => LoadMediaCore(mediaPath, initialSubtitlePath, startPositionSeconds), cancellationToken);
        }
        catch (Exception startError)
        {
            _lifetime.Cancel();
            if (_handle != IntPtr.Zero) _native?.Wakeup(_handle);
            if (_host is not null)
            {
                try { await _host.DetachPlayerAsync(this).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("播放器启动失败，且 Render Context 无法安全释放；已保留 mpv core 以避免原生崩溃。",
                        startError, cleanupError);
                }
                _host = null;
            }
            if (_eventLoopTask is not null)
            {
                await _eventLoopTask;
                _eventLoopTask = null;
            }
            if (_renderContext != IntPtr.Zero)
                throw new InvalidOperationException("Render Context 尚未释放，拒绝销毁 mpv core。", startError);
            DestroyCore();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsAudioOnlyMedia(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".m4a" or ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".opus" or ".wma";

    private void InitializeCore()
    {
        var native = _native ?? throw new InvalidOperationException("libmpv 尚未加载。");
        var handle = native.Create();
        if (handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败。");
        _handle = handle;

        SetOption("config", "no");
        SetOption("terminal", "no");
        SetOption("msg-level", "all=warn");
        SetOption("vo", "libmpv");
        // ANGLE's OpenGL context does not expose mpv's native D3D11 zero-copy
        // interop on current Windows builds. Prefer the safe copy fallback;
        // `auto` was measured to select the same d3d11va-copy path.
        SetOption("hwdec", "auto-safe");
        SetOption("keep-open", "yes");
        SetOption("idle", "yes");
        SetOption("pause", "yes");
        SetOption("input-default-bindings", "no");
        SetOption("osc", "no");
        SetOption("osd-bar", "no");
        SetOption("audio-display", "no");
        SetOption("sub-visibility", "yes");
        SetOption("sub-auto", "no");
        SetOption("sub-ass-override", "no");
        SetOption("sub-ass-scale-with-window", "yes");

        Check(native.Initialize(handle), "mpv_initialize");
        native.RequestLogMessages(handle, "warn");
        Check(native.ObserveProperty(handle, 1, "time-pos", MpvFormat.Double), "observe time-pos");
        Check(native.ObserveProperty(handle, 2, "duration", MpvFormat.Double), "observe duration");
        Check(native.ObserveProperty(handle, 3, "pause", MpvFormat.Flag), "observe pause");
        Check(native.ObserveProperty(handle, 4, "video-format", MpvFormat.String), "observe video-format");
        Check(native.ObserveProperty(handle, 5, "video-params/aspect", MpvFormat.Double), "observe aspect");
        Check(native.ObserveProperty(handle, 6, "hwdec-current", MpvFormat.String), "observe hwdec-current");
        Check(native.ObserveProperty(handle, 7, "decoder-frame-drop-count", MpvFormat.Int64), "observe decoder drops");
        Check(native.ObserveProperty(handle, 8, "vo-drop-frame-count", MpvFormat.Int64), "observe vo drops");
        Check(native.ObserveProperty(handle, 9, "avsync", MpvFormat.Double), "observe avsync");
        Check(native.ObserveProperty(handle, 10, "estimated-vf-fps", MpvFormat.Double), "observe video frame rate");

    }

    private void LoadMediaCore(string mediaPath, string? subtitlePath, double? startPositionSeconds)
    {
        var native = _native ?? throw new InvalidOperationException("libmpv 尚未加载。");
        var handle = _handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("mpv 尚未初始化。");
        if (startPositionSeconds is >= 0)
            Check(native.Command(handle, "set", "start", startPositionSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)), "set initial position");
        Check(native.Command(handle, "loadfile", mediaPath, "replace"), "loadfile");
        if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
        {
            Check(native.Command(handle, "sub-add", subtitlePath, "select"), "sub-add");
            _currentSubtitlePath = Path.GetFullPath(subtitlePath);
        }
    }

    private async Task WaitForRenderContextAsync(CancellationToken token)
    {
        var started = Environment.TickCount64;
        while (_renderContext == IntPtr.Zero)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 - started > 10_000)
                throw new TimeoutException("Avalonia OpenGL Render Context 创建超时。");
            await Task.Delay(20, token);
        }
    }

    public Task TogglePauseAsync(CancellationToken token = default) => CommandAsync(token, "cycle", "pause");
    public Task SetPauseAsync(bool pause, CancellationToken token = default) => CommandAsync(token, "set", "pause", pause ? "yes" : "no");
    public Task SeekAsync(double seconds, CancellationToken token = default) =>
        CommandAsync(token, "seek", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
    public Task SeekRelativeAsync(double seconds, CancellationToken token = default) =>
        CommandAsync(token, "seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "relative+exact");

    public async Task<string?> CaptureFrameAsync(CancellationToken token = default)
    {
        if (!IsRunning) return null;
        var tempJpg = Path.Combine(Path.GetTempPath(), $"astracat_shot_{Guid.NewGuid():N}.jpg");
        try
        {
            await CommandAsync(token, "screenshot-to-file", tempJpg, "video");
            for (var i = 0; i < 120; i++)
            {
                if (File.Exists(tempJpg) && new FileInfo(tempJpg).Length > 0) return tempJpg;
                await Task.Delay(10, token);
            }
        }
        catch { }
        try { if (File.Exists(tempJpg)) File.Delete(tempJpg); } catch { }
        return null;
    }

    public async Task LoadSubtitleAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) return;
        await CommandAsync(token, "sub-add", path, "select");
        await CommandAsync(token, "set", "sub-visibility", "yes");
        _currentSubtitlePath = Path.GetFullPath(path);
    }

    public async Task ReloadSubtitleAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) return;
        await CommandAsync(token, "set", "sub-ass-override", "no");
        await CommandAsync(token, "set", "sub-ass-scale-with-window", "yes");
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(_currentSubtitlePath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            var subtitleId = CurrentSubtitleId();
            if (subtitleId is > 0)
            {
                try
                {
                    await CommandAsync(token, "sub-reload", subtitleId.Value.ToString(CultureInfo.InvariantCulture));
                }
                catch (InvalidOperationException)
                {
                    // The track can disappear briefly while a media/subtitle
                    // load is settling. Add the fresh file first, then remove
                    // the stale id so the preview never loses all subtitles.
                    await CommandAsync(token, "sub-add", fullPath, "select");
                    await TryCommandAsync(token, "sub-remove", subtitleId.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                await CommandAsync(token, "sub-add", fullPath, "select");
            }
        }
        else
        {
            var previousSubtitleId = CurrentSubtitleId();
            await CommandAsync(token, "sub-add", fullPath, "select");
            if (previousSubtitleId is > 0)
                await TryCommandAsync(token, "sub-remove", previousSubtitleId.Value.ToString(CultureInfo.InvariantCulture));
            _currentSubtitlePath = fullPath;
        }
        await CommandAsync(token, "set", "sub-visibility", "yes");
    }

    public async Task ReloadCurrentSubtitleAsync(CancellationToken token = default)
    {
        if (_currentSubtitlePath is not null)
            await ReloadSubtitleAsync(_currentSubtitlePath, token);
    }

    private long? CurrentSubtitleId()
    {
        var native = _native;
        var handle = _handle;
        return native is not null && handle != IntPtr.Zero && native.TryGetPropertyInt64(handle, "sid", out var id)
            ? id
            : null;
    }

    private async Task TryCommandAsync(CancellationToken token, params string[] arguments)
    {
        try { await CommandAsync(token, arguments); }
        catch (InvalidOperationException) { }
    }

    public async Task ApplySubtitleStyleAsync(string fontFamily, double fontSize, string textColor,
        string outlineColor, double outlineWidth, CancellationToken token = default)
    {
        await CommandAsync(token, "set", "sub-font", fontFamily);
        await CommandAsync(token, "set", "sub-font-size", fontSize.ToString("0.##", CultureInfo.InvariantCulture));
        await CommandAsync(token, "set", "sub-color", MpvColor(textColor));
        await CommandAsync(token, "set", "sub-border-color", MpvColor(outlineColor));
        await CommandAsync(token, "set", "sub-border-size", outlineWidth.ToString("0.##", CultureInfo.InvariantCulture));
        await CommandAsync(token, "set", "sub-margin-y", "0");
        await CommandAsync(token, "set", "sub-margin-x", "0");
    }

    public async Task ApplySubtitleStyleAsync(SubtitleStyleDefinition style, CancellationToken token = default)
    {
        await ApplySubtitleStyleAsync(style.FontFamily, style.FontSize, style.TextColor, style.OutlineColor, style.OutlineWidth, token);
        await CommandAsync(token, "set", "sub-shadow-offset", style.ShadowDistance.ToString("0.##", CultureInfo.InvariantCulture));
        await CommandAsync(token, "set", "sub-bold", style.Bold ? "yes" : "no");
        await CommandAsync(token, "set", "sub-italic", style.Italic ? "yes" : "no");
    }

    internal void EnsureRenderContext(GlInterface gl, MpvVideoHost host)
    {
        if (_renderContextLost)
            throw new InvalidOperationException("OpenGL 上下文已经丢失，播放器需要重新启动。");
        if (_renderContext != IntPtr.Zero || _handle == IntPtr.Zero || _native is null) return;
        _activeGl = gl;
        var api = Marshal.StringToCoTaskMemUTF8("opengl");
        var init = IntPtr.Zero;
        var parameters = IntPtr.Zero;
        var createdContext = IntPtr.Zero;
        var renderFbo = IntPtr.Zero;
        var renderFlipY = IntPtr.Zero;
        var renderBlockForTargetTime = IntPtr.Zero;
        var renderAdvancedControl = IntPtr.Zero;
        var renderParameters = IntPtr.Zero;
        try
        {
            init = AllocStruct(new MpvOpenGlInitParams
            {
                GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProcAddress),
                Context = IntPtr.Zero
            });
            renderAdvancedControl = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(renderAdvancedControl, 1);
            parameters = AllocRenderParams(
                new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = api },
                new MpvRenderParam { Type = MpvRenderParamType.OpenGlInitParams, Data = init },
                new MpvRenderParam { Type = MpvRenderParamType.AdvancedControl, Data = renderAdvancedControl });
            Check(_native.RenderContextCreate(out createdContext, _handle, parameters), "mpv_render_context_create");
            renderFbo = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
            renderFlipY = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(renderFlipY, 1);
            renderBlockForTargetTime = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(renderBlockForTargetTime, 0);
            renderParameters = AllocRenderParams(
                new MpvRenderParam { Type = MpvRenderParamType.OpenGlFbo, Data = renderFbo },
                new MpvRenderParam { Type = MpvRenderParamType.FlipY, Data = renderFlipY },
                new MpvRenderParam { Type = MpvRenderParamType.BlockForTargetTime, Data = renderBlockForTargetTime });
            // From this point onward every update callback is coalesced onto the
            // OpenGL thread and consumed by mpv_render_context_update().
            _native.RenderContextSetUpdateCallback(createdContext, _renderUpdate, IntPtr.Zero);
            _renderFbo = renderFbo;
            _renderFlipY = renderFlipY;
            _renderBlockForTargetTime = renderBlockForTargetTime;
            _renderAdvancedControl = renderAdvancedControl;
            _renderParameters = renderParameters;
            _renderContext = createdContext;
            renderFbo = renderFlipY = renderBlockForTargetTime = renderAdvancedControl = renderParameters = createdContext = IntPtr.Zero;
            _host = host;
            if (_renderContextWasCreated)
                _ = ReloadVideoAfterContextRestoreAsync();
            _renderContextWasCreated = true;
        }
        catch
        {
            if (createdContext != IntPtr.Zero)
            {
                _native.RenderContextSetUpdateCallback(createdContext, null, IntPtr.Zero);
                _native.RenderContextFree(createdContext);
            }
            if (renderParameters != IntPtr.Zero) Marshal.FreeHGlobal(renderParameters);
            if (renderFbo != IntPtr.Zero) Marshal.FreeHGlobal(renderFbo);
            if (renderFlipY != IntPtr.Zero) Marshal.FreeHGlobal(renderFlipY);
            if (renderBlockForTargetTime != IntPtr.Zero) Marshal.FreeHGlobal(renderBlockForTargetTime);
            if (renderAdvancedControl != IntPtr.Zero) Marshal.FreeHGlobal(renderAdvancedControl);
            throw;
        }
        finally
        {
            _activeGl = null;
            if (parameters != IntPtr.Zero) Marshal.FreeHGlobal(parameters);
            if (init != IntPtr.Zero) Marshal.FreeHGlobal(init);
            Marshal.FreeCoTaskMem(api);
        }
    }

    private async Task ReloadVideoAfterContextRestoreAsync()
    {
        try
        {
            await CommandAsync(_lifetime.Token, "video-reload");
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (_stopping || _handle == IntPtr.Zero)
        {
        }
    }

    internal void Render(GlInterface gl, int framebuffer, int width, int height)
    {
        if (_renderContext == IntPtr.Zero || _native is null) return;
        var renderStarted = Stopwatch.GetTimestamp();
        _activeGl = gl;
        try
        {
            _native.RenderContextUpdate(_renderContext);
            Marshal.StructureToPtr(
                new MpvOpenGlFbo { Fbo = framebuffer, Width = width, Height = height, InternalFormat = 0 },
                _renderFbo, false);
            Check(_native.RenderContextRender(_renderContext, _renderParameters), "mpv_render_context_render");
            // Avalonia presents the shared surface later; reporting a swap here
            // would provide mpv a timestamp that is earlier than the real flip.
        }
        catch (Exception ex)
        {
            SafeRaise(PlaybackError, ex.Message);
        }
        finally
        {
            _activeGl = null;
            Interlocked.Increment(ref _renderCount);
            Interlocked.Add(ref _renderTicks, Stopwatch.GetTimestamp() - renderStarted);
        }
    }

    internal void ReleaseRenderContext()
    {
        if (_renderContext == IntPtr.Zero || _native is null) return;
        _native.RenderContextSetUpdateCallback(_renderContext, null, IntPtr.Zero);
        _native.RenderContextFree(_renderContext);
        _renderContext = IntPtr.Zero;
        FreeRenderBuffers();
    }

    internal void NotifyOpenGlLost()
    {
        _renderContextLost = true;
        SafeRaise(PlaybackError, "OpenGL 上下文已丢失；为避免复用无效 GPU 资源，请保存项目并重新启动 AstraCat。");
    }
    internal void NotifyRenderFailure(Exception error) => SafeRaise(PlaybackError, $"Render API 初始化失败：{error.Message}");

    private IntPtr GetOpenGlProcAddress(IntPtr context, IntPtr name)
    {
        try
        {
            var symbol = Marshal.PtrToStringUTF8(name);
            return string.IsNullOrWhiteSpace(symbol) || _activeGl is null ? IntPtr.Zero : _activeGl.GetProcAddress(symbol);
        }
        catch { return IntPtr.Zero; }
    }

    private void OnRenderUpdate(IntPtr context)
    {
        try
        {
            Interlocked.Increment(ref _renderUpdateCallbackCount);
            _host?.RequestRender();
        }
        catch
        {
            // Exceptions must never cross the native callback boundary.
        }
    }

    private void EventLoop(CancellationToken token)
    {
        Diagnostic?.Invoke(this, "event loop started");
        while (!token.IsCancellationRequested && _handle != IntPtr.Zero && _native is not null)
        {
            IntPtr eventPointer;
            try { eventPointer = _native.WaitEvent(_handle, -1); }
            catch (Exception ex)
            {
                SafeRaise(PlaybackError, $"mpv 事件循环失败：{ex.Message}");
                break;
            }
            if (eventPointer == IntPtr.Zero) continue;
            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            if (mpvEvent.EventId == MpvEventId.None) continue;
            if (mpvEvent.EventId == MpvEventId.Shutdown) break;
            if (mpvEvent.EventId == MpvEventId.PropertyChange) HandleProperty(mpvEvent.Data);
            else if (mpvEvent.EventId == MpvEventId.LogMessage) HandleLog(mpvEvent.Data);
            else if (mpvEvent.EventId == MpvEventId.QueueOverflow)
                SafeRaise(PlaybackError, "mpv 事件队列溢出，部分播放状态可能未及时更新。");
        }
        Diagnostic?.Invoke(this, "event loop stopped");
    }

    private void HandleProperty(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        var property = Marshal.PtrToStructure<MpvEventProperty>(data);
        var name = Marshal.PtrToStringUTF8(property.Name);
        if (name == "time-pos" && TryDouble(property, out var position))
        {
            PositionSeconds = position;
            ScheduleTrailingPositionNotification();
        }
        else if (name == "duration" && TryDouble(property, out var duration))
        {
            DurationSeconds = duration;
            SafeRaise(DurationChanged, duration);
        }
        else if (name == "pause" && property.Format == MpvFormat.Flag && property.Data != IntPtr.Zero)
        {
            IsPaused = Marshal.ReadInt32(property.Data) != 0;
            SafeRaise(PauseChanged, IsPaused);
        }
        else if (name == "video-format")
        {
            var value = property.Format == MpvFormat.String && property.Data != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data))
                : null;
            HasVideo = !string.IsNullOrWhiteSpace(value);
            SafeRaise(VideoAvailabilityChanged, HasVideo);
        }
        else if (name == "video-params/aspect" && TryDouble(property, out var aspect) && aspect > 0.05)
        {
            if (Math.Abs(aspect - VideoAspect) > 0.01)
            {
                VideoAspect = aspect;
                SafeRaise(VideoAspectChanged, aspect);
            }
        }
        else if (name == "hwdec-current")
        {
            HardwareDecoder = ReadString(property) ?? "no";
        }
        else if (name == "decoder-frame-drop-count" && TryInt64(property, out var decoderDrops))
        {
            DecoderDroppedFrames = decoderDrops;
        }
        else if (name == "vo-drop-frame-count" && TryInt64(property, out var voDrops))
        {
            VoDroppedFrames = voDrops;
        }
        else if (name == "avsync" && TryDouble(property, out var avsync))
        {
            AvSyncSeconds = avsync;
        }
        else if (name == "estimated-vf-fps" && TryDouble(property, out var frameRate) &&
                 double.IsFinite(frameRate) && frameRate is >= 1 and <= 240)
        {
            VideoFrameRate = frameRate;
        }
    }

    private void HandleLog(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        var message = Marshal.PtrToStructure<MpvEventLogMessage>(data);
        if (message.LogLevel > 20) return;
        var prefix = Marshal.PtrToStringUTF8(message.Prefix) ?? "mpv";
        var text = (Marshal.PtrToStringUTF8(message.Text) ?? string.Empty).Trim();
        if (text.Length > 0) SafeRaise(PlaybackError, $"{prefix}: {text}");
    }

    private static bool TryDouble(MpvEventProperty property, out double value)
    {
        value = 0;
        if (property.Format != MpvFormat.Double || property.Data == IntPtr.Zero) return false;
        value = Marshal.PtrToStructure<double>(property.Data);
        return double.IsFinite(value);
    }

    private static bool TryInt64(MpvEventProperty property, out long value)
    {
        value = 0;
        if (property.Format != MpvFormat.Int64 || property.Data == IntPtr.Zero) return false;
        value = Marshal.ReadInt64(property.Data);
        return true;
    }

    private static string? ReadString(MpvEventProperty property) =>
        property.Format == MpvFormat.String && property.Data != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data))
            : null;

    private async Task CommandAsync(CancellationToken token, params string[] arguments)
    {
        await _commandGate.WaitAsync(token);
        try
        {
            var native = _native;
            var handle = _handle;
            if (native is null || handle == IntPtr.Zero || _stopping) return;
            var result = await Task.Run(() => native.Command(handle, arguments), token);
            if (result < 0)
            {
                var message = $"mpv {arguments[0]}: {native.Error(result)}";
                SafeRaise(PlaybackError, message);
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void SetOption(string name, string value) => Check(_native!.SetOptionString(_handle, name, value), $"option {name}");

    private void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation}: {_native?.Error(result) ?? result.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string MpvColor(string color)
    {
        var value = color.Trim();
        return value.Length == 7 && value[0] == '#' ? $"#FF{value[1..]}" : value;
    }

    private static IntPtr AllocStruct<T>(T value) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, pointer, false);
        return pointer;
    }

    private static IntPtr AllocRenderParams(params MpvRenderParam[] values)
    {
        var size = Marshal.SizeOf<MpvRenderParam>();
        var pointer = Marshal.AllocHGlobal((values.Length + 1) * size);
        for (var i = 0; i < values.Length; i++)
            Marshal.StructureToPtr(values[i], IntPtr.Add(pointer, i * size), false);
        Marshal.StructureToPtr(new MpvRenderParam { Type = MpvRenderParamType.Invalid }, IntPtr.Add(pointer, values.Length * size), false);
        return pointer;
    }

    private void FreeRenderBuffers()
    {
        if (_renderParameters != IntPtr.Zero) Marshal.FreeHGlobal(_renderParameters);
        if (_renderFbo != IntPtr.Zero) Marshal.FreeHGlobal(_renderFbo);
        if (_renderFlipY != IntPtr.Zero) Marshal.FreeHGlobal(_renderFlipY);
        if (_renderBlockForTargetTime != IntPtr.Zero) Marshal.FreeHGlobal(_renderBlockForTargetTime);
        if (_renderAdvancedControl != IntPtr.Zero) Marshal.FreeHGlobal(_renderAdvancedControl);
        _renderParameters = IntPtr.Zero;
        _renderFbo = IntPtr.Zero;
        _renderFlipY = IntPtr.Zero;
        _renderBlockForTargetTime = IntPtr.Zero;
        _renderAdvancedControl = IntPtr.Zero;
    }

    private void ScheduleTrailingPositionNotification()
    {
        if (Interlocked.Exchange(ref _positionTrailingScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(34, _lifetime.Token);
                Interlocked.Increment(ref _positionNotificationCount);
                SafeRaise(PositionChanged, PositionSeconds);
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _positionTrailingScheduled, 0); }
        });
    }

    private void SafeRaise<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler.Invoke(this, value); } catch { }
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_handle == IntPtr.Zero) return;
            _stopping = true;
            await _commandGate.WaitAsync();
            _commandGate.Release();
            var host = _host;
            if (host is not null)
            {
                await host.DetachPlayerAsync(this).WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (_renderContext != IntPtr.Zero)
                throw new InvalidOperationException("Render Context 尚未在有效 OpenGL 线程释放；为避免未定义行为，已拒绝销毁 mpv core。");
            _host = null;

            _lifetime.Cancel();
            _native?.Wakeup(_handle);
            if (_eventLoopTask is not null)
            {
                await _eventLoopTask;
                _eventLoopTask = null;
            }
            DestroyCore();
            PositionSeconds = 0;
            DurationSeconds = 0;
            IsPaused = true;
            HasVideo = false;
            _currentSubtitlePath = null;
            _renderContextWasCreated = false;
            _renderContextLost = false;
        }
        finally
        {
            _stopping = false;
            _gate.Release();
        }
    }

    private void DestroyCore()
    {
        lock (_nativeSync)
        {
            if (_handle == IntPtr.Zero) return;
            _native?.TerminateDestroy(_handle);
            _handle = IntPtr.Zero;
            _renderContext = IntPtr.Zero;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
        _commandGate.Dispose();
        _gate.Dispose();
    }
}
