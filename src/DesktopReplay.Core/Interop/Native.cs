using System.Runtime.InteropServices;
using System.Text;

namespace DesktopReplay.Core.Interop;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public readonly bool IsEmpty => Right <= Left || Bottom <= Top;
        public static RECT Intersect(RECT a, RECT b) => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    public const uint TH32CS_SNAPPROCESS = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder sb, ref int size);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

    private static int _gpuPrioritySet;

    /// <summary>
    /// Raises this process's GPU scheduling priority so capture/encode work is not starved by a game
    /// that keeps the GPU at 100% (same approach as OBS). Measured under full GPU load: HIGH still drops
    /// to ~16 fps, REALTIME holds a steady 30. Our GPU work is ~1–2 ms per frame, so the game loses little.
    /// </summary>
    public static void SetGpuPriority(bool boost)
    {
        int want = boost ? 1 : 2;
        if (Interlocked.Exchange(ref _gpuPrioritySet, want) == want) return;
        var self = System.Diagnostics.Process.GetCurrentProcess().Handle;
        var classes = boost ? new[] { (5, "REALTIME"), (4, "HIGH") } : new[] { (2, "NORMAL") };
        foreach (var (cls, name) in classes)
        {
            int status = D3DKMTSetProcessSchedulingPriorityClass(self, cls);
            if (status == 0)
            {
                Log.Info($"GPU scheduling priority: {name}");
                return;
            }
        }
        Log.Warn("Could not raise GPU scheduling priority");
    }

    /// <summary>Registers the calling thread with MMCSS ("Capture", "Pro Audio", ...).</summary>
    public static void JoinMmcss(string task)
    {
        uint index = 0;
        var h = AvSetMmThreadCharacteristicsW(task, ref index);
        if (h != IntPtr.Zero) AvSetMmThreadPriority(h, 1 /* AVRT_PRIORITY_HIGH */);
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Snapshot of all processes: pid → (parent pid, exe name).</summary>
    public static Dictionary<uint, (uint Parent, string Exe)> SnapshotProcesses()
    {
        var result = new Dictionary<uint, (uint, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
            {
                do { result[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile); }
                while (Process32NextW(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }
        return result;
    }

    /// <summary>True if <paramref name="pid"/> is <paramref name="root"/> or one of its descendants.</summary>
    public static bool IsInTree(uint pid, uint root, Dictionary<uint, (uint Parent, string Exe)> procs)
    {
        for (int guard = 0; guard < 64 && pid != 0; guard++)
        {
            if (pid == root) return true;
            if (!procs.TryGetValue(pid, out var e) || e.Parent == pid) return false;
            pid = e.Parent;
        }
        return false;
    }

    /// <summary>Extended frame bounds (without invisible resize borders), falls back to GetWindowRect.</summary>
    public static RECT GetVisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;
}

/// <summary>High resolution one-shot waiter used by the video and audio clocks.</summary>
public sealed class PreciseTimer : IDisposable
{
    private IntPtr _timer;

    public PreciseTimer()
    {
        _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (_timer == IntPtr.Zero)
            _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0, Native.TIMER_ALL_ACCESS);
    }

    /// <summary>Waits until the given <see cref="Clock.NowUs"/> value.</summary>
    public void WaitUntilUs(long targetUs)
    {
        long remainingUs = targetUs - Clock.NowUs();
        if (remainingUs <= 0) return;
        long due = -remainingUs * 10; // relative, 100ns units
        if (_timer != IntPtr.Zero && Native.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            Native.WaitForSingleObject(_timer, 2000);
        else
            Thread.Sleep(TimeSpan.FromMicroseconds(remainingUs));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) { Native.CloseHandle(_timer); _timer = IntPtr.Zero; }
    }
}

public static class Clock
{
    private static readonly double UsPerTick = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Microseconds since an arbitrary epoch (QPC based).</summary>
    public static long NowUs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * UsPerTick);
}
