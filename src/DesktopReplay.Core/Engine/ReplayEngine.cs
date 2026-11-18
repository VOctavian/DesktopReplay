using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopReplay.Core.Audio;
using DesktopReplay.Core.Interop;
using DesktopReplay.Core.Media;
using DesktopReplay.Core.Settings;

namespace DesktopReplay.Core.Engine;

public enum NoticeKind { ReplaySaved, RecordingStarted, RecordingStopped, ScreenshotSaved, StorageWarning, ReplayOn, ReplayOff, Error }

public sealed record Notice(NoticeKind Kind, IReadOnlyList<string> Paths, string? Detail = null, StorageUsage? Storage = null, double MinutesLeft = 0);

/// <summary>
/// Orchestrates capture sessions: replay buffer, manual recording and screenshots.
/// All public operations are serialized.
/// </summary>
public sealed class ReplayEngine : IDisposable
{
    private sealed class Session : IDisposable
    {
        public long T0;
        public required PacketStore Store;
        public List<VideoPipeline> Video = new();
        public AudioEngine? Audio;
        public long BitrateBps;

        public void Dispose()
        {
            foreach (var v in Video) { try { v.Dispose(); } catch (Exception ex) { Log.Error("Video dispose", ex); } }
            try { Audio?.Dispose(); } catch (Exception ex) { Log.Error("Audio dispose", ex); }
            Store.Dispose();
        }
    }

    private sealed class Recording
    {
        public required List<FileMuxer> Muxers;
        public required Action<EncodedPacket> Listener;
        public DateTime Started;
        public long LastStorageCheck;
    }

    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly GameDetector _game = new();
    private readonly ExclusionTracker _exclusions;
    private Session? _session;
    private Recording? _recording;
    private AppSettings _settings;
    private readonly Timer _watchdog;
    private bool _disposed;

    public event Action? StateChanged;
    public event Action<Notice>? Notify;

    public ReplayEngine(AppSettings settings)
    {
        _settings = settings.Clone();
        _exclusions = new ExclusionTracker(_settings.ExcludedProcesses);
        _watchdog = new Timer(_ => Watchdog(), null, 5000, 5000);
    }

    public AppSettings Settings => _settings;
    public bool ReplayActive => _session != null && _settings.ReplayEnabled;
    public bool IsRecording => _recording != null;
    public TimeSpan RecordingElapsed => _recording != null ? DateTime.Now - _recording.Started : TimeSpan.Zero;
    public TimeSpan BufferedDuration => _session?.Store.BufferEnabled == true ? _session.Store.BufferedDuration : TimeSpan.Zero;
    public long BufferedBytes => _session?.Store.BufferedBytes ?? 0;
    public string? LastError { get; private set; }
    public string ActiveAppName => _game.ActiveName;
    public float[]? AudioLevels => _session?.Audio?.Levels;
    public string VideoSummary => _session == null ? "" : string.Join(", ", _session.Video.Select(v => $"{v.OutputWidth}×{v.OutputHeight} {v.EncoderName}"));

    // ---------- Lifecycle ----------

    /// <summary>Starts the replay buffer if enabled in settings.</summary>
    public Task StartAsync() => Locked(async () => { if (_settings.ReplayEnabled) await EnsureSessionAsync(); });

    public Task SetReplayEnabledAsync(bool enabled) => Locked(async () =>
    {
        _settings.ReplayEnabled = enabled;
        if (enabled)
        {
            await EnsureSessionAsync();
            if (_session != null) _session.Store.BufferEnabled = true;
        }
        else if (_session != null)
        {
            if (_recording == null) StopSession();
            else _session.Store.BufferEnabled = false;
        }
        Notify?.Invoke(new Notice(enabled ? NoticeKind.ReplayOn : NoticeKind.ReplayOff, Array.Empty<string>()));
    });

    public Task ApplySettingsAsync(AppSettings s) => Locked(async () =>
    {
        bool restart = s.CaptureDiffers(_settings);
        var old = _settings;
        _settings = s.Clone();
        _exclusions.SetProcesses(_settings.ExcludedProcesses);
        if (_session != null)
        {
            _session.Audio?.UpdateSettings(_settings.Audio);
            _session.Store.SetWindow(TimeSpan.FromSeconds(_settings.ReplaySeconds));
        }
        if (restart && _session != null && _recording == null)
        {
            Log.Info("Capture settings changed, restarting session");
            StopSession();
            if (_settings.ReplayEnabled) await EnsureSessionAsync();
        }
        else if (old.ReplayEnabled != _settings.ReplayEnabled)
        {
            if (_settings.ReplayEnabled) await EnsureSessionAsync();
            else if (_recording == null) StopSession();
        }
    });

    private async Task EnsureSessionAsync()
    {
        if (_session != null) return;
        LastError = null;
        try
        {
            _session = await Task.Run(CreateSession);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Failed to start capture", ex);
            Notify?.Invoke(new Notice(NoticeKind.Error, Array.Empty<string>(), ex.Message));
        }
        StateChanged?.Invoke();
    }

    private Session CreateSession()
    {
        FF.Init();
        var s = _settings;
        var monitors = Monitors.Resolve(s.Monitors);
        if (monitors.Count == 0) throw new InvalidOperationException("No monitors found");
        long t0 = Clock.NowUs();
        var store = new PacketStore(s.BufferFolder, TimeSpan.FromSeconds(s.ReplaySeconds), s.ReplayEnabled);
        var session = new Session { T0 = t0, Store = store };
        try
        {
            int index = 0;
            foreach (var m in monitors)
            {
                int outH = s.ResolutionHeight <= 0 || s.ResolutionHeight >= m.Height ? m.Height : s.ResolutionHeight;
                int outW = (int)Math.Round(outH * (double)m.Width / m.Height) & ~1;
                var cfg = new VideoConfig(s.Fps, s.ResolutionHeight, s.Codec, Storage.VideoBitrate(s, outW, outH), s.CaptureCursor, s.HdrToSdr);
                var vp = new VideoPipeline(m, cfg, index++, t0, store.Add, () => _exclusions.Rects);
                vp.Failed += OnPipelineFailed;
                session.Video.Add(vp);
                vp.Start();
                session.BitrateBps += cfg.BitrateBps;
            }
            session.Audio = new AudioEngine(t0, index, s.Audio, s.Language, () => _game.ActivePid, store.Add);
            session.BitrateBps += 5L * s.Audio.AudioBitrateKbps * 1000;
            Log.Info($"Session started: {monitors.Count} monitor(s), replay {s.ReplaySeconds}s, buffer {s.BufferFolder}");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void StopSession()
    {
        var s = _session;
        _session = null;
        s?.Dispose();
        StateChanged?.Invoke();
        Log.Info("Session stopped");
    }

    private volatile bool _restartPending;

    private void OnPipelineFailed(string reason)
    {
        Log.Warn($"Pipeline failed: {reason}");
        _restartPending = true;
    }

    private void Watchdog()
    {
        if (_disposed) return;
        if (_restartPending)
        {
            _restartPending = false;
            _ = Locked(async () =>
            {
                if (_session == null) return;
                if (_recording != null) await StopRecordingCore();
                StopSession();
                await Task.Delay(1000);
                await EnsureSessionAsync();
            });
        }
        if (_recording != null && Environment.TickCount64 - _recording.LastStorageCheck > 60_000)
        {
            _recording.LastStorageCheck = Environment.TickCount64;
            CheckStorage();
        }
    }

    // ---------- Replay ----------

    public Task<IReadOnlyList<string>> SaveReplayAsync() => Locked<IReadOnlyList<string>>(async () =>
    {
        var session = _session;
        if (session == null || !session.Store.BufferEnabled) return Array.Empty<string>();
        var name = _game.ActiveName;
        var time = DateTime.Now;
        var result = await Task.Run(() => SaveReplayCore(session, name, time));
        if (result.Count > 0)
        {
            Notify?.Invoke(new Notice(NoticeKind.ReplaySaved, result));
            CheckStorage();
        }
        return result;
    });

    private List<string> SaveReplayCore(Session session, string appName, DateTime time)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long end = Clock.NowUs() - session.T0;
        long cutoff = end - _settings.ReplaySeconds * 1_000_000L;

        // Pass 1: locate the keyframe to start each monitor's clip at.
        var starts = new Dictionary<int, long>();
        var firstKeyAfter = new Dictionary<int, long>();
        foreach (var p in session.Store.ReadSince(cutoff - 5_000_000, headersOnly: true))
        {
            if (!p.Key || p.Pts > end) continue;
            if (session.Video.All(v => v.Stream!.Index != p.Stream)) continue;
            if (p.Pts <= cutoff) starts[p.Stream] = Math.Max(starts.GetValueOrDefault(p.Stream, long.MinValue), p.Pts);
            else if (!firstKeyAfter.ContainsKey(p.Stream)) firstKeyAfter[p.Stream] = p.Pts;
        }
        foreach (var (k, v) in firstKeyAfter) starts.TryAdd(k, v);
        if (starts.Count == 0)
        {
            Log.Warn("Replay save: buffer is empty");
            return new List<string>();
        }

        var audio = session.Audio!.Streams;
        var outputs = new List<(FileMuxer Mux, long Start, string Final)>();
        bool multi = session.Video.Count > 1;
        try
        {
            foreach (var v in session.Video)
            {
                if (!starts.TryGetValue(v.Stream!.Index, out long start)) continue;
                var final = FileNaming.Build(_settings, appName, time, ".mp4", multi ? v.Monitor.Number : null);
                var mux = new FileMuxer(final + ".part", "mp4", v.Stream, audio, start, fastStart: true);
                outputs.Add((mux, start, final));
            }

            // Pass 2: copy packets.
            long from = outputs.Min(o => o.Start);
            foreach (var p in session.Store.ReadSince(from))
            {
                if (p.Pts > end) continue;
                foreach (var o in outputs)
                    if (p.Pts >= o.Start) o.Mux.Write(p);
            }
        }
        finally
        {
            foreach (var o in outputs) o.Mux.Dispose();
        }

        var paths = new List<string>();
        foreach (var o in outputs)
        {
            try
            {
                File.Move(o.Final + ".part", o.Final, true);
                paths.Add(o.Final);
            }
            catch (Exception ex) { Log.Error("Rename failed", ex); }
        }
        Log.Info($"Replay saved in {sw.ElapsedMilliseconds} ms: {string.Join(", ", paths)}");
        return paths;
    }

    // ---------- Recording ----------

    public Task ToggleRecordingAsync() => Locked(async () =>
    {
        if (_recording != null) await StopRecordingCore();
        else await StartRecordingCore();
    });

    private async Task StartRecordingCore()
    {
        await EnsureSessionAsync();
        var session = _session;
        if (session == null) return;
        if (!_settings.ReplayEnabled) session.Store.BufferEnabled = false;

        var name = _game.ActiveName;
        var time = DateTime.Now;
        bool multi = session.Video.Count > 1;
        var muxers = new List<FileMuxer>();
        var byStream = new Dictionary<int, FileMuxer>();
        try
        {
            foreach (var v in session.Video)
            {
                var path = FileNaming.Build(_settings, name, time, ".mkv", multi ? v.Monitor.Number : null);
                var mux = new FileMuxer(path, "matroska", v.Stream!, session.Audio!.Streams, -1, fastStart: false);
                muxers.Add(mux);
                byStream[v.Stream!.Index] = mux;
            }
        }
        catch (Exception ex)
        {
            foreach (var m in muxers) m.Dispose();
            Log.Error("Cannot start recording", ex);
            Notify?.Invoke(new Notice(NoticeKind.Error, Array.Empty<string>(), ex.Message));
            return;
        }

        var sync = new object();
        void Listener(EncodedPacket p)
        {
            lock (sync)
            {
                if (byStream.TryGetValue(p.Stream, out var m)) m.Write(p);
                else foreach (var mm in muxers) mm.Write(p); // audio goes to every file
            }
        }
        session.Store.AddListener(Listener);
        foreach (var v in session.Video) v.RequestKeyframe();
        _recording = new Recording { Muxers = muxers, Listener = Listener, Started = DateTime.Now, LastStorageCheck = Environment.TickCount64 };
        Log.Info($"Recording started: {string.Join(", ", muxers.Select(m => m.Path))}");
        Notify?.Invoke(new Notice(NoticeKind.RecordingStarted, muxers.Select(m => m.Path).ToList()));
        StateChanged?.Invoke();
        CheckStorage();
    }

    private async Task StopRecordingCore()
    {
        var rec = _recording;
        if (rec == null) return;
        _recording = null;
        _session?.Store.RemoveListener(rec.Listener);
        await Task.Delay(300); // let in-flight packets drain
        foreach (var m in rec.Muxers) m.Dispose();
        var keepMkv = _settings.KeepMkv;
        var results = await Task.Run(() =>
        {
            var list = new List<string>();
            foreach (var m in rec.Muxers)
            {
                if (!m.HasData) { try { File.Delete(m.Path); } catch { } continue; }
                var mp4 = Path.ChangeExtension(m.Path, ".mp4");
                try
                {
                    FileMuxer.Remux(m.Path, mp4);
                    if (!keepMkv) File.Delete(m.Path);
                    list.Add(mp4);
                }
                catch (Exception ex)
                {
                    Log.Error($"Remux failed, keeping {m.Path}", ex);
                    try { File.Delete(mp4); } catch { }
                    list.Add(m.Path);
                }
            }
            return list;
        });
        Log.Info($"Recording stopped: {string.Join(", ", results)}");
        Notify?.Invoke(new Notice(NoticeKind.RecordingStopped, results));
        if (!_settings.ReplayEnabled) StopSession();
        StateChanged?.Invoke();
        CheckStorage();
    }

    // ---------- Screenshots ----------

    public Task<IReadOnlyList<string>> ScreenshotAsync() => Locked<IReadOnlyList<string>>(async () =>
    {
        var name = _game.ActiveName;
        var time = DateTime.Now;
        List<Screenshot> shots = new();
        try
        {
            var pipelines = _session?.Video;
            List<VideoPipeline>? tempPipes = null;
            if (pipelines == null)
            {
                // Replay is off: spin up the capture briefly.
                tempPipes = await Task.Run(() =>
                {
                    FF.Init();
                    var list = new List<VideoPipeline>();
                    int i = 0;
                    foreach (var m in Monitors.Resolve(_settings.Monitors))
                    {
                        var vp = new VideoPipeline(m, new VideoConfig(_settings.Fps, 0, VideoCodec.H264, 5_000_000, _settings.CaptureCursor, false), i++, Clock.NowUs(), _ => { }, () => _exclusions.Rects);
                        vp.Start();
                        list.Add(vp);
                    }
                    return list;
                });
                pipelines = tempPipes;
            }
            try
            {
                foreach (var v in pipelines)
                {
                    var task = v.CaptureScreenshotAsync();
                    if (await Task.WhenAny(task, Task.Delay(3000)) == task) shots.Add(await task);
                    else Log.Warn($"Screenshot timeout on monitor {v.Monitor.Number}");
                }
            }
            finally
            {
                if (tempPipes != null) foreach (var p in tempPipes) p.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot failed", ex);
            Notify?.Invoke(new Notice(NoticeKind.Error, Array.Empty<string>(), ex.Message));
            return Array.Empty<string>();
        }

        var fmt = _settings.ScreenshotFormat;
        bool multi = shots.Count > 1;
        var paths = await Task.Run(() => shots.Select(s =>
        {
            var path = FileNaming.Build(_settings, name, time, fmt == ScreenshotFormat.Jpg ? ".jpg" : ".png", multi ? s.MonitorNumber : null);
            WriteImage(s, path, fmt);
            return path;
        }).ToList());
        if (paths.Count > 0) Notify?.Invoke(new Notice(NoticeKind.ScreenshotSaved, paths));
        return paths;
    });

    private static void WriteImage(Screenshot s, string path, ScreenshotFormat fmt)
    {
        var bmp = BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgra32, null, s.Bgra, s.Width * 4);
        BitmapEncoder enc = fmt == ScreenshotFormat.Jpg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------- Storage ----------

    public StorageUsage GetStorageUsage() => Storage.GetUsage(_settings);

    private void CheckStorage()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var u = Storage.GetUsage(_settings);
                if (u.LimitBytes <= 0 || u.Percent < _settings.WarnAtPercent) return;
                long bps = _session?.BitrateBps ?? 25_000_000;
                double minutes = u.RemainingBytes * 8.0 / bps / 60.0;
                Notify?.Invoke(new Notice(NoticeKind.StorageWarning, Array.Empty<string>(), null, u, minutes));
            }
            catch (Exception ex) { Log.Warn($"Storage check failed: {ex.Message}"); }
        });
    }

    // ---------- Helpers ----------

    private async Task Locked(Func<Task> action)
    {
        await _op.WaitAsync();
        try { await action(); }
        catch (Exception ex)
        {
            Log.Error("Engine operation failed", ex);
            Notify?.Invoke(new Notice(NoticeKind.Error, Array.Empty<string>(), ex.Message));
        }
        finally
        {
            _op.Release();
            StateChanged?.Invoke();
        }
    }

    private async Task<T> Locked<T>(Func<Task<T>> action)
    {
        await _op.WaitAsync();
        try { return await action(); }
        catch (Exception ex)
        {
            Log.Error("Engine operation failed", ex);
            Notify?.Invoke(new Notice(NoticeKind.Error, Array.Empty<string>(), ex.Message));
            return default!;
        }
        finally
        {
            _op.Release();
            StateChanged?.Invoke();
        }
    }

    public async Task ShutdownAsync()
    {
        await _op.WaitAsync();
        try
        {
            if (_recording != null) await StopRecordingCore();
            StopSession();
        }
        finally { _op.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Dispose();
        _game.Dispose();
        _exclusions.Dispose();
        _session?.Dispose();
        _session = null;
    }
}
