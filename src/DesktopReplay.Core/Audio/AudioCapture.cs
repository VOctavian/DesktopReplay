using System.Runtime.InteropServices;
using DesktopReplay.Core.Interop;

namespace DesktopReplay.Core.Audio;

public static class AudioFormat
{
    public const int Rate = 48000;
    public const int Channels = 2;
}

/// <summary>Thread-safe ring buffer of interleaved stereo float samples.</summary>
public sealed class AudioRing
{
    private float[] _buf;
    private int _read;
    private int _count;
    private readonly object _sync = new();

    public AudioRing(int capacitySamples) => _buf = new float[capacitySamples];

    public int Count { get { lock (_sync) return _count; } }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_sync)
        {
            if (data.Length > _buf.Length) data = data[^_buf.Length..];
            int overflow = _count + data.Length - _buf.Length;
            if (overflow > 0) { _read = (_read + overflow) % _buf.Length; _count -= overflow; }
            int write = (_read + _count) % _buf.Length;
            int first = Math.Min(data.Length, _buf.Length - write);
            data[..first].CopyTo(_buf.AsSpan(write));
            data[first..].CopyTo(_buf);
            _count += data.Length;
        }
    }

    public void WriteSilence(int samples)
    {
        Span<float> zeros = stackalloc float[Math.Min(samples, 4096)];
        while (samples > 0)
        {
            int n = Math.Min(samples, zeros.Length);
            Write(zeros[..n]);
            samples -= n;
        }
    }

    public int Read(Span<float> dst)
    {
        lock (_sync)
        {
            int n = Math.Min(dst.Length, _count);
            int first = Math.Min(n, _buf.Length - _read);
            _buf.AsSpan(_read, first).CopyTo(dst);
            _buf.AsSpan(0, n - first).CopyTo(dst[first..]);
            _read = (_read + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }

    public void Skip(int samples)
    {
        lock (_sync)
        {
            samples = Math.Min(samples, _count);
            _read = (_read + samples) % _buf.Length;
            _count -= samples;
        }
    }

    public void Clear() { lock (_sync) { _read = 0; _count = 0; } }
}

/// <summary>
/// A WASAPI capture stream (device or process loopback) converted to 48 kHz stereo float by Windows
/// (AUTOCONVERTPCM) and pushed into a ring the mixer pulls from.
/// </summary>
public sealed class CaptureSource : IDisposable
{
    // Mixer pulls with this latency so capture jitter is absorbed.
    public const int TargetLatencySamples = AudioFormat.Rate * AudioFormat.Channels / 10;      // 100 ms
    private const int MaxLatencySamples = TargetLatencySamples + AudioFormat.Rate * AudioFormat.Channels / 20; // +50 ms

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly bool _pcm16;
    private readonly AutoResetEvent _event = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private bool _primed;
    private readonly Action<float[], int, AudioRing>? _processor;

    public string Name { get; }
    public AudioRing Ring { get; } = new(AudioFormat.Rate * AudioFormat.Channels * 2);
    public bool Faulted { get; private set; }

    private CaptureSource(string name, IAudioClient client, bool pcm16, Action<float[], int, AudioRing>? processor)
    {
        Name = name;
        _client = client;
        _pcm16 = pcm16;
        _processor = processor;
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));
        var iid = CoreAudio.IID_IAudioCaptureClient;
        Marshal.ThrowExceptionForHR(_client.GetService(ref iid, out var svc));
        _capture = (IAudioCaptureClient)svc;
        Marshal.ThrowExceptionForHR(_client.Start());
        _thread = new Thread(Loop) { Name = "Audio " + name, IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Called by the mixer: fills <paramref name="dst"/> with aligned samples (silence when not ready).</summary>
    public void Pull(Span<float> dst)
    {
        int count = Ring.Count;
        if (!_primed)
        {
            if (count < TargetLatencySamples) { dst.Clear(); return; }
            _primed = true;
        }
        if (count > MaxLatencySamples) Ring.Skip((count - TargetLatencySamples) & ~1);
        int n = Ring.Read(dst);
        if (n < dst.Length)
        {
            dst[n..].Clear();
            _primed = false;
        }
    }

    private void Loop()
    {
        var conv = new float[0];
        try
        {
            while (_running)
            {
                _event.WaitOne(100);
                while (_running)
                {
                    int hr = _capture.GetNextPacketSize(out uint packet);
                    if (hr != 0) { Marshal.ThrowExceptionForHR(hr); }
                    if (packet == 0) break;
                    Marshal.ThrowExceptionForHR(_capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _));
                    int samples = (int)frames * AudioFormat.Channels;
                    if (conv.Length < samples) conv = new float[samples];
                    if ((flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                    {
                        Array.Clear(conv, 0, samples);
                    }
                    else if (_pcm16)
                    {
                        unsafe
                        {
                            var src = (short*)data;
                            for (int i = 0; i < samples; i++) conv[i] = src[i] / 32768f;
                        }
                    }
                    else
                    {
                        Marshal.Copy(data, conv, 0, samples);
                    }
                    _capture.ReleaseBuffer(frames);
                    if (_processor != null) _processor(conv, samples, Ring);
                    else Ring.Write(conv.AsSpan(0, samples));
                }
            }
        }
        catch (Exception ex)
        {
            Faulted = true;
            Log.Warn($"Audio source '{Name}' stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _running = false;
        _event.Set();
        _thread.Join(1000);
        try { _client.Stop(); } catch { }
        try { Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
        _event.Dispose();
    }

    // ---------- Factories ----------

    private const long BufferDuration = 200_000; // 20 ms in 100ns units

    /// <summary>Opens a capture endpoint (microphone). Empty id = default communications device.</summary>
    public static CaptureSource OpenDevice(string deviceId, Action<float[], int, AudioRing>? processor)
    {
        var en = CoreAudio.CreateEnumerator();
        try
        {
            IMMDevice? dev = null;
            if (!string.IsNullOrEmpty(deviceId) && en.GetDevice(deviceId, out var d) == 0) dev = d;
            if (dev == null) Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Communications, out dev));
            var name = CoreAudio.GetFriendlyName(dev!);
            var iid = CoreAudio.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(dev!.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out var obj));
            Marshal.ReleaseComObject(dev);
            var client = (IAudioClient)obj;
            var fmt = WaveFormatEx.Float(AudioFormat.Rate, AudioFormat.Channels);
            Marshal.ThrowExceptionForHR(client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED,
                CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | CoreAudio.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | CoreAudio.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                BufferDuration, 0, ref fmt, IntPtr.Zero));
            Log.Info($"Microphone opened: {name}");
            return new CaptureSource("mic:" + name, client, false, processor);
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    /// <summary>Captures the audio of a process and all its children (Windows 10 2004+).</summary>
    public static CaptureSource OpenProcess(uint pid, string label)
    {
        var client = ActivateProcessLoopback(pid);
        bool pcm16 = false;
        var fmt = WaveFormatEx.Float(AudioFormat.Rate, AudioFormat.Channels);
        uint flags = CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK | CoreAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK | CoreAudio.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
        int hr = client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration, 0, ref fmt, IntPtr.Zero);
        if (hr < 0)
        {
            // Some builds only accept integer PCM for process loopback.
            Marshal.ReleaseComObject(client);
            client = ActivateProcessLoopback(pid);
            fmt = WaveFormatEx.Pcm16(AudioFormat.Rate, AudioFormat.Channels);
            Marshal.ThrowExceptionForHR(client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration, 0, ref fmt, IntPtr.Zero));
            pcm16 = true;
        }
        return new CaptureSource($"{label}:{pid}", client, pcm16, null);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    private static IAudioClient ActivateProcessLoopback(uint pid)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK(1), TargetProcessId, ProcessLoopbackMode = INCLUDE_TREE(0) }
        IntPtr p = Marshal.AllocHGlobal(12);
        IntPtr pv = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.WriteInt32(p, 0, 1);
            Marshal.WriteInt32(p, 4, (int)pid);
            Marshal.WriteInt32(p, 8, 0);
            Marshal.StructureToPtr(new PropVariant { vt = 65 /* VT_BLOB */, blobSize = 12, blobData = p }, pv, false);

            var handler = new ActivationHandler();
            var iid = CoreAudio.IID_IAudioClient;
            CoreAudio.ActivateAudioInterfaceAsync(CoreAudio.VirtualProcessLoopback, ref iid, pv, handler, out var op);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Process loopback activation timed out");
            op.GetActivateResult(out int hr, out var obj);
            Marshal.ThrowExceptionForHR(hr);
            Marshal.ReleaseComObject(op);
            return (IAudioClient)obj;
        }
        finally
        {
            Marshal.FreeHGlobal(pv);
            Marshal.FreeHGlobal(p);
        }
    }
}
