using System.Diagnostics;
using DesktopReplay.Core.Interop;

namespace DesktopReplay.Core.Engine;

/// <summary>
/// Tracks the "active application" for audio track 2 and file naming.
/// A fullscreen / borderless-fullscreen app becomes the sticky game: it stays selected on Alt+Tab
/// until it exits or another fullscreen app takes focus. Without a game, the foreground app is used.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private static readonly HashSet<string> ShellExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "textinputhost.exe", "lockapp.exe", "applicationframehost.exe", "taskmgr.exe",
    };
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "discord.exe", "discordptb.exe", "discordcanary.exe" };

    private readonly Timer _timer;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly object _sync = new();

    private uint _gamePid;
    private string _gameName = "";
    private uint _foregroundPid;
    private string _foregroundName = "";

    public GameDetector()
    {
        _timer = new Timer(_ => Poll(), null, 0, 1000);
    }

    public uint GamePid { get { lock (_sync) return _gamePid; } }

    /// <summary>Pid used for audio track 2.</summary>
    public uint ActivePid { get { lock (_sync) return _gamePid != 0 ? _gamePid : _foregroundPid; } }

    /// <summary>Name for files: the game, otherwise the foreground app, otherwise "Desktop".</summary>
    public string ActiveName
    {
        get
        {
            lock (_sync)
            {
                if (_gamePid != 0) return _gameName;
                return string.IsNullOrEmpty(_foregroundName) ? "Desktop" : _foregroundName;
            }
        }
    }

    private void Poll()
    {
        try
        {
            lock (_sync)
            {
                if (_gamePid != 0 && !IsAlive(_gamePid))
                {
                    Log.Info($"Game exited: {_gameName}");
                    _gamePid = 0;
                    _gameName = "";
                }
            }

            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == _ownPid) return; // our overlay: keep previous state

            var path = Native.GetProcessPath(pid);
            var exe = path != null ? Path.GetFileName(path) : "";
            var cls = Native.GetClassName(hwnd);
            bool shell = ShellExe.Contains(exe) || ShellClasses.Contains(cls) || hwnd == Native.GetShellWindow();

            lock (_sync)
            {
                if (shell)
                {
                    _foregroundPid = 0;
                    _foregroundName = "";
                    return;
                }
                if (_foregroundPid != pid)
                {
                    _foregroundPid = pid;
                    _foregroundName = FriendlyName(path, hwnd);
                }
                if (pid != _gamePid && !NotGames.Contains(exe) && IsFullscreen(hwnd))
                {
                    _gamePid = pid;
                    _gameName = _foregroundName;
                    Log.Info($"Game detected: {_gameName} ({exe}, pid {pid})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"GameDetector: {ex.Message}");
        }
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(mon, ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static bool IsAlive(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static string FriendlyName(string? path, IntPtr hwnd)
    {
        try
        {
            if (path != null)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var desc = info.FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 60 && !desc.Contains("Launcher", StringComparison.OrdinalIgnoreCase))
                    return desc;
                var product = info.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(product) && product.Length <= 60) return product;
            }
            var title = Native.GetWindowTitle(hwnd).Trim();
            if (!string.IsNullOrWhiteSpace(title) && title.Length <= 60) return title;
            if (path != null) return Path.GetFileNameWithoutExtension(path);
        }
        catch { }
        return "App";
    }

    public void Dispose() => _timer.Dispose();
}
