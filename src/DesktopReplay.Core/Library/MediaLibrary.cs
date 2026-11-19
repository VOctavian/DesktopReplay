using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace DesktopReplay.Core.Library;

public sealed class LibraryEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required DateTime Date { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsImage => MediaLibrary.IsImage(Path);
}

/// <summary>Finds every video file in the output folders (including files added from outside) and caches their metadata and thumbnails.</summary>
public static partial class MediaLibrary
{
    public static readonly string[] Extensions = { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".ts" };
    public const string TempMarker = ".dr-edit";

    private static readonly string CacheFile = Path.Combine(AppPaths.LocalData, "library.json");
    public static readonly string ThumbDir = Path.Combine(AppPaths.LocalData, "thumbs");
    private static readonly object CacheLock = new();
    private static Dictionary<string, CacheItem>? _cache;
    private static readonly SemaphoreSlim ThumbGate = new(2);

    private sealed record CacheItem(long Size, long Ticks, double Duration, int Width, int Height);

    [GeneratedRegex(@"^(?<game>.+?)_(?<d>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})")]
    private static partial Regex NameRegex();

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
        !path.Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Distinct folders, dropping ones nested inside another listed folder.</summary>
    public static List<string> Roots(IEnumerable<string> folders)
    {
        var list = folders.Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => System.IO.Path.GetFullPath(f).TrimEnd('\\') + "\\")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Where(f => !list.Any(o => o != f && f.StartsWith(o, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Lists videos, or with <paramref name="images"/> screenshots/pictures, under the folders.</summary>
    public static List<LibraryEntry> Scan(IEnumerable<string> folders, CancellationToken ct = default, bool images = false)
    {
        LoadCache();
        var result = new List<LibraryEntry>();
        bool dirty = false;
        foreach (var root in Roots(folders))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
            catch { continue; }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (images ? !IsImage(file) : !IsVideo(file)) continue;
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length == 0) continue;
                    var e = MakeEntry(root, fi);
                    lock (CacheLock)
                    {
                        if (_cache!.TryGetValue(file, out var c) && c.Size == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks)
                        {
                            e.Duration = c.Duration; e.Width = c.Width; e.Height = c.Height;
                            result.Add(e);
                            continue;
                        }
                    }
                    try
                    {
                        if (images)
                        {
                            (e.Width, e.Height) = ImageSize(file);
                        }
                        else
                        {
                            var info = MediaProbe.Probe(file, full: false);
                            e.Duration = info.Duration; e.Width = info.Width; e.Height = info.Height;
                        }
                    }
                    catch (Exception ex) { Log.Warn($"Gallery: cannot probe {file}: {ex.Message}"); }
                    lock (CacheLock) _cache![file] = new CacheItem(fi.Length, fi.LastWriteTimeUtc.Ticks, e.Duration, e.Width, e.Height);
                    dirty = true;
                    result.Add(e);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (dirty) SaveCache();
        return result;
    }

    /// <summary>Entry for a single file (e.g. a replay just saved), probed on the spot.</summary>
    public static LibraryEntry EntryFor(string path)
    {
        var fi = new FileInfo(path);
        var e = MakeEntry(fi.DirectoryName! + "\\", fi);
        try
        {
            var info = MediaProbe.Probe(path, full: false);
            e.Duration = info.Duration; e.Width = info.Width; e.Height = info.Height;
        }
        catch { }
        return e;
    }

    private static LibraryEntry MakeEntry(string root, FileInfo fi)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fi.Name);
        var m = NameRegex().Match(name);
        var rel = System.IO.Path.GetRelativePath(root, fi.DirectoryName!);
        string group = rel != "." ? rel.Split('\\')[0] : m.Success ? m.Groups["game"].Value : "";
        var date = m.Success && DateTime.TryParseExact(m.Groups["d"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : fi.LastWriteTime;
        return new LibraryEntry
        {
            Path = fi.FullName, Name = fi.Name, Group = group, Date = date,
            Size = fi.Length, Modified = fi.LastWriteTimeUtc,
        };
    }

    /// <summary>Pixel size from the image header (no full decode).</summary>
    public static (int W, int H) ImageSize(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var f = dec.Frames[0];
        return (f.PixelWidth, f.PixelHeight);
    }

    // ---------- thumbnails ----------

    public static async Task<BitmapSource?> GetThumbnailAsync(LibraryEntry e, int width = 400, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{e.Path.ToLowerInvariant()}|{e.Size}|{e.Modified.Ticks}|{width}")));
        var file = System.IO.Path.Combine(ThumbDir, key + ".jpg");
        if (File.Exists(file)) return LoadImage(file);
        await ThumbGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                if (File.Exists(file)) return LoadImage(file);
                BitmapSource? bmp;
                if (e.IsImage)
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    b.DecodePixelWidth = width;
                    b.UriSource = new Uri(e.Path);
                    b.EndInit();
                    b.Freeze();
                    bmp = b;
                }
                else
                {
                    using var g = new FrameGrabber(e.Path);
                    var dur = g.Duration > 0 ? g.Duration : e.Duration;
                    bmp = g.Grab(dur > 2 ? dur * 0.4 : 0, width) ?? g.Grab(0, width);
                }
                if (bmp == null) return null;
                Directory.CreateDirectory(ThumbDir);
                var enc = new JpegBitmapEncoder { QualityLevel = 85 };
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(file)) enc.Save(fs);
                return bmp;
            }, ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn($"Gallery: thumbnail failed for {e.Path}: {ex.Message}");
            return null;
        }
        finally { ThumbGate.Release(); }
    }

    private static BitmapSource? LoadImage(string file)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(file);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>Evenly spaced keyframes for the editor timeline.</summary>
    public static List<BitmapSource?> Filmstrip(string path, int count, int width)
    {
        var list = new List<BitmapSource?>();
        using var g = new FrameGrabber(path);
        double step = g.Duration / count;
        for (int i = 0; i < count; i++) list.Add(g.Grab(step * (i + 0.5), width));
        return list;
    }

    // ---------- metadata cache ----------

    private static void LoadCache()
    {
        lock (CacheLock)
        {
            if (_cache != null) return;
            try
            {
                if (File.Exists(CacheFile))
                    _cache = JsonSerializer.Deserialize<Dictionary<string, CacheItem>>(File.ReadAllText(CacheFile));
            }
            catch { }
            _cache = new Dictionary<string, CacheItem>(_cache ?? new(), StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveCache()
    {
        lock (CacheLock)
        {
            try
            {
                foreach (var k in _cache!.Keys.Where(k => !File.Exists(k)).ToList()) _cache.Remove(k);
                Directory.CreateDirectory(AppPaths.LocalData);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache));
            }
            catch (Exception ex) { Log.Warn($"Gallery cache save failed: {ex.Message}"); }
        }
    }
}
