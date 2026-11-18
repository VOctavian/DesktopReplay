using System.Runtime.InteropServices;

namespace DesktopReplay.Core.Interop;

// Minimal Core Audio (WASAPI) interop.

public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
    public PropertyKey(Guid f, uint p) { fmtid = f; pid = p; }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;
    [FieldOffset(8)] public uint blobSize;
    [FieldOffset(16)] public IntPtr blobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public static WaveFormatEx Float(int rate, int channels) => new()
    {
        wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
        nChannels = (ushort)channels,
        nSamplesPerSec = (uint)rate,
        wBitsPerSample = 32,
        nBlockAlign = (ushort)(channels * 4),
        nAvgBytesPerSec = (uint)(rate * channels * 4),
        cbSize = 0,
    };

    public static WaveFormatEx Pcm16(int rate, int channels) => new()
    {
        wFormatTag = 1, // WAVE_FORMAT_PCM
        nChannels = (ushort)channels,
        nSamplesPerSec = (uint)rate,
        wBitsPerSample = 16,
        nBlockAlign = (ushort)(channels * 2),
        nAvgBytesPerSec = (uint)(rate * channels * 2),
        cbSize = 0,
    };
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    [PreserveSig] int RegisterSessionNotification(IntPtr notification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optOut);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }

public static class CoreAudio
{
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint CLSCTX_ALL = 0x17;

    public const string VirtualProcessLoopback = "VAD\\Process_Loopback";

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    public static string GetFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0, out var store) != 0) return "?";
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return "?";
            try { return Marshal.PtrToStringUni(pv.pointerValue) ?? "?"; }
            finally { PropVariantClear(ref pv); Marshal.ReleaseComObject(store); }
        }
        catch { return "?"; }
    }

    public sealed record AudioDevice(string Id, string Name, bool IsDefault);

    public static List<AudioDevice> ListDevices(EDataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = CreateEnumerator();
            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(flow, flow == EDataFlow.Capture ? ERole.Communications : ERole.Console, out var def) == 0)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }
            if (en.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var coll) == 0)
            {
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (coll.Item(i, out var dev) != 0) continue;
                    dev.GetId(out var id);
                    list.Add(new AudioDevice(id, GetFriendlyName(dev), id == defaultId));
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(coll);
            }
            Marshal.ReleaseComObject(en);
        }
        catch (Exception ex)
        {
            Log.Error("ListDevices failed", ex);
        }
        return list;
    }

    /// <summary>Process ids that currently own an audio session on any active render endpoint.</summary>
    public static HashSet<uint> GetSessionProcessIds()
    {
        var result = new HashSet<uint>();
        try
        {
            var en = CreateEnumerator();
            if (en.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out var coll) == 0)
            {
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (coll.Item(i, out var dev) != 0) continue;
                    var iid = IID_IAudioSessionManager2;
                    if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) == 0 && obj is IAudioSessionManager2 mgr)
                    {
                        if (mgr.GetSessionEnumerator(out var sessions) == 0)
                        {
                            sessions.GetCount(out int count);
                            for (int s = 0; s < count; s++)
                            {
                                if (sessions.GetSession(s, out var ctl) != 0) continue;
                                // state 2 = expired
                                if (ctl.GetState(out int state) == 0 && state != 2 && ctl.IsSystemSoundsSession() != 0 && ctl.GetProcessId(out uint pid) == 0 && pid != 0)
                                    result.Add(pid);
                                Marshal.ReleaseComObject(ctl);
                            }
                            Marshal.ReleaseComObject(sessions);
                        }
                        Marshal.ReleaseComObject(mgr);
                    }
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(coll);
            }
            Marshal.ReleaseComObject(en);
        }
        catch (Exception ex)
        {
            Log.Warn($"Session enumeration failed: {ex.Message}");
        }
        return result;
    }
}
