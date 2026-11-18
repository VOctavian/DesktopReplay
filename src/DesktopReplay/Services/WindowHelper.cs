using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopReplay.Services;

public static class WindowHelper
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    /// <summary>Keeps our own windows (overlay, toasts) out of recordings and screenshots.</summary>
    /// <summary>--debug-ui: let our windows be captured (for UI screenshots during development).</summary>
    public static bool AllowCapture { get; set; }

    public static void ExcludeFromCapture(Window w)
    {
        if (AllowCapture) return;
        var h = new WindowInteropHelper(w).EnsureHandle();
        SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Hides the window from Alt+Tab; toasts are also non-activating and click-through.</summary>
    public static void MakeToolWindow(Window w, bool clickThrough)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (clickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    public static void ForceForeground(Window w) => SetForegroundWindow(new WindowInteropHelper(w).Handle);

    /// <summary>Monitor rectangles in physical pixels: the one under the cursor, and the one with the foreground window.</summary>
    public static (RECT Monitor, RECT Work) CursorMonitor()
    {
        GetCursorPos(out var p);
        return Info(MonitorFromPoint(p, 2));
    }

    public static (RECT Monitor, RECT Work) ForegroundMonitor() => Info(MonitorFromWindow(GetForegroundWindow(), 2));

    private static (RECT, RECT) Info(IntPtr mon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        return (mi.rcMonitor, mi.rcWork);
    }

    /// <summary>Places a window using physical pixel coordinates (correct across mixed-DPI monitors).</summary>
    public static void PlacePhysical(Window w, RECT r)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static string FormatBytes(long bytes)
    {
        string[] u = Loc.I.Language == "en" ? new[] { "B", "KB", "MB", "GB", "TB" } : new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public static string FormatDuration(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
