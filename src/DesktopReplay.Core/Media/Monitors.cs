using DesktopReplay.Core.Interop;
using Vortice.DXGI;

namespace DesktopReplay.Core.Media;

public sealed record MonitorInfo(
    string DeviceName,
    IntPtr Handle,
    Native.RECT Bounds,
    bool IsPrimary,
    bool IsHdr,
    int AdapterIndex,
    string AdapterName,
    int Number)
{
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public string DisplayName => $"{Number}: {Width}×{Height}{(IsPrimary ? " ★" : "")}{(IsHdr ? " HDR" : "")}";
    public override string ToString() => DisplayName;
}

public static class Monitors
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    var adesc = adapter.Description1;
                    if ((adesc.Flags & AdapterFlags.Software) != 0) continue;
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var d = output.Description;
                            if (!d.AttachedToDesktop) continue;
                            bool hdr = false;
                            try
                            {
                                using var o6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                                if (o6 != null) hdr = o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            }
                            catch { }
                            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
                            Native.GetMonitorInfo(d.Monitor, ref info);
                            list.Add(new MonitorInfo(
                                d.DeviceName,
                                d.Monitor,
                                info.rcMonitor,
                                (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                                hdr,
                                (int)a,
                                adesc.Description,
                                0));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Monitor enumeration failed", ex);
        }

        // Stable numbering: primary first, then left-to-right.
        var ordered = list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i] = ordered[i] with { Number = i + 1 };
        return ordered;
    }

    /// <summary>Monitors selected in settings (falls back to the primary one).</summary>
    public static List<MonitorInfo> Resolve(IEnumerable<string> deviceNames)
    {
        var all = Enumerate();
        var selected = all.Where(m => deviceNames.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            var primary = all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
            if (primary != null) selected.Add(primary);
        }
        return selected;
    }
}
