using DesktopReplay.Core.Interop;
using DesktopReplay.Core.Media;
using DesktopReplay.Core.Settings;

namespace DesktopReplay.Core.Audio;

/// <summary>
/// Captures and mixes the five audio tracks:
/// 1 = everything mixed, 2 = active application (game), 3 = microphone,
/// 4 = desktop sounds (all other apps except the game and Discord), 5 = Discord.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public static readonly string[] TrackTitlesRu = { "Всё вместе", "Игра / активное приложение", "Микрофон", "Звуки системы", "Discord" };
    public static readonly string[] TrackTitlesEn = { "Mix", "Game / active app", "Microphone", "Desktop sounds", "Discord" };

    private static readonly string[] DiscordExe = { "discord.exe", "discordptb.exe", "discordcanary.exe" };
    private static readonly string[] NeverCapture = { "audiodg.exe", "explorer.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe", "textinputhost.exe" };

    private const int Chunk = 480; // 10 ms
    private readonly long _t0Us;
    private readonly Func<uint> _activePid;
    private readonly AacEncoder[] _encoders = new AacEncoder[5];
    private volatile AudioSettings _settings;
    private readonly object _srcLock = new();

    private CaptureSource? _game; private uint _gamePid;
    private CaptureSource? _discord; private uint _discordPid;
    private CaptureSource? _mic;
    private NoiseFilter? _noise;
    private readonly Dictionary<uint, CaptureSource> _system = new();
    private readonly HashSet<uint> _failedPids = new();
    private long _nextMicRetryUs;

    private readonly Thread _mixThread;
    private readonly Thread _controlThread;
    private volatile bool _running = true;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private float _micGain = 1;

    public IReadOnlyList<StreamDescriptor> Streams => _encoders.Select(e => e.Stream).ToArray();

    /// <summary>Peak levels (0..1) of the last 10 ms per track, for UI meters.</summary>
    public float[] Levels { get; } = new float[5];

    public AudioEngine(long t0Us, int firstStreamIndex, AudioSettings settings, string language, Func<uint> activePid, Action<EncodedPacket> sink)
    {
        _t0Us = t0Us;
        _settings = settings;
        _activePid = activePid;
        var titles = language == "en" ? TrackTitlesEn : TrackTitlesRu;
        for (int i = 0; i < 5; i++)
            _encoders[i] = new AacEncoder(firstStreamIndex + i, titles[i], i + 1, settings.AudioBitrateKbps, sink);

        if (settings.NoiseSuppression)
        {
            try { _noise = new NoiseFilter(); }
            catch (Exception ex) { Log.Warn($"Noise filter unavailable: {ex.Message}"); }
        }

        _controlThread = new Thread(ControlLoop) { Name = "Audio control", IsBackground = true };
        _controlThread.Start();
        _mixThread = new Thread(MixLoop) { Name = "Audio mixer", IsBackground = true, Priority = ThreadPriority.Highest };
        _mixThread.Start();
    }

    public void UpdateSettings(AudioSettings s) => _settings = s;

    // ---------- Source management ----------

    private void ControlLoop()
    {
        while (_running)
        {
            try { RefreshSources(); }
            catch (Exception ex) { Log.Warn($"Audio refresh failed: {ex.Message}"); }
            for (int i = 0; i < 15 && _running; i++) Thread.Sleep(100);
        }
    }

    private void RefreshSources()
    {
        var procs = Native.SnapshotProcesses();
        _failedPids.RemoveWhere(p => !procs.ContainsKey(p));

        // Microphone (re-open if the device disappeared).
        if ((_mic == null || _mic.Faulted) && Clock.NowUs() >= _nextMicRetryUs)
        {
            var old = _mic;
            lock (_srcLock) _mic = null;
            old?.Dispose();
            try
            {
                var noise = _noise;
                var mic = CaptureSource.OpenDevice(_settings.MicDeviceId, noise != null ? noise.Process : null);
                lock (_srcLock) _mic = mic;
            }
            catch (Exception ex)
            {
                Log.Warn($"Microphone unavailable: {ex.Message}");
                _nextMicRetryUs = Clock.NowUs() + 5_000_000;
            }
        }

        // Track 2: active application / game.
        uint active = _activePid();
        if (active != 0 && (!procs.TryGetValue(active, out var ap) || NeverCapture.Contains(ap.Exe.ToLowerInvariant()) || active == _ownPid))
            active = 0;
        if (active != _gamePid || (_game?.Faulted ?? false))
        {
            var old = _game;
            lock (_srcLock) { _game = null; _gamePid = 0; }
            old?.Dispose();
            if (active != 0)
            {
                try
                {
                    var src = CaptureSource.OpenProcess(active, "app");
                    lock (_srcLock) { _game = src; _gamePid = active; }
                    Log.Info($"Track 2 → {procs[active].Exe} ({active})");
                }
                catch (Exception ex) { Log.Warn($"Cannot capture app {active}: {ex.Message}"); }
            }
        }

        // Track 5: Discord (root process of the Discord tree).
        uint discord = 0;
        foreach (var (pid, e) in procs)
        {
            if (!DiscordExe.Contains(e.Exe.ToLowerInvariant())) continue;
            if (procs.TryGetValue(e.Parent, out var parent) && DiscordExe.Contains(parent.Exe.ToLowerInvariant())) continue;
            discord = pid;
            break;
        }
        if (discord != _discordPid || (_discord?.Faulted ?? false))
        {
            var old = _discord;
            lock (_srcLock) { _discord = null; _discordPid = 0; }
            old?.Dispose();
            if (discord != 0)
            {
                try
                {
                    var src = CaptureSource.OpenProcess(discord, "discord");
                    lock (_srcLock) { _discord = src; _discordPid = discord; }
                    Log.Info($"Track 5 → Discord ({discord})");
                }
                catch (Exception ex) { Log.Warn($"Cannot capture Discord: {ex.Message}"); }
            }
        }

        // Track 4: every other process with an audio session.
        var sessions = CoreAudio.GetSessionProcessIds();
        var candidates = new HashSet<uint>();
        foreach (var pid in sessions)
        {
            if (pid == _ownPid || !procs.TryGetValue(pid, out var e)) continue;
            var exe = e.Exe.ToLowerInvariant();
            if (NeverCapture.Contains(exe) || DiscordExe.Contains(exe)) continue;
            if (_gamePid != 0 && Native.IsInTree(pid, _gamePid, procs)) continue;
            if (_discordPid != 0 && Native.IsInTree(pid, _discordPid, procs)) continue;
            // Loopback captures the whole process tree: skip a process whose tree contains the game or
            // Discord (e.g. a launcher that started the game) so their audio does not leak into this track.
            if (_gamePid != 0 && Native.IsInTree(_gamePid, pid, procs)) continue;
            if (_discordPid != 0 && Native.IsInTree(_discordPid, pid, procs)) continue;
            candidates.Add(pid);
        }
        // Skip a process if one of its ancestors is captured too (the ancestor's tree already includes it).
        var wanted = candidates.Where(pid => !candidates.Any(other => other != pid && Native.IsInTree(pid, other, procs))).ToHashSet();

        List<CaptureSource> toDispose = new();
        lock (_srcLock)
        {
            foreach (var pid in _system.Keys.ToList())
            {
                if (!wanted.Contains(pid) || _system[pid].Faulted)
                {
                    toDispose.Add(_system[pid]);
                    _system.Remove(pid);
                }
            }
        }
        foreach (var s in toDispose) s.Dispose();

        foreach (var pid in wanted)
        {
            bool have;
            lock (_srcLock) have = _system.ContainsKey(pid);
            if (have || _failedPids.Contains(pid)) continue;
            try
            {
                var src = CaptureSource.OpenProcess(pid, "sys");
                lock (_srcLock) _system[pid] = src;
                Log.Info($"Track 4 + {procs[pid].Exe} ({pid})");
            }
            catch (Exception ex)
            {
                _failedPids.Add(pid);
                Log.Warn($"Cannot capture {procs[pid].Exe}: {ex.Message}");
            }
        }
    }

    // ---------- Mixer ----------

    private void MixLoop()
    {
        using var timer = new PreciseTimer();
        const int samples = Chunk * 2;
        var game = new float[samples];
        var mic = new float[samples];
        var sys = new float[samples];
        var tmp = new float[samples];
        var discord = new float[samples];
        var mix = new float[samples];
        long produced = 0;
        // Audio is produced 100 ms behind real time, matching the capture latency of the sources.
        long latencyUs = CaptureSource.TargetLatencySamples / 2 * 1_000_000L / AudioFormat.Rate;

        while (_running)
        {
            timer.WaitUntilUs(Clock.NowUs() + 10_000);
            long target = (Clock.NowUs() - _t0Us - latencyUs) * AudioFormat.Rate / 1_000_000;
            // After a long stall don't try to catch up more than one second at once.
            if (target - produced > AudioFormat.Rate) produced = target - AudioFormat.Rate;
            while (produced + Chunk <= target && _running)
            {
                var s = _settings;
                lock (_srcLock)
                {
                    PullOrSilence(_game, game);
                    PullOrSilence(_mic, mic);
                    PullOrSilence(_discord, discord);
                    Array.Clear(sys);
                    foreach (var src in _system.Values)
                    {
                        src.Pull(tmp);
                        for (int i = 0; i < samples; i++) sys[i] += tmp[i];
                    }
                }

                ApplyMicGate(mic, s);
                Scale(game, (float)s.GameVolume);
                Scale(mic, (float)s.MicVolume);
                Scale(sys, (float)s.SystemVolume);
                Scale(discord, (float)s.DiscordVolume);

                for (int i = 0; i < samples; i++)
                {
                    float v = 0;
                    if (s.GameInMix) v += game[i];
                    if (s.MicInMix) v += mic[i];
                    if (s.SystemInMix) v += sys[i];
                    if (s.DiscordInMix) v += discord[i];
                    mix[i] = Math.Clamp(v, -1f, 1f);
                }
                Clamp(game); Clamp(mic); Clamp(sys); Clamp(discord);

                try
                {
                    _encoders[0].Push(mix);
                    _encoders[1].Push(game);
                    _encoders[2].Push(mic);
                    _encoders[3].Push(sys);
                    _encoders[4].Push(discord);
                }
                catch (Exception ex)
                {
                    Log.Error("AAC encode failed", ex);
                }
                Levels[0] = Peak(mix); Levels[1] = Peak(game); Levels[2] = Peak(mic); Levels[3] = Peak(sys); Levels[4] = Peak(discord);
                produced += Chunk;
            }
        }
    }

    private static void PullOrSilence(CaptureSource? src, float[] dst)
    {
        if (src == null) Array.Clear(dst);
        else src.Pull(dst);
    }

    private void ApplyMicGate(float[] mic, AudioSettings s)
    {
        float target = 1;
        if (s.MicMode != MicMode.AlwaysOn)
        {
            int vk = KeyNames.ToVk(s.PushKey);
            bool down = vk != 0 && (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            target = s.MicMode == MicMode.PushToTalk ? (down ? 1 : 0) : (down ? 0 : 1);
        }
        // Short ramp to avoid clicks when the gate opens/closes.
        float start = _micGain;
        int frames = mic.Length / 2;
        for (int f = 0; f < frames; f++)
        {
            float g = start + (target - start) * (f + 1) / frames;
            mic[f * 2] *= g;
            mic[f * 2 + 1] *= g;
        }
        _micGain = target;
    }

    private static void Scale(float[] a, float g)
    {
        if (Math.Abs(g - 1f) < 0.0001f) return;
        for (int i = 0; i < a.Length; i++) a[i] *= g;
    }

    private static void Clamp(float[] a)
    {
        for (int i = 0; i < a.Length; i++) a[i] = Math.Clamp(a[i], -1f, 1f);
    }

    private static float Peak(float[] a)
    {
        float p = 0;
        for (int i = 0; i < a.Length; i++) p = Math.Max(p, Math.Abs(a[i]));
        return p;
    }

    public void Dispose()
    {
        _running = false;
        _mixThread.Join(2000);
        _controlThread.Join(3000);
        lock (_srcLock)
        {
            _game?.Dispose(); _mic?.Dispose(); _discord?.Dispose();
            foreach (var s in _system.Values) s.Dispose();
            _system.Clear();
            _game = _mic = _discord = null;
        }
        _noise?.Dispose();
        foreach (var e in _encoders) e.Dispose();
    }
}
