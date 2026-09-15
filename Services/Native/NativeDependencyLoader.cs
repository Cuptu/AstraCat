using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AstraCat;

internal static class NativeDependencyLoader
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private static readonly object Sync = new();
    private static readonly HashSet<string> RegisteredDirectories = new(StringComparer.OrdinalIgnoreCase);

    public static IntPtr Load(string libraryPath)
    {
        if (Path.IsPathRooted(libraryPath) || File.Exists(libraryPath))
        {
            var fullPath = Path.GetFullPath(libraryPath);
            if (OperatingSystem.IsWindows()) RegisterWindowsDirectory(Path.GetDirectoryName(fullPath)!);
            return NativeLibrary.Load(fullPath);
        }
        return NativeLibrary.Load(libraryPath);
    }

    private static void RegisterWindowsDirectory(string directory)
    {
        lock (Sync)
        {
            if (RegisteredDirectories.Contains(directory)) return;
            if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs | LoadLibrarySearchUserDirs))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用安全的原生 DLL 搜索策略。");
            if (AddDllDirectory(directory) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法注册 AstraCore DLL 目录：{directory}");
            RegisteredDirectories.Add(directory);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
