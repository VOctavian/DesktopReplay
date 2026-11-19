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

    private bool _selected, _showCheck;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    /// <summary>Selection mode is on (something is selected): checkboxes are shown on every tile.</summary>
    public bool ShowCheck { get => _showCheck; set { if (_showCheck == value) return; _showCheck = value; PropertyChanged?.Invoke(this, new(nameof(ShowCheck))); } }
    public bool IsImage => Entry.IsImage;

    private bool _busy;
    private double _progress;
    /// <summary>A Discord export of this file is running.</summary>
    public bool IsBusy { get => _busy; set { _busy = value; PropertyChanged?.Invoke(this, new(nameof(IsBusy))); } }
    public double Progress { get => _progress; set { _progress = value; PropertyChanged?.Invoke(this, new(nameof(Progress))); } }

    public string DurationText => Entry.Duration > 0 ? WindowHelper.FormatDuration(TimeSpan.FromSeconds(Math.Round(Entry.Duration))) : "—";
    public string InfoText
    {
        get
        {
            var date = Entry.Date.ToString("g", Loc.I.Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ru-RU"));
            var size = WindowHelper.FormatBytes(Entry.Size);
            return IsImage && Entry.Width > 0 ? $"{Entry.Width}×{Entry.Height} · {size} · {date}" : $"{size} · {date}";
        }
    }

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
    private bool _images; // screenshots tab
    private VideoItem? _anchor; // for Shift+click ranges
    private readonly DispatcherTimer _hoverTimer;
    private Grid? _hoverHost;
    private MediaElement? _hoverPlayer;

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
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); StartHoverPreview(); };

        _loading = true;
        FillSortOptions(Enum.TryParse<SortMode>(app.GallerySort, out var sm) ? sm : SortMode.Newest);
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
            StopHoverPreview();
            foreach (var w in _watchers) w.Dispose();
            _settings().GalleryTileWidth = (int)ZoomSlider.Value;
            _settings().GallerySort = SortCombo.SelectedValue?.ToString() ?? "Newest";
            _saveSettings();
        };
    }

    private IEnumerable<string> Folders => _images
        ? new[] { _settings().EffectiveScreenshotsFolder }
        : new[] { _settings().OutputFolder, _settings().EffectiveRecordingsFolder };

    private IEnumerable<string> AllFolders => new[] { _settings().OutputFolder, _settings().EffectiveRecordingsFolder, _settings().EffectiveScreenshotsFolder };

    private void FillSortOptions(SortMode selected)
    {
        var opts = new List<Opt> { new(Loc.T("G.SortNewest"), SortMode.Newest), new(Loc.T("G.SortOldest"), SortMode.Oldest) };
        if (!_images) { opts.Add(new(Loc.T("G.SortShortest"), SortMode.Shortest)); opts.Add(new(Loc.T("G.SortLongest"), SortMode.Longest)); }
        opts.Add(new(Loc.T("G.SortSize"), SortMode.Largest));
        opts.Add(new(Loc.T("G.SortName"), SortMode.Name));
        SortCombo.ItemsSource = opts;
        SortCombo.SelectedValue = opts.Any(o => (SortMode)o.Value == selected) ? selected : SortMode.Newest;
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool images = TabShots.IsChecked == true;
        if (images == _images) return;
        _images = images;
        StopHoverPreview();
        _loading = true;
        FillSortOptions(SortCombo.SelectedValue as SortMode? ?? SortMode.Newest);
        DurationFilter.Visibility = images ? Visibility.Collapsed : Visibility.Visible;
        if (GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
        _loading = false;
        _items.Clear();
        _view.Clear();
        UpdateSelection();
        _ = RescanAsync();
    }

    // ---------- scanning ----------

    private void StartWatching()
    {
        foreach (var root in MediaLibrary.Roots(AllFolders))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
                FileSystemEventHandler h = (_, e) =>
                {
                    if (MediaLibrary.IsVideo(e.FullPath) || MediaLibrary.IsImage(e.FullPath) || !Path.HasExtension(e.FullPath))
                        Dispatcher.BeginInvoke(ScheduleRescan);
                };
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
            bool images = _images;
            var list = await Task.Run(() => MediaLibrary.Scan(folders, _cts.Token, images));
            if (images != _images) { _rescanPending = true; return; } // tab switched meanwhile
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
        if (!_images && minS != null) items = items.Where(i => i.Entry.Duration >= minS);
        if (!_images && maxS != null) items = items.Where(i => i.Entry.Duration <= maxS);

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
        CountText.Text = Loc.F(_images ? "G.CountShots" : "G.Count", list.Count, WindowHelper.FormatBytes(list.Sum(i => i.Entry.Size)));
        EmptyPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _items.Count == 0 ? Loc.T(_images ? "G.EmptyShots" : "G.EmptyFolder") : Loc.T("G.NothingFound");
        foreach (var i in _items.Values) if (i.IsSelected && !list.Contains(i)) i.IsSelected = false; // hidden items leave the selection
        UpdateSelection();
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
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool inText = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.F && ctrl) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { _ = RescanAsync(); e.Handled = true; }
        else if (e.Key == Key.A && ctrl && !inText) { SelectAll(); e.Handled = true; }
        else if (e.Key == Key.C && ctrl && !inText && Selected.Count > 0) { CopyItems(Selected); e.Handled = true; }
        else if (e.Key == Key.Delete && !inText && Selected.Count > 0) { DeleteItems(Selected); e.Handled = true; }
        else if (e.Key == Key.Escape && !inText) { if (Selected.Count > 0) ClearSelection(); else Close(); e.Handled = true; }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RescanAsync();

    // ---------- tile actions ----------

    private static VideoItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as VideoItem;

    /// <summary>Click on a thumbnail: Ctrl toggles, Shift selects a range, in selection mode a click toggles; otherwise opens.</summary>
    private void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } i) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Shift) != 0 && _anchor != null && _view.Contains(_anchor))
        {
            int a = _view.IndexOf(_anchor), b = _view.IndexOf(i);
            for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) _view[k].IsSelected = true;
            UpdateSelection();
        }
        else if ((mods & ModifierKeys.Control) != 0 || Selected.Count > 0) Toggle(i);
        else Open(i);
        e.Handled = true;
    }

    private void OnCheckClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is { } i) Toggle(i);
        e.Handled = true;
    }

    private void Toggle(VideoItem i)
    {
        i.IsSelected = !i.IsSelected;
        _anchor = i;
        UpdateSelection();
    }

    private void Open(VideoItem i)
    {
        if (i.IsImage) App.OpenPath(i.Entry.Path);
        else OpenEditor(i, edit: false);
    }

    private List<VideoItem> Selected => _view.Where(i => i.IsSelected).ToList();

    private void UpdateSelection()
    {
        var sel = Selected;
        bool any = sel.Count > 0;
        foreach (var i in _view) i.ShowCheck = any;
        SelectionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any) SelectionText.Text = Loc.F("G.Selected", sel.Count, WindowHelper.FormatBytes(sel.Sum(i => i.Entry.Size)));
    }

    private void SelectAll()
    {
        foreach (var i in _view) i.IsSelected = true;
        UpdateSelection();
    }

    private void ClearSelection()
    {
        foreach (var i in _view) i.IsSelected = false;
        _anchor = null;
        UpdateSelection();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SelectAll();
    private void OnClearSelection(object sender, RoutedEventArgs e) => ClearSelection();
    private void OnCopySelected(object sender, RoutedEventArgs e) => CopyItems(Selected);
    private void OnDeleteSelected(object sender, RoutedEventArgs e) => DeleteItems(Selected);
    private void OnCopy(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) CopyItems(new() { i }); }

    private async void OnDiscord(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { IsImage: false, IsBusy: false } item) return;
        StopHoverPreview();
        item.IsBusy = true;
        item.Progress = 0;
        try
        {
            var progress = new Progress<double>(p => { item.Progress = p; CountText.Text = Loc.F("D.Working", (int)(p * 100)); });
            var output = await DiscordShare.ExportWholeAsync(item.Entry.Path, progress, _cts.Token);
            CountText.Text = DiscordShare.Finish(output);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Discord export failed", ex);
            CountText.Text = Loc.F("D.Failed", ex.Message);
        }
        finally { item.IsBusy = false; }
    }

    private void CopyItems(List<VideoItem> items)
    {
        if (items.Count == 0) return;
        bool ok = FileActions.CopyToClipboard(items.Select(i => i.Entry.Path));
        CountText.Text = ok ? Loc.F("G.Copied", items.Count) : Loc.T("G.CopyFailed");
    }

    private void DeleteItems(List<VideoItem> items)
    {
        if (items.Count == 0) return;
        StopHoverPreview();
        string question = items.Count == 1 ? Loc.F("G.DeleteConfirm", items[0].Entry.Name) : Loc.F("G.DeleteManyConfirm", items.Count, WindowHelper.FormatBytes(items.Sum(i => i.Entry.Size)));
        if (MessageBox.Show(this, question, Loc.T("G.Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var failed = new List<string>();
        foreach (var i in items)
        {
            try
            {
                FileActions.Recycle(i.Entry.Path);
                _items.Remove(i.Entry.Path);
                _view.Remove(i);
            }
            catch (Exception ex)
            {
                Log.Warn($"Delete failed for {i.Entry.Path}: {ex.Message}");
                failed.Add(i.Entry.Name);
            }
        }
        if (failed.Count > 0) MessageBox.Show(this, Loc.F("G.DeleteFailed", string.Join("\n", failed)), Loc.T("G.Delete"), MessageBoxButton.OK, MessageBoxImage.Warning);
        _anchor = null;
        RebuildGroups();
        ApplyFilter();
    }

    // ---------- hover preview ----------

    private void OnThumbEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Grid g || ItemOf(sender) is not { IsImage: false }) return;
        StopHoverPreview();
        _hoverHost = g;
        _hoverTimer.Start();
    }

    private void OnThumbLeave(object sender, MouseEventArgs e)
    {
        if (sender == _hoverHost) StopHoverPreview();
    }

    private void StartHoverPreview()
    {
        if (_hoverHost == null || ItemOf(_hoverHost) is not { } item || !File.Exists(item.Entry.Path)) return;
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true,
            Stretch = Stretch.UniformToFill, IsHitTestVisible = false, ScrubbingEnabled = false,
        };
        double start = item.Entry.Duration > 8 ? item.Entry.Duration * 0.2 : 0;
        player.MediaOpened += (_, _) => { if (start > 0) player.Position = TimeSpan.FromSeconds(start); };
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.FromSeconds(start); player.Play(); };
        player.MediaFailed += (_, _) => StopHoverPreview();
        _hoverHost.Children.Insert(1, player); // above the thumbnail, below the badges
        player.Source = new Uri(item.Entry.Path);
        player.Play();
        _hoverPlayer = player;
    }

    private void StopHoverPreview()
    {
        _hoverTimer.Stop();
        if (_hoverPlayer != null)
        {
            try { _hoverPlayer.Stop(); _hoverPlayer.Close(); } catch { }
            (_hoverPlayer.Parent as Panel)?.Children.Remove(_hoverPlayer);
            _hoverPlayer = null;
        }
        _hoverHost = null;
    }

    private void OnPreview(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) Open(i); }
    private void OnCut(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) OpenEditor(i, edit: true); }

    private void OpenEditor(VideoItem item, bool edit)
    {
        StopHoverPreview();
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
        if (ItemOf(sender) is { } i) DeleteItems(new() { i });
    }
}

/// <summary>One-click "for Discord": compress to the size limit into %TEMP% and put the file on the clipboard.</summary>
public static class DiscordShare
{
    public static string TempDir => Path.Combine(Path.GetTempPath(), "DesktopReplay", "discord");

    /// <summary>A fresh path in its own folder (so a file still on the clipboard is never overwritten).</summary>
    public static string NewTempPath(string sourcePath)
    {
        Cleanup();
        var dir = Path.Combine(TempDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(sourcePath) + ".mp4");
    }

    /// <summary>Removes exports older than a day (long enough to paste them, short enough not to pile up).</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var d in new DirectoryInfo(TempDir).EnumerateDirectories())
                if (d.CreationTime < DateTime.Now.AddDays(-1))
                    try { d.Delete(true); } catch { }
        }
        catch { }
    }

    /// <summary>Whole file, first audio track (what Discord plays).</summary>
    public static async Task<string> ExportWholeAsync(string source, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await Task.Run(() => MediaProbe.Probe(source), ct);
        var tracks = info.Audio.Select(a => new AudioTrackEdit(a.Index, a.Title, true, 1, 0, info.Duration)).ToList();
        var output = NewTempPath(source);
        await ClipExporter.ExportAsync(new ExportRequest(source, output, 0, info.Duration, false, 0.5, tracks, false, "",
            ExportPreset.Discord, ClipExporter.DiscordLimit), progress, ct);
        return output;
    }

    /// <summary>Copies the result and returns the status line to show.</summary>
    public static string Finish(string output)
    {
        bool ok = FileActions.CopyToClipboard(new[] { output });
        Log.Info($"Discord share ready: {output} ({new FileInfo(output).Length / 1024} KB)");
        return ok ? Loc.F("D.Done", WindowHelper.FormatBytes(new FileInfo(output).Length)) : Loc.T("G.CopyFailed");
    }
}

public static class FileActions
{
    public static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Warn($"explorer failed: {ex.Message}"); }
    }

    /// <summary>
    /// Puts files on the clipboard as a file list, so Ctrl+V in Discord/Telegram/Explorer attaches them.
    /// A single image also goes on as a bitmap (for apps that paste pictures).
    /// </summary>
    public static bool CopyToClipboard(IEnumerable<string> paths)
    {
        var list = paths.Where(File.Exists).ToArray();
        if (list.Length == 0) return false;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(list);
        var data = new DataObject();
        data.SetFileDropList(files);
        if (list.Length == 1 && MediaLibrary.IsImage(list[0]))
        {
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(list[0]);
                img.EndInit();
                data.SetImage(img);
            }
            catch { }
        }
        for (int i = 0; ; i++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (System.Runtime.InteropServices.COMException) when (i < 5) { Thread.Sleep(60); } // clipboard busy
        }
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
