using System.Buffers.Binary;
using System.Threading.Channels;
using DesktopReplay.Core.Interop;

namespace DesktopReplay.Core.Media;

/// <summary>
/// Disk-backed ring buffer of encoded packets. Packets from all streams are appended to
/// segment files of ~2 seconds; segments older than the replay window are deleted.
/// Also fans packets out to live listeners (manual recordings).
/// </summary>
public sealed class PacketStore : IDisposable
{
    private const int HeaderSize = 4 + 8 + 8 + 8 + 1 + 4; // stream, pts, dts, duration, key, size
    private const long SegmentUs = 2_000_000;

    private sealed class Segment
    {
        public required string Path;
        public long FirstUs = long.MaxValue;
        public long LastUs = long.MinValue;
        public long Length;
        public FileStream? Writer;
    }

    private readonly string _dir;
    private readonly Channel<EncodedPacket> _queue = Channel.CreateUnbounded<EncodedPacket>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Segment> _segments = new();
    private readonly object _sync = new();
    private readonly Thread _thread;
    private readonly List<Action<EncodedPacket>> _listeners = new();
    private Segment? _current;
    private long _segmentStartUs;
    private int _segmentCounter;
    private int _pins;
    private long _keepUs;
    private volatile bool _bufferEnabled;

    public PacketStore(string directory, TimeSpan window, bool bufferEnabled)
    {
        _dir = directory;
        _keepUs = (long)window.TotalMicroseconds + 6_000_000;
        _bufferEnabled = bufferEnabled;
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.EnumerateFiles(_dir, "seg_*.bin"))
            try { File.Delete(f); } catch { }
        _thread = new Thread(WriterLoop) { Name = "PacketStore", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public long LatestUs { get; private set; }

    public bool BufferEnabled
    {
        get => _bufferEnabled;
        set
        {
            _bufferEnabled = value;
            if (!value) lock (_sync) DropAllSegments();
        }
    }

    public void SetWindow(TimeSpan window) => Interlocked.Exchange(ref _keepUs, (long)window.TotalMicroseconds + 6_000_000);

    public void Add(EncodedPacket p) => _queue.Writer.TryWrite(p);

    public void AddListener(Action<EncodedPacket> l) { lock (_listeners) _listeners.Add(l); }
    public void RemoveListener(Action<EncodedPacket> l) { lock (_listeners) _listeners.Remove(l); }

    /// <summary>Total bytes currently held in the buffer.</summary>
    public long BufferedBytes { get { lock (_sync) return _segments.Sum(s => s.Length); } }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_sync)
            {
                if (_segments.Count == 0) return TimeSpan.Zero;
                return TimeSpan.FromMicroseconds(Math.Max(0, _segments[^1].LastUs - _segments[0].FirstUs));
            }
        }
    }

    private void WriterLoop()
    {
        var reader = _queue.Reader;
        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var p))
                {
                    if (p.Pts > LatestUs) LatestUs = p.Pts;
                    Action<EncodedPacket>[] ls;
                    lock (_listeners) ls = _listeners.ToArray();
                    foreach (var l in ls)
                    {
                        try { l(p); } catch (Exception ex) { Log.Error("Packet listener failed", ex); }
                    }
                    if (_bufferEnabled) Append(p);
                }
                lock (_sync) _current?.Writer?.Flush();
            }
        }
        catch (Exception ex)
        {
            Log.Error("PacketStore writer crashed", ex);
        }
    }

    private void Append(EncodedPacket p)
    {
        lock (_sync)
        {
            long now = Clock.NowUs();
            if (_current == null || now - _segmentStartUs >= SegmentUs)
            {
                CloseCurrent();
                _current = new Segment { Path = Path.Combine(_dir, $"seg_{_segmentCounter++:D6}.bin") };
                _current.Writer = new FileStream(_current.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                _segments.Add(_current);
                _segmentStartUs = now;
                Trim();
            }

            Span<byte> h = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteInt32LittleEndian(h, p.Stream);
            BinaryPrimitives.WriteInt64LittleEndian(h[4..], p.Pts);
            BinaryPrimitives.WriteInt64LittleEndian(h[12..], p.Dts);
            BinaryPrimitives.WriteInt64LittleEndian(h[20..], p.Duration);
            h[28] = p.Key ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(h[29..], p.Data.Length);
            try
            {
                _current.Writer!.Write(h);
                _current.Writer.Write(p.Data);
                _current.Length += HeaderSize + p.Data.Length;
                if (p.Pts < _current.FirstUs) _current.FirstUs = p.Pts;
                if (p.Pts > _current.LastUs) _current.LastUs = p.Pts;
            }
            catch (IOException ex)
            {
                Log.Error("Buffer write failed (disk full?)", ex);
                CloseCurrent();
                _current = null;
            }
        }
    }

    private void CloseCurrent()
    {
        if (_current?.Writer != null)
        {
            try { _current.Writer.Dispose(); } catch { }
            _current.Writer = null;
        }
    }

    private void Trim()
    {
        if (_pins > 0) return;
        long limit = LatestUs - Interlocked.Read(ref _keepUs);
        while (_segments.Count > 1 && _segments[0].LastUs < limit)
        {
            var s = _segments[0];
            _segments.RemoveAt(0);
            try { s.Writer?.Dispose(); File.Delete(s.Path); } catch { }
        }
    }

    private void DropAllSegments()
    {
        if (_pins > 0) return;
        CloseCurrent();
        _current = null;
        foreach (var s in _segments)
            try { s.Writer?.Dispose(); File.Delete(s.Path); } catch { }
        _segments.Clear();
    }

    /// <summary>
    /// Reads all buffered packets with Pts >= fromUs. Segments are pinned while reading,
    /// so the writer keeps appending but nothing gets deleted.
    /// </summary>
    public IEnumerable<EncodedPacket> ReadSince(long fromUs, bool headersOnly = false)
    {
        List<(string Path, long Length)> snapshot;
        lock (_sync)
        {
            _current?.Writer?.Flush();
            snapshot = _segments.Where(s => s.LastUs >= fromUs - SegmentUs).Select(s => (s.Path, s.Length)).ToList();
            _pins++;
        }

        try
        {
            var header = new byte[HeaderSize];
            foreach (var (path, length) in snapshot)
            {
                FileStream fs;
                try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
                catch (IOException) { continue; }
                using (fs)
                {
                    long pos = 0;
                    while (pos + HeaderSize <= length)
                    {
                        fs.ReadExactly(header);
                        var p = new EncodedPacket
                        {
                            Stream = BinaryPrimitives.ReadInt32LittleEndian(header),
                            Pts = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(4)),
                            Dts = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12)),
                            Duration = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20)),
                            Key = header[28] != 0,
                        };
                        int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(29));
                        pos += HeaderSize + size;
                        if (pos > length) break;
                        if (headersOnly || p.Pts < fromUs)
                        {
                            fs.Seek(size, SeekOrigin.Current);
                            if (headersOnly && p.Pts >= fromUs) yield return p;
                            continue;
                        }
                        p.Data = new byte[size];
                        fs.ReadExactly(p.Data);
                        yield return p;
                    }
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _pins--;
                if (!_bufferEnabled) DropAllSegments();
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _thread.Join(3000);
        lock (_sync)
        {
            _pins = 0;
            DropAllSegments();
        }
    }
}
