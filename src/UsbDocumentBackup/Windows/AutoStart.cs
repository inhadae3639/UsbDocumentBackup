using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UsbDocumentBackup.Windows;

/// <summary>
/// Per-user "run at login" registration. Deliberately user scope only: the design does not include
/// a Windows service that would run before login.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsbDocumentBackup";

    /// <summary>
    /// Path of the executable as Windows should launch it. Returns null when the app is running
    /// from a build output or a temporary folder, so a throwaway path never lands in the Run key.
    /// </summary>
    public static string? StableExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? string.Empty;

        var unstableRoots = new[]
        {
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.InternetCache),
        };

        foreach (var root in unstableRoots)
        {
            if (!string.IsNullOrEmpty(root)
                && directory.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var segments = directory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase))
            && segments.Any(s => s.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Release", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return full;
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>Registers the current executable. Returns false when the path is not stable enough to register.</summary>
    public static bool Enable()
    {
        var path = StableExecutablePath();
        if (path is null)
        {
            return false;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        // Quoted so a path containing spaces is passed as one argument.
        key.SetValue(ValueName, "\"" + path + "\"", RegistryValueKind.String);
        return true;
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + path + "\"",
            UseShellExecute = true,
        });
    }
}
