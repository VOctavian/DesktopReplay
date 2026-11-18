using System.Text;
using DesktopReplay.Core.Settings;

namespace DesktopReplay.Core.Engine;

public static class FileNaming
{
    /// <summary>Builds "Folder\Game\Game_2026-11-18_21-30-05[_mon2].ext", unique on disk.</summary>
    public static string Build(AppSettings s, string baseFolder, string appName, DateTime time, string ext, int? monitorNumber)
    {
        var name = Sanitize(appName);
        var folder = baseFolder;
        if (s.SubfolderPerGame) folder = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        var baseName = $"{name}_{time:yyyy-MM-dd_HH-mm-ss}" + (monitorNumber.HasValue ? $"_mon{monitorNumber}" : "");
        var path = Path.Combine(folder, baseName + ext);
        // A recording is written as .mkv and later remuxed to .mp4, so both names must be free.
        bool Taken(string p) => File.Exists(p) || File.Exists(Path.ChangeExtension(p, ".mp4")) || File.Exists(Path.ChangeExtension(p, ".mkv"));
        for (int i = 2; Taken(path); i++) path = Path.Combine(folder, $"{baseName}_{i}{ext}");
        return path;
    }

    public static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in s.Trim())
        {
            if (invalid.Contains(c) || c == '_' && sb.Length > 0 && sb[^1] == '_') continue;
            sb.Append(c == ' ' ? ' ' : c);
        }
        var r = sb.ToString().Trim().TrimEnd('.');
        if (r.Length > 50) r = r[..50].Trim();
        return r.Length == 0 ? "Desktop" : r;
    }
}

public sealed record StorageUsage(long UsedBytes, long LimitBytes, long FreeDiskBytes)
{
    public double Percent => LimitBytes > 0 ? 100.0 * UsedBytes / LimitBytes : 0;
    public long RemainingBytes => LimitBytes > 0 ? Math.Max(0, LimitBytes - UsedBytes) : FreeDiskBytes;
}

public static class Storage
{
    public static StorageUsage GetUsage(AppSettings s)
    {
        // Replays, recordings and screenshots may live in different folders; count each tree once.
        var folders = new[] { s.OutputFolder, s.EffectiveRecordingsFolder, s.EffectiveScreenshotsFolder }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)); } catch { return f; } })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        folders = folders.Where(f => !folders.Any(o => o != f && f.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        long used = 0;
        foreach (var folder in folders)
        {
            try
            {
                if (Directory.Exists(folder))
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                        used += f.Length;
            }
            catch { }
        }
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(s.OutputFolder))!).AvailableFreeSpace; } catch { }
        return new StorageUsage(used, (long)(s.MaxFolderSizeGb * 1024 * 1024 * 1024), free);
    }

    /// <summary>Approximate bitrate for given settings (video + 5 audio tracks), bits per second.</summary>
    public static long VideoBitrate(AppSettings s, int width, int height)
    {
        if (s.Quality == QualityPreset.Custom) return Math.Clamp(s.CustomBitrateMbps, 1, 300) * 1_000_000L;
        double baseMbps = s.Quality switch
        {
            QualityPreset.Low => 6,
            QualityPreset.Medium => 12,
            QualityPreset.Ultra => 35,
            _ => 20,
        };
        double pixels = (double)width * height / (1920.0 * 1080.0);
        double fps = Math.Pow(s.Fps / 30.0, 0.8);
        double codec = s.Codec == VideoCodec.H264 ? 1.0 : 0.7;
        return (long)(Math.Clamp(baseMbps * pixels * fps * codec, 2, 200) * 1_000_000);
    }

    public static long TotalBitrate(AppSettings s, IEnumerable<(int W, int H)> outputs) =>
        outputs.Sum(o => VideoBitrate(s, o.W, o.H)) + 5L * s.Audio.AudioBitrateKbps * 1000;

    public static double MegabytesPerMinute(long bitsPerSecond) => bitsPerSecond * 60.0 / 8 / 1024 / 1024;
}
