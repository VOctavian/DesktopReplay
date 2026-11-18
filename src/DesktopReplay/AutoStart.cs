using DesktopReplay.Core;
using Microsoft.Win32;

namespace DesktopReplay;

/// <summary>Windows autostart via HKCU\...\Run (no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopReplay";

    /// <summary>Velopack puts a stable launcher one level above "current"; prefer it so updates keep autostart working.</summary>
    private static string ExePath
    {
        get
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DesktopReplay.exe");
            var dir = Path.GetDirectoryName(exe)!;
            if (string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
            {
                var stub = Path.Combine(Path.GetDirectoryName(dir)!, "DesktopReplay.exe");
                if (File.Exists(stub)) return stub;
            }
            return exe;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\" --minimized");
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            Log.Error("AutoStart change failed", ex);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }
}
