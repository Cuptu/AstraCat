using System.Runtime.InteropServices;

namespace AstraCat;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    VideoReconfig = 17,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    AdvancedControl = 10,
    BlockForTargetTime = 12
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;
    public IntPtr Context;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr MpvOpenGlGetProcAddress(IntPtr context, IntPtr name);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MpvRenderUpdateCallback(IntPtr context);

internal sealed class MpvNative
{
    private static readonly object SharedSync = new();
    private static MpvNative? _shared;

    public static MpvNative GetShared(string libraryPath)
    {
        lock (SharedSync)
            return _shared ??= new MpvNative(libraryPath);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetOptionStringDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ObservePropertyDelegate(IntPtr handle, ulong replyUserData, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPropertyDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestLogMessagesDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr WaitEventDelegate(IntPtr handle, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandDelegate(IntPtr handle, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandAsyncDelegate(IntPtr handle, ulong replyUserData, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WakeupDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TerminateDestroyDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorStringDelegate(int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ClientApiVersionDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RenderContextCreateDelegate(out IntPtr context, IntPtr handle, IntPtr parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderContextSetUpdateCallbackDelegate(IntPtr context, MpvRenderUpdateCallback? callback, IntPtr callbackContext);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong RenderContextUpdateDelegate(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RenderContextRenderDelegate(IntPtr context, IntPtr parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderContextReportSwapDelegate(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderContextFreeDelegate(IntPtr context);

    private readonly IntPtr _library;
    private readonly CreateDelegate _create;
    private readonly InitializeDelegate _initialize;
    private readonly SetOptionStringDelegate _setOptionString;
    private readonly ObservePropertyDelegate _observeProperty;
    private readonly GetPropertyDelegate _getProperty;
    private readonly RequestLogMessagesDelegate _requestLogMessages;
    private readonly WaitEventDelegate _waitEvent;
    private readonly CommandDelegate _command;
    private readonly CommandAsyncDelegate _commandAsync;
    private readonly WakeupDelegate _wakeup;
    private readonly TerminateDestroyDelegate _terminateDestroy;
    private readonly ErrorStringDelegate _errorString;
    private readonly ClientApiVersionDelegate _clientApiVersion;
    private readonly RenderContextCreateDelegate _renderContextCreate;
    private readonly RenderContextSetUpdateCallbackDelegate _renderContextSetUpdateCallback;
    private readonly RenderContextUpdateDelegate _renderContextUpdate;
    private readonly RenderContextRenderDelegate _renderContextRender;
    private readonly RenderContextReportSwapDelegate _renderContextReportSwap;
    private readonly RenderContextFreeDelegate _renderContextFree;

    private MpvNative(string libraryPath)
    {
        _library = NativeLibrary.Load(libraryPath);
        _create = Bind<CreateDelegate>("mpv_create");
        _initialize = Bind<InitializeDelegate>("mpv_initialize");
        _setOptionString = Bind<SetOptionStringDelegate>("mpv_set_option_string");
        _observeProperty = Bind<ObservePropertyDelegate>("mpv_observe_property");
        _getProperty = Bind<GetPropertyDelegate>("mpv_get_property");
        _requestLogMessages = Bind<RequestLogMessagesDelegate>("mpv_request_log_messages");
        _waitEvent = Bind<WaitEventDelegate>("mpv_wait_event");
        _command = Bind<CommandDelegate>("mpv_command");
        _commandAsync = Bind<CommandAsyncDelegate>("mpv_command_async");
        _wakeup = Bind<WakeupDelegate>("mpv_wakeup");
        _terminateDestroy = Bind<TerminateDestroyDelegate>("mpv_terminate_destroy");
        _errorString = Bind<ErrorStringDelegate>("mpv_error_string");
        _clientApiVersion = Bind<ClientApiVersionDelegate>("mpv_client_api_version");
        _renderContextCreate = Bind<RenderContextCreateDelegate>("mpv_render_context_create");
        _renderContextSetUpdateCallback = Bind<RenderContextSetUpdateCallbackDelegate>("mpv_render_context_set_update_callback");
        _renderContextUpdate = Bind<RenderContextUpdateDelegate>("mpv_render_context_update");
        _renderContextRender = Bind<RenderContextRenderDelegate>("mpv_render_context_render");
        _renderContextReportSwap = Bind<RenderContextReportSwapDelegate>("mpv_render_context_report_swap");
        _renderContextFree = Bind<RenderContextFreeDelegate>("mpv_render_context_free");
    }

    public ulong ClientApiVersion => _clientApiVersion();
    public IntPtr Create() => _create();
    public int Initialize(IntPtr handle) => _initialize(handle);
    public int SetOptionString(IntPtr handle, string name, string value) => _setOptionString(handle, name, value);
    public int ObserveProperty(IntPtr handle, ulong id, string name, MpvFormat format) => _observeProperty(handle, id, name, format);
    public double GetPropertyDouble(IntPtr handle, string name)
    {
        var data = Marshal.AllocHGlobal(sizeof(double));
        try
        {
            var code = _getProperty(handle, name, MpvFormat.Double, data);
            if (code < 0) throw new InvalidOperationException($"{name}: {Error(code)}");
            return Marshal.PtrToStructure<double>(data);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public bool TryGetPropertyInt64(IntPtr handle, string name, out long value)
    {
        var data = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            var code = _getProperty(handle, name, MpvFormat.Int64, data);
            value = code < 0 ? 0 : Marshal.ReadInt64(data);
            return code >= 0;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public int RequestLogMessages(IntPtr handle, string level) => _requestLogMessages(handle, level);
    public IntPtr WaitEvent(IntPtr handle, double timeout) => _waitEvent(handle, timeout);
    public void Wakeup(IntPtr handle) => _wakeup(handle);
    public void TerminateDestroy(IntPtr handle) => _terminateDestroy(handle);
    public int RenderContextCreate(out IntPtr context, IntPtr handle, IntPtr parameters) => _renderContextCreate(out context, handle, parameters);
    public void RenderContextSetUpdateCallback(IntPtr context, MpvRenderUpdateCallback? callback, IntPtr callbackContext) => _renderContextSetUpdateCallback(context, callback, callbackContext);
    public ulong RenderContextUpdate(IntPtr context) => _renderContextUpdate(context);
    public int RenderContextRender(IntPtr context, IntPtr parameters) => _renderContextRender(context, parameters);
    public void RenderContextReportSwap(IntPtr context) => _renderContextReportSwap(context);
    public void RenderContextFree(IntPtr context) => _renderContextFree(context);

    public string Error(int code) => Marshal.PtrToStringUTF8(_errorString(code)) ?? $"mpv error {code}";

    public int Command(IntPtr handle, params string[] arguments) => InvokeCommand(handle, arguments, false);
    public int CommandAsync(IntPtr handle, params string[] arguments) => InvokeCommand(handle, arguments, true);

    private int InvokeCommand(IntPtr handle, IReadOnlyList<string> arguments, bool async)
    {
        var strings = new IntPtr[arguments.Count];
        var argv = IntPtr.Zero;
        try
        {
            argv = Marshal.AllocHGlobal((arguments.Count + 1) * IntPtr.Size);
            for (var i = 0; i < arguments.Count; i++)
            {
                strings[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
            }
            Marshal.WriteIntPtr(argv, arguments.Count * IntPtr.Size, IntPtr.Zero);
            return async ? _commandAsync(handle, 0, argv) : _command(handle, argv);
        }
        finally
        {
            foreach (var value in strings)
                if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value);
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
        }
    }

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
}
