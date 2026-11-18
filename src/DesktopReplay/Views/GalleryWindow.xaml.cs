using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopReplay.Core;
using DesktopReplay.Core.Library;
using DesktopReplay.Core.Settings;
using DesktopReplay.Services;

namespace DesktopReplay.Views;

public sealed class VideoItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public LibraryEntry Entry { get; set; }
    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }
    public bool ThumbRequested { get; set; }

    public VideoItem(LibraryEntry e) => Entry = e;

    public string DurationText => Entry.Duration > 0 ? WindowHelper.FormatDuration(TimeSpan.FromSeconds(Math.Round(Entry.Duration))) : "—";
    public string InfoText => $"{WindowHelper.FormatBytes(Entry.Size)} · {Entry.Date.ToString("g", Loc.I.Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU"))}";

    public void Update(LibraryEntry e)
    {
        bool changed = e.Size != Entry.Size || e.Modified != Entry.Modified;
        Entry = e;
        if (changed) { Thumb = null; ThumbRequested = false; }
        PropertyChanged?.Invoke(this, new(nameof(Entry)));
        PropertyChanged?.Invoke(this, new(nameof(DurationText)));
        PropertyChanged?.Invoke(this, new(nameof(InfoText)));
    }
}

public sealed record GroupItem(string Key, string Display, int Count);

public partial class GalleryWindow : Window
{
    public enum SortMode { Newest, Oldest, Shortest, Longest, Largest, Name }
    private sealed record Opt(string Label, object Value) { public override string ToString() => Label; }

    private readonly Func<AppSettings> _settings;
    private readonly Action _saveSettings;
    private readonly Dictionary<string, VideoItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<VideoItem> _view = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly DispatcherTimer _rescanTimer, _filterTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _loading, _scanning, _rescanPending;

    public GalleryWindow(Func<AppSettings> settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        var app = settings();
        InitializeComponent();
        TileList.ItemsSource = _view;
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); _ = RescanAsync(); };
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };

        _loading = true;
        SortCombo.ItemsSource = new[]
        {
            new Opt(Loc.T("G.SortNewest"), SortMode.Newest), new Opt(Loc.T("G.SortOldest"), SortMode.Oldest),
            new Opt(Loc.T("G.SortShortest"), SortMode.Shortest), new Opt(Loc.T("G.SortLongest"), SortMode.Longest),
            new Opt(Loc.T("G.SortSize"), SortMode.Largest), new Opt(Loc.T("G.SortName"), SortMode.Name),
        };
        SortCombo.SelectedValue = Enum.TryParse<SortMode>(app.GallerySort, out var sm) ? sm : SortMode.Newest;
        var units = new[] { new Opt(Loc.T("G.Sec"), 1.0), new Opt(Loc.T("G.Min"), 60.0), new Opt(Loc.T("G.Hour"), 3600.0) };
        DurMinUnit.ItemsSource = units; DurMinUnit.SelectedIndex = 0;
        DurMaxUnit.ItemsSource = units; DurMaxUnit.SelectedIndex = 1;
        ZoomSlider.Value = Math.Clamp(app.GalleryTileWidth, 180, 560);
        _loading = false;

        SourceInitialized += (_, _) =>
        {
            WindowHelper.UseDarkTitleBar(this);
            WindowHelper.ExcludeFromCapture(this);
        };
        Loaded += async (_, _) => { StartWatching(); await RescanAsync(); };
        Closed += (_, _) =>
        {
            _cts.Cancel();
            foreach (var w in _watchers) w.Dispose();
            _settings().GalleryTileWidth = (int)ZoomSlider.Value;
            _settings().GallerySort = SortCombo.SelectedValue?.ToString() ?? "Newest";
            _saveSettings();
        };
    }

    private IEnumerable<string> Folders => new[] { _settings().OutputFolder, _settings().EffectiveRecordingsFolder };

    // ---------- scanning ----------

    private void StartWatching()
    {
        foreach (var root in MediaLibrary.Roots(Folders))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
                FileSystemEventHandler h = (_, e) => { if (MediaLibrary.IsVideo(e.FullPath) || !Path.HasExtension(e.FullPath)) Dispatcher.BeginInvoke(ScheduleRescan); };
                w.Created += h; w.Deleted += h; w.Changed += h;
                w.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleRescan);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Warn($"Gallery watcher for {root}: {ex.Message}"); }
        }
    }

    public void ScheduleRescan()
    {
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private async Task RescanAsync()
    {
        if (_scanning) { _rescanPending = true; return; }
        _scanning = true;
        try
        {
            var folders = Folders.ToList();
            var list = await Task.Run(() => MediaLibrary.Scan(folders, _cts.Token));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in list)
            {
                seen.Add(e.Path);
                if (_items.TryGetValue(e.Path, out var item)) item.Update(e);
                else _items[e.Path] = new VideoItem(e);
            }
            foreach (var k in _items.Keys.Where(k => !seen.Contains(k)).ToList()) _items.Remove(k);
            RebuildGroups();
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Gallery scan failed", ex); }
        finally
        {
            _scanning = false;
            if (_rescanPending) { _rescanPending = false; ScheduleRescan(); }
        }
    }

    private void RebuildGroups()
    {
        var selected = (GroupList.SelectedItem as GroupItem)?.Key;
        var groups = new List<GroupItem> { new("*", Loc.T("G.AllGames"), _items.Count) };
        groups.AddRange(_items.Values.GroupBy(i => i.Entry.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new GroupItem(g.Key, g.Key == "" ? Loc.T("G.Other") : g.Key, g.Count())));
        _loading = true;
        GroupList.ItemsSource = groups;
        GroupList.SelectedItem = groups.FirstOrDefault(g => g.Key == selected) ?? groups[0];
        _loading = false;
    }

    // ---------- filtering ----------

    private void OnFilterChanged(object sender, EventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_loading) return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) ApplyFilter(); }

    private void OnDateChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        UpdateQuickChips();
        ApplyFilter();
    }

    private static double? ParseNum(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private void ApplyFilter()
    {
        if (!IsLoaded) return;
        string q = SearchBox.Text.Trim();
        string? group = (GroupList.SelectedItem as GroupItem)?.Key;
        DateTime? from = DateFrom.SelectedDate?.Date, to = DateTo.SelectedDate?.Date.AddDays(1);
        double? minS = ParseNum(DurMin.Text) * (double)(DurMinUnit.SelectedValue ?? 1.0);
        double? maxS = ParseNum(DurMax.Text) * (double)(DurMaxUnit.SelectedValue ?? 60.0);

        IEnumerable<VideoItem> items = _items.Values;
        if (group != null && group != "*") items = items.Where(i => string.Equals(i.Entry.Group, group, StringComparison.OrdinalIgnoreCase));
        if (q.Length > 0) items = items.Where(i => i.Entry.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                                   i.Entry.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        if (from != null) items = items.Where(i => i.Entry.Date >= from);
        if (to != null) items = items.Where(i => i.Entry.Date < to);
        if (minS != null) items = items.Where(i => i.Entry.Duration >= minS);
        if (maxS != null) items = items.Where(i => i.Entry.Duration <= maxS);

        items = (SortCombo.SelectedValue as SortMode? ?? SortMode.Newest) switch
        {
            SortMode.Oldest => items.OrderBy(i => i.Entry.Date),
            SortMode.Shortest => items.OrderBy(i => i.Entry.Duration).ThenByDescending(i => i.Entry.Date),
            SortMode.Longest => items.OrderByDescending(i => i.Entry.Duration).ThenByDescending(i => i.Entry.Date),
            SortMode.Largest => items.OrderByDescending(i => i.Entry.Size),
            SortMode.Name => items.OrderBy(i => i.Entry.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderByDescending(i => i.Entry.Date),
        };
        var list = items.ToList();

        _view.Clear();
        foreach (var i in list) _view.Add(i);
        CountText.Text = Loc.F("G.Count", list.Count, WindowHelper.FormatBytes(list.Sum(i => i.Entry.Size)));
        EmptyPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _items.Count == 0 ? Loc.T("G.EmptyFolder") : Loc.T("G.NothingFound");
        _ = LoadThumbsAsync(list);
    }

    private async Task LoadThumbsAsync(List<VideoItem> list)
    {
        foreach (var item in list)
        {
            if (_cts.IsCancellationRequested) return;
            if (item.ThumbRequested) continue;
            item.ThumbRequested = true;
            var entry = item.Entry;
            var bmp = await MediaLibrary.GetThumbnailAsync(entry, 400, _cts.Token);
            if (item.Entry == entry) item.Thumb = bmp;
        }
    }

    // ---------- quick date filters ----------

    private static (DateTime? From, DateTime? To) QuickRange(string tag)
    {
        var today = DateTime.Today;
        int dow = ((int)today.DayOfWeek + 6) % 7; // Monday = 0
        var monday = today.AddDays(-dow);
        return tag switch
        {
            "today" => (today, today),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "week" => (monday, today),
            "lastweek" => (monday.AddDays(-7), monday.AddDays(-1)),
            "older" => (null, today.AddDays(-14)),
            _ => (null, null),
        };
    }

    private IEnumerable<ToggleButton> QuickChips => new[] { QToday, QYesterday, QWeek, QLastWeek, QOlder };

    private bool _syncingChips;

    private void OnQuickDate(object sender, RoutedEventArgs e)
    {
        if (_syncingChips || _loading) return;
        var chip = (ToggleButton)sender;
        var (f, t) = chip.IsChecked == true ? QuickRange((string)chip.Tag) : (null, null);
        _loading = true;
        DateFrom.SelectedDate = f;
        DateTo.SelectedDate = t;
        _loading = false;
        UpdateQuickChips();
        ApplyFilter();
    }

    private void UpdateQuickChips()
    {
        _syncingChips = true;
        try { SyncChips(); } finally { _syncingChips = false; }
    }

    private void SyncChips()
    {
        foreach (var c in QuickChips)
        {
            var (f, t) = QuickRange((string)c.Tag);
            c.IsChecked = (DateFrom.SelectedDate != null || DateTo.SelectedDate != null) &&
                          DateFrom.SelectedDate?.Date == f && DateTo.SelectedDate?.Date == t;
        }
    }

    private void OnResetFilters(object sender, RoutedEventArgs e)
    {
        _loading = true;
        SearchBox.Text = "";
        DateFrom.SelectedDate = DateTo.SelectedDate = null;
        DurMin.Text = DurMax.Text = "";
        _loading = false;
        UpdateQuickChips();
        if (GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
        ApplyFilter();
    }

    // ---------- zoom ----------

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }

    private void OnGridWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + (e.Delta > 0 ? 30 : -30), ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { _ = RescanAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape && !SearchBox.IsKeyboardFocused) Close();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RescanAsync();

    // ---------- tile actions ----------

    private static VideoItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as VideoItem;

    private void OnTilePreview(object sender, MouseButtonEventArgs e) { if (ItemOf(sender) is { } i) OpenEditor(i, edit: false); }
    private void OnPreview(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) OpenEditor(i, edit: false); }
    private void OnCut(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) OpenEditor(i, edit: true); }

    private void OpenEditor(VideoItem item, bool edit)
    {
        if (!File.Exists(item.Entry.Path)) { ScheduleRescan(); return; }
        var w = new ClipEditorWindow(item.Entry, edit) { Owner = this };
        w.Closed += (_, _) => { if (w.FilesChanged) _ = RescanAsync(); };
        w.Show();
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) FileActions.ShowInFolder(i.Entry.Path);
    }

    private void OnOpenPlayer(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } i) App.OpenPath(i.Entry.Path);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } i) return;
        if (!FileActions.ConfirmAndRecycle(this, i.Entry.Path)) return;
        _items.Remove(i.Entry.Path);
        _view.Remove(i);
        RebuildGroups();
        ApplyFilter();
    }
}

public static class FileActions
{
    public static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Warn($"explorer failed: {ex.Message}"); }
    }

    /// <summary>Moves a file to the Recycle Bin (not a permanent delete).</summary>
    public static void Recycle(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    public static bool ConfirmAndRecycle(Window owner, string path)
    {
        var r = MessageBox.Show(owner, Loc.F("G.DeleteConfirm", Path.GetFileName(path)), Loc.T("G.Delete"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return false;
        try
        {
            Recycle(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Delete failed", ex);
            MessageBox.Show(owner, ex.Message, Loc.T("G.Delete"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
