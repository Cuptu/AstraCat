using Avalonia;
using System;
using System.Diagnostics;

namespace AstraCat;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] is "--native-aot-smoke" or "--native-aot-ui-smoke")
        {
            Environment.ExitCode = NativeAotSmoke.Run(args[1], args[0] == "--native-aot-ui-smoke");
            return;
        }
        var diagnosticIndex = Array.IndexOf(args, "--mpv-render-smoke");
        if (diagnosticIndex < 0) diagnosticIndex = Array.IndexOf(args, "--mpv-render-benchmark");
        if (diagnosticIndex < 0) diagnosticIndex = Array.IndexOf(args, "--mpv-wid-benchmark");
        if (diagnosticIndex < 0) diagnosticIndex = Array.IndexOf(args, "--mpv-d3d11-benchmark");
        if (diagnosticIndex < 0) diagnosticIndex = Array.IndexOf(args, "--media-service-smoke");
        if (diagnosticIndex >= 0 && args.Length > diagnosticIndex + 2)
        {
            Trace.Listeners.Add(new TextWriterTraceListener(args[diagnosticIndex + 2] + ".trace"));
            Trace.AutoFlush = true;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                // OpenGlControlBase and libmpv must share an OpenGL-capable
                // backend. Do not silently fall back to software rendering.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            builder = builder.With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software]
            });
        }

        return builder
            .WithInterFont()
            .LogToTrace();
    }
}
