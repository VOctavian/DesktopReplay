using System.Text;

namespace DesktopReplay.Core;

/// <summary>Simple thread-safe file logger: %LOCALAPPDATA%\DesktopReplay\logs\yyyy-MM-dd.log</summary>
public static class Log
{
    private static readonly object Sync = new();
    public static string Directory { get; } = Path.Combine(AppPaths.LocalData, "logs");

    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR", ex == null ? msg : $"{msg}: {ex}");

    public static void Write(string level, string msg)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = Path.Combine(Directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }

    public static void Cleanup(int keepDays = 14)
    {
        try
        {
            foreach (var f in new DirectoryInfo(Directory).EnumerateFiles("*.log"))
                if (f.LastWriteTime < DateTime.Now.AddDays(-keepDays)) f.Delete();
        }
        catch { }
    }
}

public static class AppPaths
{
    public static string LocalData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopReplay");
    public static string Roaming { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopReplay");
    public static string DefaultBuffer => Path.Combine(LocalData, "Buffer");
    public static string DefaultOutput => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DesktopReplay");
}
