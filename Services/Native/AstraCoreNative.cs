using System.Runtime.InteropServices;

namespace AstraCat;

internal sealed class AstraCoreNative
{
    private const uint MinSupportedAbi = 1;
    private const uint MaxSupportedAbi = 4;
    private static readonly Lazy<AstraCoreNative?> Shared = new(Create);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMediaInfo
    {
        public uint StructSize;
        public double DurationSeconds;
        public long BitRate;
        public int Width;
        public int Height;
        public double FrameRate;
        public int HasVideo;
        public int HasAudio;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CancelCallbackDelegate(IntPtr opaque);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProbeDelegate(IntPtr path, ref NativeMediaInfo result, IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProbeCancelDelegate(
        IntPtr path, ref NativeMediaInfo result,
        CancelCallbackDelegate? cancelCb, IntPtr cancelOpaque,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CheckEncoderDelegate(IntPtr encoderName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExtractWaveformPeaksDelegate(
        IntPtr path, int targetSampleRate, int samplesPerPeak, IntPtr outPeaks, int maxPeaks,
        out double outDuration, IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExtractWaveformPeaksCancelDelegate(
        IntPtr path, int targetSampleRate, int samplesPerPeak, IntPtr outPeaks, int maxPeaks,
        out double outDuration, CancelCallbackDelegate? cancelCb, IntPtr cancelOpaque,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExtractAudioWavDelegate(
        IntPtr inputPath, IntPtr outputWavPath, int targetSampleRate, int channels,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExtractAudioWavCancelDelegate(
        IntPtr inputPath, IntPtr outputWavPath, int targetSampleRate, int channels,
        CancelCallbackDelegate? cancelCb, IntPtr cancelOpaque,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ChangeAudioSpeedDelegate(
        IntPtr inputPath, IntPtr outputWavPath, double speedFactor,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ChangeAudioSpeedCancelDelegate(
        IntPtr inputPath, IntPtr outputWavPath, double speedFactor,
        CancelCallbackDelegate? cancelCb, IntPtr cancelOpaque,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TrimMediaDelegate(
        IntPtr inputPath, IntPtr outputPath, double startSeconds, double durationSeconds, int streamCopy,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TrimMediaCancelDelegate(
        IntPtr inputPath, IntPtr outputPath, double startSeconds, double durationSeconds, int streamCopy,
        CancelCallbackDelegate? cancelCb, IntPtr cancelOpaque,
        IntPtr error, nuint errorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExtractAudioStreamDelegate(
        IntPtr inputMedia, IntPtr outputAudio, int streamIndex,
        IntPtr error, nuint errorSize);

    private readonly ProbeDelegate? _probe;
    private readonly ProbeCancelDelegate? _probeCancel;
    private readonly CheckEncoderDelegate? _checkEncoder;
    private readonly ExtractWaveformPeaksDelegate? _extractWaveform;
    private readonly ExtractWaveformPeaksCancelDelegate? _extractWaveformCancel;
    private readonly ExtractAudioWavDelegate? _extractWav;
    private readonly ExtractAudioWavCancelDelegate? _extractWavCancel;
    private readonly ChangeAudioSpeedDelegate? _changeSpeed;
    private readonly ChangeAudioSpeedCancelDelegate? _changeSpeedCancel;
    private readonly TrimMediaDelegate? _trimMedia;
    private readonly TrimMediaCancelDelegate? _trimMediaCancel;
    private readonly ExtractAudioStreamDelegate? _extractAudioStream;

    private AstraCoreNative(IntPtr library)
    {
        var abi = Bind<AbiVersionDelegate>(library, "ac_abi_version")();
        if (abi < MinSupportedAbi || abi > MaxSupportedAbi)
            throw new NotSupportedException($"AstraCore Native ABI {abi} 不受支持。");

        _probeCancel = TryBind<ProbeCancelDelegate>(library, "ac_probe_cancel_utf8");
        if (_probeCancel is null) _probe = Bind<ProbeDelegate>(library, "ac_probe_utf8");

        _checkEncoder = TryBind<CheckEncoderDelegate>(library, "ac_check_encoder");

        _extractWaveformCancel = TryBind<ExtractWaveformPeaksCancelDelegate>(library, "ac_extract_waveform_peaks_cancel_utf8");
        if (_extractWaveformCancel is null) _extractWaveform = TryBind<ExtractWaveformPeaksDelegate>(library, "ac_extract_waveform_peaks_utf8");

        _extractWavCancel = TryBind<ExtractAudioWavCancelDelegate>(library, "ac_extract_audio_wav_cancel_utf8");
        if (_extractWavCancel is null) _extractWav = TryBind<ExtractAudioWavDelegate>(library, "ac_extract_audio_wav_utf8");

        _changeSpeedCancel = TryBind<ChangeAudioSpeedCancelDelegate>(library, "ac_change_audio_speed_cancel_utf8");
        if (_changeSpeedCancel is null) _changeSpeed = TryBind<ChangeAudioSpeedDelegate>(library, "ac_change_audio_speed_utf8");

        _trimMediaCancel = TryBind<TrimMediaCancelDelegate>(library, "ac_trim_media_cancel_utf8");
        if (_trimMediaCancel is null) _trimMedia = TryBind<TrimMediaDelegate>(library, "ac_trim_media_utf8");

        _extractAudioStream = TryBind<ExtractAudioStreamDelegate>(library, "ac_extract_audio_stream_utf8");
    }

    public static bool TryProbe(string path, out MediaProbeInfo result) =>
        TryProbe(path, out result, CancellationToken.None, out _);

    public static bool TryProbe(
        string path,
        out MediaProbeInfo result,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        result = MediaProbeInfo.Unknown;
        errorMessage = null;
        var native = Shared.Value;
        if (native is null)
        {
            errorMessage = "AstraCore 原生库未加载";
            return false;
        }

        var pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        var errorPointer = Marshal.AllocHGlobal(1024);
        try
        {
            var info = new NativeMediaInfo { StructSize = (uint)Marshal.SizeOf<NativeMediaInfo>() };
            int code;
            if (native._probeCancel is not null)
            {
                CancelCallbackDelegate cancelDelegate = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
                code = native._probeCancel(pathPointer, ref info, cancelDelegate, IntPtr.Zero, errorPointer, 1024);
            }
            else
            {
                code = native._probe!(pathPointer, ref info, errorPointer, 1024);
            }

            if (code < 0)
            {
                errorMessage = Marshal.PtrToStringUTF8(errorPointer);
                if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = $"原生探针返回错误码 {code}";
                return false;
            }

            result = new MediaProbeInfo(
                info.DurationSeconds,
                info.Width,
                info.Height,
                info.FrameRate,
                info.BitRate,
                info.HasVideo != 0,
                info.HasAudio != 0);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(errorPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    public static bool TryCheckEncoder(string encoderName, out bool available)
    {
        available = false;
        var native = Shared.Value;
        if (native?._checkEncoder is null || string.IsNullOrWhiteSpace(encoderName)) return false;

        var namePointer = Marshal.StringToCoTaskMemUTF8(encoderName);
        try
        {
            var result = native._checkEncoder(namePointer);
            available = (result == 1);
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    public static bool TryExtractWaveformPeaks(
        string path,
        int targetSampleRate,
        int samplesPerPeak,
        int maxPeaks,
        out double duration,
        out float[] peaks) =>
        TryExtractWaveformPeaks(path, targetSampleRate, samplesPerPeak, maxPeaks, out duration, out peaks, CancellationToken.None, out _);

    public static bool TryExtractWaveformPeaks(
        string path,
        int targetSampleRate,
        int samplesPerPeak,
        int maxPeaks,
        out double duration,
        out float[] peaks,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        duration = 0;
        peaks = Array.Empty<float>();
        errorMessage = null;
        var native = Shared.Value;
        if (native is null || string.IsNullOrWhiteSpace(path) || maxPeaks <= 0)
        {
            errorMessage = native is null ? "AstraCore 原生库未加载" : "参数无效";
            return false;
        }

        var pathPointer = Marshal.StringToCoTaskMemUTF8(path);
        var peaksBuffer = Marshal.AllocHGlobal(maxPeaks * sizeof(float));
        var errorPointer = Marshal.AllocHGlobal(1024);
        try
        {
            int count;
            if (native._extractWaveformCancel is not null)
            {
                CancelCallbackDelegate cancelDelegate = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
                count = native._extractWaveformCancel(
                    pathPointer, targetSampleRate, samplesPerPeak, peaksBuffer, maxPeaks,
                    out duration, cancelDelegate, IntPtr.Zero, errorPointer, 1024);
            }
            else if (native._extractWaveform is not null)
            {
                count = native._extractWaveform(
                    pathPointer, targetSampleRate, samplesPerPeak, peaksBuffer, maxPeaks,
                    out duration, errorPointer, 1024);
            }
            else
            {
                errorMessage = "未找到波形提取原生入口";
                return false;
            }

            if (count < 0)
            {
                errorMessage = Marshal.PtrToStringUTF8(errorPointer);
                if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = $"波形提取返回错误码 {count}";
                return false;
            }

            peaks = new float[count];
            Marshal.Copy(peaksBuffer, peaks, 0, count);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(errorPointer);
            Marshal.FreeHGlobal(peaksBuffer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    public static bool TryExtractAudioWav(
        string inputPath,
        string outputWavPath,
        int targetSampleRate = 16000,
        int channels = 1) =>
        TryExtractAudioWav(inputPath, outputWavPath, targetSampleRate, channels, CancellationToken.None, out _);

    public static bool TryExtractAudioWav(
        string inputPath,
        string outputWavPath,
        int targetSampleRate,
        int channels,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        errorMessage = null;
        var native = Shared.Value;
        if (native is null || string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputWavPath))
        {
            errorMessage = native is null ? "AstraCore 原生库未加载" : "路径参数无效";
            return false;
        }

        var inputPointer = Marshal.StringToCoTaskMemUTF8(inputPath);
        var outputPointer = Marshal.StringToCoTaskMemUTF8(outputWavPath);
        var errorPointer = Marshal.AllocHGlobal(1024);
        try
        {
            int code;
            if (native._extractWavCancel is not null)
            {
                CancelCallbackDelegate cancelDelegate = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
                code = native._extractWavCancel(
                    inputPointer, outputPointer, targetSampleRate, channels,
                    cancelDelegate, IntPtr.Zero, errorPointer, 1024);
            }
            else if (native._extractWav is not null)
            {
                code = native._extractWav(
                    inputPointer, outputPointer, targetSampleRate, channels, errorPointer, 1024);
            }
            else
            {
                errorMessage = "未找到音频抽取原生入口";
                return false;
            }

            if (code != 0)
            {
                errorMessage = Marshal.PtrToStringUTF8(errorPointer);
                if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = $"音频抽取返回错误码 {code}";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(errorPointer);
            Marshal.FreeCoTaskMem(outputPointer);
            Marshal.FreeCoTaskMem(inputPointer);
        }
    }

    public static bool TryChangeAudioSpeed(
        string inputPath,
        string outputWavPath,
        double speedFactor) =>
        TryChangeAudioSpeed(inputPath, outputWavPath, speedFactor, CancellationToken.None, out _);

    public static bool TryChangeAudioSpeed(
        string inputPath,
        string outputWavPath,
        double speedFactor,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        errorMessage = null;
        var native = Shared.Value;
        if (native is null || string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputWavPath))
        {
            errorMessage = native is null ? "AstraCore 原生库未加载" : "路径参数无效";
            return false;
        }

        if (speedFactor < 0.25 || speedFactor > 4.0)
        {
            errorMessage = "倍速参数必须在 0.25 到 4.0 之间";
            return false;
        }

        var inputPointer = Marshal.StringToCoTaskMemUTF8(inputPath);
        var outputPointer = Marshal.StringToCoTaskMemUTF8(outputWavPath);
        var errorPointer = Marshal.AllocHGlobal(1024);
        try
        {
            int code;
            if (native._changeSpeedCancel is not null)
            {
                CancelCallbackDelegate cancelDelegate = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
                code = native._changeSpeedCancel(
                    inputPointer, outputPointer, speedFactor,
                    cancelDelegate, IntPtr.Zero, errorPointer, 1024);
            }
            else if (native._changeSpeed is not null)
            {
                code = native._changeSpeed(
                    inputPointer, outputPointer, speedFactor, errorPointer, 1024);
            }
            else
            {
                errorMessage = "未找到音频倍速处理原生入口";
                return false;
            }

            if (code != 0)
            {
                errorMessage = Marshal.PtrToStringUTF8(errorPointer);
                if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = $"音频倍速返回错误码 {code}";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(errorPointer);
            Marshal.FreeCoTaskMem(outputPointer);
            Marshal.FreeCoTaskMem(inputPointer);
        }
    }

    public static bool TryTrimMedia(
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        bool streamCopy = true) =>
        TryTrimMedia(inputPath, outputPath, startSeconds, durationSeconds, streamCopy, CancellationToken.None, out _);

    public static bool TryTrimMedia(
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        bool streamCopy,
        CancellationToken cancellationToken,
        out string? errorMessage)
    {
        errorMessage = null;
        var native = Shared.Value;
        if (native is null || string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            errorMessage = native is null ? "AstraCore 原生库未加载" : "路径参数无效";
            return false;
        }

        var inputPointer = Marshal.StringToCoTaskMemUTF8(inputPath);
        var outputPointer = Marshal.StringToCoTaskMemUTF8(outputPath);
        var errorPointer = Marshal.AllocHGlobal(1024);
        try
        {
            int code;
            int copyFlag = streamCopy ? 1 : 0;
            if (native._trimMediaCancel is not null)
            {
                CancelCallbackDelegate cancelDelegate = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
                code = native._trimMediaCancel(
                    inputPointer, outputPointer, startSeconds, durationSeconds, copyFlag,
                    cancelDelegate, IntPtr.Zero, errorPointer, 1024);
            }
            else if (native._trimMedia is not null)
            {
                code = native._trimMedia(
                    inputPointer, outputPointer, startSeconds, durationSeconds, copyFlag, errorPointer, 1024);
            }
            else
            {
                errorMessage = "未找到媒体裁剪原生入口";
                return false;
            }

            if (code != 0)
            {
                errorMessage = Marshal.PtrToStringUTF8(errorPointer);
                if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = $"媒体裁剪返回错误码 {code}";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(errorPointer);
            Marshal.FreeCoTaskMem(outputPointer);
            Marshal.FreeCoTaskMem(inputPointer);
        }
    }

    public static bool TryExtractAudioStream(
        string inputMedia,
        string outputAudio,
        int streamIndex,
        out string? errorMessage)
    {
        errorMessage = null;
        var native = Shared.Value;
        if (native?._extractAudioStream is null || string.IsNullOrWhiteSpace(inputMedia) || string.IsNullOrWhiteSpace(outputAudio))
        {
            errorMessage = native is null ? "AstraCore 原生库未加载" : "路径参数无效";
            return false;
        }

        var inputPointer = Marshal.StringToCoTaskMemUTF8(inputMedia);
        var outputPointer = Marshal.StringToCoTaskMemUTF8(outputAudio);
        var errorPointer = Marshal.AllocHGlobal(1024);
        try
        {
            var code = native._extractAudioStream(inputPointer, outputPointer, streamIndex, errorPointer, 1024);
            if (code != 0)
            {
                errorMessage = Marshal.PtrToStringUTF8(errorPointer);
                if (string.IsNullOrWhiteSpace(errorMessage)) errorMessage = $"音频流提取返回错误码 {code}";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(errorPointer);
            Marshal.FreeCoTaskMem(outputPointer);
            Marshal.FreeCoTaskMem(inputPointer);
        }
    }

    private static AstraCoreNative? Create()
    {
        var path = AstraCoreRuntime.Current?.Resolve("native");
        if (path is null) return null;
        try { return new AstraCoreNative(NativeDependencyLoader.Load(path)); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException or System.ComponentModel.Win32Exception or FileLoadException)
        {
            return null;
        }
    }

    private static T Bind<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static T? TryBind<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;
}
