using DesktopReplay.Core.Interop;

namespace DesktopReplay.Core.Engine;

/// <summary>
/// Periodically collects screen rectangles of windows that belong to excluded processes
/// (e.g. a password manager). Those areas are painted black in recordings and screenshots.
/// </summary>
public sealed class ExclusionTracker : IDisposable
{
    private readonly Timer _timer;
    private volatile HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private volatile Native.RECT[] _rects = Array.Empty<Native.RECT>();
    private readonly Dictionary<uint, string> _exeCache = new();
    private int _polls;

    public ExclusionTracker(IEnumerable<string> processNames)
    {
        SetProcesses(processNames);
        _timer = new Timer(_ => Poll(), null, 0, 200);
    }

    public Native.RECT[] Rects => _rects;

    public void SetProcesses(IEnumerable<string> names)
    {
        _names = new HashSet<string>(
            names.Select(n => n.Trim()).Where(n => n.Length > 0).Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe"),
            StringComparer.OrdinalIgnoreCase);
    }

    private void Poll()
    {
        var names = _names;
        if (names.Count == 0)
        {
            _rects = Array.Empty<Native.RECT>();
            return;
        }
        var list = new List<Native.RECT>();
        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.IsCloaked(hwnd)) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!_exeCache.TryGetValue(pid, out var exe))
                {
                    exe = Path.GetFileName(Native.GetProcessPath(pid) ?? "");
                    _exeCache[pid] = exe;
                }
                if (names.Contains(exe))
                {
                    var r = Native.GetVisibleBounds(hwnd);
                    if (!r.IsEmpty) list.Add(r);
                }
                return true;
            }, IntPtr.Zero);
            if (++_polls % 50 == 0) _exeCache.Clear(); // pids get reused
        }
        catch (Exception ex)
        {
            Log.Warn($"Exclusion poll failed: {ex.Message}");
        }
        _rects = list.ToArray();
    }

    public void Dispose() => _timer.Dispose();
}
