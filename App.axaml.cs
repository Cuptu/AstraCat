using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AstraCat;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if NATIVE_AOT
            desktop.MainWindow = new MainWindow();
#else
            var args = desktop.Args ?? [];
            var smokeIndex = Array.IndexOf(args, "--mpv-render-smoke");
            var benchmarkIndex = Array.IndexOf(args, "--mpv-render-benchmark");
            var widBenchmarkIndex = Array.IndexOf(args, "--mpv-wid-benchmark");
            var d3d11BenchmarkIndex = Array.IndexOf(args, "--mpv-d3d11-benchmark");
            var mediaServiceSmokeIndex = Array.IndexOf(args, "--media-service-smoke");
            desktop.MainWindow = mediaServiceSmokeIndex >= 0 && args.Length > mediaServiceSmokeIndex + 3
                ? new MediaServiceSmokeWindow(args[mediaServiceSmokeIndex + 1], args[mediaServiceSmokeIndex + 2],
                    args[mediaServiceSmokeIndex + 3], desktop)
                : d3d11BenchmarkIndex >= 0 && args.Length > d3d11BenchmarkIndex + 2
                ? new MpvD3D11BenchmarkWindow(args[d3d11BenchmarkIndex + 1], args[d3d11BenchmarkIndex + 2], desktop)
                : widBenchmarkIndex >= 0 && args.Length > widBenchmarkIndex + 2
                ? new MpvWidBenchmarkWindow(args[widBenchmarkIndex + 1], args[widBenchmarkIndex + 2], desktop)
                : benchmarkIndex >= 0 && args.Length > benchmarkIndex + 2
                ? new MpvRenderBenchmarkWindow(args[benchmarkIndex + 1], args[benchmarkIndex + 2], desktop)
                : smokeIndex >= 0 && args.Length > smokeIndex + 2
                    ? new MpvRenderSmokeWindow(args[smokeIndex + 1], args[smokeIndex + 2], desktop)
                    : new MainWindow();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}
