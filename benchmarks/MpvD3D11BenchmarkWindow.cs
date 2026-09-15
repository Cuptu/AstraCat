using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AstraCat;

internal sealed class MpvD3D11BenchmarkWindow : Window
{
    private readonly string _mediaPath;
    private readonly string _resultPath;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MpvD3D11CompositionHost _host = new();

    public MpvD3D11BenchmarkWindow(string mediaPath, string resultPath,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        _mediaPath = mediaPath;
        _resultPath = resultPath;
        _desktop = desktop;
        Title = "AstraCat D3D11 Render API benchmark";
        Width = 960;
        Height = 600;
        Content = _host;
        Opened += RunAsync;
    }

    private async void RunAsync(object? sender, EventArgs e)
    {
        const int sampleSeconds = 20;
        var exitCode = 1;
        object result;
        await using var session = new MpvD3D11BenchmarkSession();
        try
        {
            await _host.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await session.StartAsync(_host, _mediaPath);
            await WaitUntilAsync(() => session.TryGetPosition(out var value) && value > 0.25,
                TimeSpan.FromSeconds(15));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var renderBefore = session.RenderCount;
            var renderTicksBefore = session.RenderTicks;
            var presentTicksBefore = session.PresentTicks;
            var positionStart = session.TryGetPosition(out var start) ? start : 0;
            var peakWorkingSet = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(sampleSeconds))
            {
                await Task.Delay(250);
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            }

            process.Refresh();
            var elapsed = stopwatch.Elapsed.TotalSeconds;
            var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
            var renders = session.RenderCount - renderBefore;
            var renderTicks = session.RenderTicks - renderTicksBefore;
            var presentTicks = session.PresentTicks - presentTicksBefore;
            var positionEnd = session.TryGetPosition(out var end) ? end : 0;
            result = new
            {
                mode = "libmpv-render-api-d3d11-avalonia-composition",
                media = Path.GetFileName(_mediaPath),
                sampleSeconds = elapsed,
                playbackSeconds = positionEnd - positionStart,
                cpuSeconds,
                cpuPercentOneCore = cpuSeconds / elapsed * 100,
                workingSetMiB = process.WorkingSet64 / 1048576d,
                peakWorkingSetMiB = peakWorkingSet / 1048576d,
                managedAllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1048576d,
                renderCount = renders,
                renderFps = renders / elapsed,
                averageMpvRenderMs = renders == 0 ? 0 : renderTicks * 1000d / Stopwatch.Frequency / renders,
                averageCompositionUpdateMs = renders == 0 ? 0 : presentTicks * 1000d / Stopwatch.Frequency / renders,
                updateCallbacks = session.UpdateCallbackCount,
                hardwareDecoder = session.HardwareDecoder,
                decoderDroppedFrames = session.DecoderDroppedFrames,
                voDroppedFrames = session.VoDroppedFrames,
                sharedTextureGeneration = session.SharedTextureGeneration
            };
            exitCode = positionEnd > positionStart ? 0 : 1;
        }
        catch (Exception ex)
        {
            result = new { mode = "libmpv-render-api-d3d11-avalonia-composition", error = ex.ToString() };
        }

        await session.StopAsync();
        await _host.DisposeAsync();
        var fullPath = Path.GetFullPath(_resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        _desktop.Shutdown(exitCode);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout) throw new TimeoutException("等待 D3D11 播放器状态超时。");
            await Task.Delay(50);
        }
    }
}

internal sealed class MpvD3D11CompositionHost : Control, IAsyncDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private ICompositionGpuInterop? _interop;
    private ICompositionImportedGpuImage? _image;
    private ulong _generation;
    private int _pixelWidth = 1;
    private int _pixelHeight = 1;

    public Task Ready => _ready.Task;
    public (int Width, int Height) PixelSize =>
        (Volatile.Read(ref _pixelWidth), Volatile.Read(ref _pixelHeight));

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            var elementVisual = ElementComposition.GetElementVisual(this)
                ?? throw new InvalidOperationException("Avalonia 没有为视频控件创建 Composition visual。");
            var compositor = elementVisual.Compositor;
            _interop = await compositor.TryGetCompositionGpuInterop()
                ?? throw new NotSupportedException("Avalonia 当前渲染后端不支持 GPU image interop。");
            if (!_interop.SupportedImageHandleTypes.Contains(
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle))
                throw new NotSupportedException("Avalonia 当前渲染后端不支持 D3D11 NT shared texture。");
            _surface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);
            UpdatePixelSize(Bounds.Size);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_visual is not null) _visual.Size = new Vector(finalSize.Width, finalSize.Height);
        UpdatePixelSize(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    private void UpdatePixelSize(Size size)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        Volatile.Write(ref _pixelWidth, Math.Max(1, (int)Math.Ceiling(size.Width * scale)));
        Volatile.Write(ref _pixelHeight, Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
    }

    public async Task PresentAsync(MpvD3D11RenderTarget target)
    {
        // Force the generic overload so the dispatcher only creates/imports and
        // queues the update. Awaiting the compositor task on the UI dispatcher
        // can deadlock the very commit that releases the keyed mutex.
        var operation = Dispatcher.UIThread.InvokeAsync<Task>(() => PresentOnUiThread(target));
        var updateTask = await operation;
        await updateTask.ConfigureAwait(false);
    }

    private Task PresentOnUiThread(MpvD3D11RenderTarget target)
    {
        if (_surface is null || _interop is null)
            throw new InvalidOperationException("Composition surface 尚未初始化。");
        if (_interop.IsLost)
            throw new InvalidOperationException("Avalonia Composition GPU device 已丢失。");

        if (_image is null || _generation != target.Generation)
        {
            (_image as IDisposable)?.Dispose();
            _image = _interop.ImportImage(
                new PlatformHandle(target.SharedHandle,
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = target.Width,
                    Height = target.Height,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                    TopLeftOrigin = true
                });
            _generation = target.Generation;
        }

        return _surface.UpdateWithKeyedMutexAsync(_image,
            checked((uint)target.ConsumerAcquireKey), checked((uint)target.ConsumerReleaseKey));
    }

    public async ValueTask DisposeAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            (_image as IDisposable)?.Dispose();
            _image = null;
            _surface?.Dispose();
            _surface = null;
            ElementComposition.SetElementChildVisual(this, null!);
            _visual = null;
        });
    }
}

internal sealed class MpvD3D11BenchmarkSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _renderSignal = new(0, 1);
    private readonly MpvRenderUpdateCallback _updateCallback;
    private readonly CancellationTokenSource _lifetime = new();
    private MpvNative? _native;
    private MpvD3D11CompositionHost? _host;
    private IntPtr _handle;
    private IntPtr _renderContext;
    private IntPtr _targetBuffer;
    private IntPtr _renderParams;
    private Task? _renderLoop;
    private long _renderCount;
    private long _renderTicks;
    private long _presentTicks;
    private long _updateCallbacks;
    private Exception? _renderError;

    public MpvD3D11BenchmarkSession() => _updateCallback = OnUpdate;
    public long RenderCount => Interlocked.Read(ref _renderCount);
    public long RenderTicks => Interlocked.Read(ref _renderTicks);
    public long PresentTicks => Interlocked.Read(ref _presentTicks);
    public long UpdateCallbackCount => Interlocked.Read(ref _updateCallbacks);
    public ulong SharedTextureGeneration { get; private set; }
    public string HardwareDecoder => _handle == IntPtr.Zero ? "no" : _native?.GetPropertyString(_handle, "hwdec-current") ?? "no";
    public long DecoderDroppedFrames => ReadInt64("decoder-frame-drop-count");
    public long VoDroppedFrames => ReadInt64("vo-drop-frame-count");

    public Task StartAsync(MpvD3D11CompositionHost host, string mediaPath)
    {
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("媒体文件不存在。", mediaPath);
        var library = MediaToolLocator.FindLibMpv() ?? throw new FileNotFoundException("libmpv not found");
        _native = MpvNative.GetShared(library);
        _host = host;
        _handle = _native.Create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");
        Set("config", "no");
        Set("terminal", "no");
        Set("msg-level", "all=warn");
        Set("vo", "libmpv");
        Set("hwdec", "d3d11va");
        Set("keep-open", "yes");
        Set("pause", "yes");
        Set("input-default-bindings", "no");
        Set("osd-bar", "no");
        Set("audio-display", "no");
        Check(_native.Initialize(_handle), "mpv_initialize");
        CreateRenderContext();
        _renderLoop = Task.Factory.StartNew(() => RenderLoopAsync(_lifetime.Token),
            _lifetime.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        Check(_native.Command(_handle, "loadfile", mediaPath, "replace"), "loadfile");
        Check(_native.Command(_handle, "set", "pause", "no"), "unpause");
        SignalRender();
        return Task.CompletedTask;
    }

    private void CreateRenderContext()
    {
        var api = Marshal.StringToCoTaskMemUTF8("d3d11");
        var advanced = Marshal.AllocHGlobal(sizeof(int));
        var createParams = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(advanced, 1);
            createParams = AllocRenderParams(
                new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = api },
                new MpvRenderParam { Type = MpvRenderParamType.AdvancedControl, Data = advanced });
            Check(_native!.RenderContextCreate(out _renderContext, _handle, createParams),
                "mpv_render_context_create(d3d11)");
            _targetBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<MpvD3D11RenderTarget>());
            _renderParams = AllocRenderParams(new MpvRenderParam
            {
                Type = MpvRenderParamType.D3D11RenderTarget,
                Data = _targetBuffer
            });
            _native.RenderContextSetUpdateCallback(_renderContext, _updateCallback, IntPtr.Zero);
        }
        finally
        {
            if (createParams != IntPtr.Zero) Marshal.FreeHGlobal(createParams);
            Marshal.FreeHGlobal(advanced);
            Marshal.FreeCoTaskMem(api);
        }
    }

    private async Task RenderLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _renderSignal.WaitAsync(token);
                if (_renderContext == IntPtr.Zero || _host is null || _native is null) continue;
                _native.RenderContextUpdate(_renderContext);
                var (width, height) = _host.PixelSize;
                Marshal.StructureToPtr(new MpvD3D11RenderTarget { Width = width, Height = height },
                    _targetBuffer, false);
                var renderStart = Stopwatch.GetTimestamp();
                Check(_native.RenderContextRender(_renderContext, _renderParams),
                    "mpv_render_context_render(d3d11)");
                Interlocked.Add(ref _renderTicks, Stopwatch.GetTimestamp() - renderStart);
                var target = Marshal.PtrToStructure<MpvD3D11RenderTarget>(_targetBuffer);
                SharedTextureGeneration = target.Generation;
                var presentStart = Stopwatch.GetTimestamp();
                await _host.PresentAsync(target);
                Interlocked.Add(ref _presentTicks, Stopwatch.GetTimestamp() - presentStart);
                Interlocked.Increment(ref _renderCount);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _renderError = ex;
        }
    }

    private void OnUpdate(IntPtr context)
    {
        Interlocked.Increment(ref _updateCallbacks);
        SignalRender();
    }

    private void SignalRender()
    {
        try { if (_renderSignal.CurrentCount == 0) _renderSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public bool TryGetPosition(out double position)
    {
        if (_renderError is not null) throw new InvalidOperationException("D3D11 render loop failed", _renderError);
        try
        {
            position = _handle == IntPtr.Zero ? 0 : _native!.GetPropertyDouble(_handle, "time-pos");
            return double.IsFinite(position);
        }
        catch { position = 0; return false; }
    }

    private long ReadInt64(string name) =>
        _handle != IntPtr.Zero && _native is not null && _native.TryGetPropertyInt64(_handle, name, out var value) ? value : 0;

    private void Set(string name, string value) => Check(_native!.SetOptionString(_handle, name, value), name);
    private void Check(int code, string operation)
    {
        if (code < 0) throw new InvalidOperationException($"{operation}: {_native!.Error(code)}");
    }

    private static IntPtr AllocRenderParams(params MpvRenderParam[] values)
    {
        var size = Marshal.SizeOf<MpvRenderParam>();
        var pointer = Marshal.AllocHGlobal((values.Length + 1) * size);
        for (var i = 0; i < values.Length; i++)
            Marshal.StructureToPtr(values[i], IntPtr.Add(pointer, i * size), false);
        Marshal.StructureToPtr(new MpvRenderParam { Type = MpvRenderParamType.Invalid },
            IntPtr.Add(pointer, values.Length * size), false);
        return pointer;
    }

    public async Task StopAsync()
    {
        if (_renderContext != IntPtr.Zero && _native is not null)
            _native.RenderContextSetUpdateCallback(_renderContext, null, IntPtr.Zero);
        _lifetime.Cancel();
        SignalRender();
        if (_renderLoop is not null)
        {
            try { await _renderLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _renderLoop = null;
        }
        if (_renderContext != IntPtr.Zero)
        {
            _native?.RenderContextFree(_renderContext);
            _renderContext = IntPtr.Zero;
        }
        if (_renderParams != IntPtr.Zero) Marshal.FreeHGlobal(_renderParams);
        if (_targetBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_targetBuffer);
        _renderParams = _targetBuffer = IntPtr.Zero;
        if (_handle != IntPtr.Zero)
        {
            _native?.TerminateDestroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _renderSignal.Dispose();
        _lifetime.Dispose();
    }
}
