using System.Diagnostics;
using Avalonia.Input;

namespace AstraCat;

/// <summary>
/// Provides cross-platform operating system utilities for desktop interactions,
/// shell commands, and input modifier mapping.
/// </summary>
internal static class PlatformHelper
{
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", path) { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"无法打开目录 {path}: {ex.Message}");
        }
    }

    public static bool HasCommandModifier(KeyModifiers modifiers) =>
        OperatingSystem.IsMacOS()
            ? modifiers.HasFlag(KeyModifiers.Meta)
            : modifiers.HasFlag(KeyModifiers.Control);
}
