using Avalonia;
using System;
using System.Diagnostics;

namespace AstraCat;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var diagnosticIndex = Array.IndexOf(args, "--mpv-render-smoke");
        if (diagnosticIndex < 0) diagnosticIndex = Array.IndexOf(args, "--mpv-render-benchmark");
        if (diagnosticIndex < 0) diagnosticIndex = Array.IndexOf(args, "--mpv-wid-benchmark");
        if (diagnosticIndex >= 0 && args.Length > diagnosticIndex + 2)
        {
            Trace.Listeners.Add(new TextWriterTraceListener(args[diagnosticIndex + 2] + ".trace"));
            Trace.AutoFlush = true;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                // OpenGlControlBase and libmpv must share an OpenGL-capable
                // backend. Do not silently fall back to software rendering.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            })
            .WithInterFont()
            .LogToTrace();
}
