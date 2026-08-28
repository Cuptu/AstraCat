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
            var args = desktop.Args ?? [];
            var smokeIndex = Array.IndexOf(args, "--mpv-render-smoke");
            var benchmarkIndex = Array.IndexOf(args, "--mpv-render-benchmark");
            var widBenchmarkIndex = Array.IndexOf(args, "--mpv-wid-benchmark");
            desktop.MainWindow = widBenchmarkIndex >= 0 && args.Length > widBenchmarkIndex + 2
                ? new MpvWidBenchmarkWindow(args[widBenchmarkIndex + 1], args[widBenchmarkIndex + 2], desktop)
                : benchmarkIndex >= 0 && args.Length > benchmarkIndex + 2
                ? new MpvRenderBenchmarkWindow(args[benchmarkIndex + 1], args[benchmarkIndex + 2], desktop)
                : smokeIndex >= 0 && args.Length > smokeIndex + 2
                    ? new MpvRenderSmokeWindow(args[smokeIndex + 1], args[smokeIndex + 2], desktop)
                    : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
