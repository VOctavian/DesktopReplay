using System.IO;
using System.Text.RegularExpressions;
using DesktopReplay.Core;

namespace DesktopReplay.Views;

/// <summary>
/// The "replay saved" / "recording saved" toast. One file: Open / Cut / Folder / Copy / Discord.
/// Several monitors: a row per monitor (open, cut, copy, Discord, delete it, keep only it) plus "Delete all".
/// Deleting goes to the recycle bin and never asks (a dialog would take focus from the game).
/// </summary>
public static class SavedToast
{
    private static readonly Regex MonSuffix = new(@"_mon(\d+)(?:_\d+)?$", RegexOptions.IgnoreCase);

    /// <summary>Monitor number from the "_monN" file name suffix, if any.</summary>
    public static int? MonitorOf(string path)
    {
        var m = MonSuffix.Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static void Show(string title, IReadOnlyList<string> paths, Action<string> cut, double seconds = 7)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            var p = paths[0];
            ToastWindow.Show(title, Path.GetFileName(p), ToastKind.Ok, seconds, new[]
            {
                new ToastAction(Loc.T("Open"), "", () => App.OpenPath(p)),
                new ToastAction(Loc.T("G.Cut"), "", () => cut(p)),
                new ToastAction(Loc.T("N.Folder"), "", () => FileActions.ShowInFolder(p)),
                Copy(new[] { p }, Loc.T("G.Copy")),
                Discord(p, Loc.T("D.Button")),
            });
            return;
        }

        var ordered = paths.OrderBy(p => MonitorOf(p) ?? int.MaxValue).ToList();
        var baseName = MonSuffix.Replace(Path.GetFileNameWithoutExtension(ordered[0]), "");
        var rows = ordered.Select((p, i) => new ToastRow(Loc.F("N.Monitor", MonitorOf(p) ?? i + 1), new[]
        {
            new ToastAction("", "", () => App.OpenPath(p), Tip: Loc.T("Open")),
            new ToastAction("", "", () => cut(p), Tip: Loc.T("G.Cut")),
            Copy(new[] { p }, ""),
            Discord(p, ""),
            new ToastAction(Loc.T("N.DeleteThis"), "", () => Delete(title, ordered, new[] { p }, cut), Danger: true, Close: false),
            new ToastAction(Loc.T("N.KeepOnly"), "", () => Delete(title, ordered, ordered.Where(x => x != p).ToList(), cut), Close: false),
        })).ToList();
        ToastWindow.Show(title, Loc.F("N.MonitorsBody", baseName, ordered.Count), ToastKind.Ok, Math.Max(seconds, 12), new[]
        {
            new ToastAction(Loc.T("N.Folder"), "", () => FileActions.ShowInFolder(ordered[0])),
            Copy(ordered, Loc.T("N.CopyAll")),
            new ToastAction(Loc.T("N.DeleteAll"), "", () => Delete(title, ordered, ordered, cut), Danger: true, Close: false),
        }, rows);
    }

    /// <summary>Copies the files and says so in the toast's title (the toast stays, other buttons remain usable).</summary>
    private static ToastAction Copy(IReadOnlyList<string> files, string label) =>
        new(label, "", () =>
        {
            bool ok = FileActions.CopyToClipboard(files);
            ToastWindow.Current?.SetTitle(ok ? Loc.F("G.Copied", files.Count) : Loc.T("G.CopyFailed"));
        }, Tip: Loc.T("G.CopyHint"), Close: false);

    /// <summary>Compresses to the Discord limit in %TEMP% and copies; progress and result replace the toast.</summary>
    private static ToastAction Discord(string file, string label) =>
        new(label, "", () => _ = DiscordAsync(file), Tip: Loc.T("D.Hint"), Close: false);

    private static async Task DiscordAsync(string file)
    {
        var name = Path.GetFileName(file);
        var toast = ToastWindow.Show(Loc.F("D.Working", 0), name, ToastKind.Info, 3600);
        try
        {
            var progress = new Progress<double>(p => { if (toast.IsOpen) toast.SetTitle(Loc.F("D.Working", (int)(p * 100))); });
            var output = await DiscordShare.ExportWholeAsync(file, progress, CancellationToken.None);
            var done = DiscordShare.Finish(output);
            ToastWindow.Show(done, name, ToastKind.Ok, 8, new[]
            {
                new ToastAction(Loc.T("N.Folder"), "", () => FileActions.ShowInFolder(output)),
            });
        }
        catch (Exception ex)
        {
            Log.Error("Discord export from toast failed", ex);
            ToastWindow.Show(Loc.F("D.Failed", ex.Message), name, ToastKind.Error, 8);
        }
    }

    private static void Delete(string title, IReadOnlyList<string> all, IReadOnlyList<string> toDelete, Action<string> cut)
    {
        int deleted = 0;
        foreach (var p in toDelete)
        {
            try { FileActions.Recycle(p); deleted++; Log.Info($"Deleted from toast: {p}"); }
            catch (Exception ex) { Log.Warn($"Delete from toast failed: {p}: {ex.Message}"); }
        }
        var left = all.Where(File.Exists).ToList();
        if (left.Count == 0)
            ToastWindow.Show(Loc.T("N.DeletedAll"), Loc.T("N.DeletedHint"), ToastKind.Info, 3);
        else
            Show(deleted > 0 ? Loc.F("N.DeletedSome", deleted) : title, left, cut);
    }
}
