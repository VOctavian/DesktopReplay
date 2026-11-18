using System.Media;

namespace DesktopReplay.Services;

/// <summary>Short synthesized notification sounds (no external files).</summary>
public static class Sounds
{
    private static readonly Lazy<byte[]> SavedWav = new(() => Tones((880, 70), (1320, 110)));
    private static readonly Lazy<byte[]> StartWav = new(() => Tones((660, 90), (990, 90)));
    private static readonly Lazy<byte[]> StopWav = new(() => Tones((990, 90), (660, 120)));
    private static readonly Lazy<byte[]> ShotWav = new(() => Tones((1500, 40), (2000, 50)));
    private static readonly Lazy<byte[]> ErrorWav = new(() => Tones((330, 140), (247, 200)));

    public static void Saved() => Play(SavedWav.Value);
    public static void Start() => Play(StartWav.Value);
    public static void Stop() => Play(StopWav.Value);
    public static void Shot() => Play(ShotWav.Value);
    public static void Error() => Play(ErrorWav.Value);

    private static void Play(byte[] wav)
    {
        Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(wav);
                using var p = new SoundPlayer(ms);
                p.PlaySync();
            }
            catch { }
        });
    }

    private static byte[] Tones(params (double Freq, int Ms)[] tones)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (f, ms) in tones)
        {
            int n = rate * ms / 1000;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Min(1, (n - i) / (rate * 0.03));
                double v = Math.Sin(2 * Math.PI * f * t) * 0.6 + Math.Sin(4 * Math.PI * f * t) * 0.1;
                samples.Add((short)(v * env * 0.35 * short.MaxValue));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
