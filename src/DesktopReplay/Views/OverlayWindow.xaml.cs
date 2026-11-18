using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopReplay.Core;
using DesktopReplay.Core.Engine;
using DesktopReplay.Core.Interop;
using DesktopReplay.Core.Media;
using DesktopReplay.Core.Settings;
using DesktopReplay.Services;

namespace DesktopReplay.Views;

public sealed record Opt(object Value, string Label, string Description = "", bool Enabled = true)
{
    public override string ToString() => Label; // shown in the closed ComboBox
}

public sealed class MonitorOption
{
    public required MonitorInfo Info { get; init; }
    public required string Label { get; init; }
    public bool Selected { get; set; }
}

public sealed class Meter : INotifyPropertyChanged
{
    private double _width;
    public required string Label { get; init; }
    public double Width { get => _width; set { _width = value; PropertyChanged?.Invoke(this, new(nameof(Width))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class OverlayWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private AppSettings _draft = new();
    private readonly ObservableCollection<string> _exclusions = new();
    private List<MonitorOption> _monitors = new();
    private readonly List<Meter> _meters = new();
    private double _storageCacheAge = double.MaxValue;

    public OverlayWindow(App app)
    {
        _app = app;
        InitializeComponent();
        for (int i = 1; i <= 5; i++) _meters.Add(new Meter { Label = $"{i} · {Loc.T("Track" + i)}" });
        Meters.ItemsSource = _meters;
        ExclusionList.ItemsSource = _exclusions;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => RefreshStatus();
        SourceInitialized += (_, _) =>
        {
            WindowHelper.ExcludeFromCapture(this);
            WindowHelper.MakeToolWindow(this, clickThrough: false);
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        Loc.I.PropertyChanged += (_, _) => RefreshLabels();
    }

    public bool IsOpen => IsVisible;

    // ---------- Show / hide ----------

    public void Open(bool settings = false)
    {
        var (mon, _) = WindowHelper.CursorMonitor();
        if (!IsVisible) Show();
        WindowHelper.PlacePhysical(this, mon);
        RefreshLabels();
        _storageCacheAge = double.MaxValue;
        RefreshStatus();
        if (settings) ShowSettings();
        else ShowHome();
        Activate();
        WindowHelper.ForceForeground(this);
        Focus();
    }

    public void Close2()
    {
        if (SettingsPanel.Visibility == Visibility.Visible) Keyboard.ClearFocus();
        Hide();
    }

    private void ShowHome()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Visible;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.FocusedElement is HotkeyBox) return;
        if (SettingsPanel.Visibility == Visibility.Visible) ShowHome();
        else Close2();
        e.Handled = true;
    }

    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && SettingsPanel.Visibility != Visibility.Visible) Close2();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close2();

    // ---------- Home ----------

    private void RefreshLabels()
    {
        var hk = _app.Settings.Hotkeys;
        SaveKeyText.Text = hk.SaveReplay;
        RecordKeyText.Text = hk.ToggleRecording;
        ShotKeyText.Text = hk.Screenshot;
        FooterHint.Text = Loc.F("EscToClose", hk.Overlay);
        for (int i = 0; i < _meters.Count; i++)
            _meters[i] = new Meter { Label = $"{i + 1} · {Loc.T("Track" + (i + 1))}", Width = _meters[i].Width };
        Meters.ItemsSource = null;
        Meters.ItemsSource = _meters;
    }

    private void RefreshStatus()
    {
        var e = _app.Engine;
        var s = _app.Settings;
        bool replay = e.ReplayActive;
        var total = TimeSpan.FromSeconds(s.ReplaySeconds);
        var buffered = e.BufferedDuration > total ? total : e.BufferedDuration;

        ReplayChipText.Text = $"{Loc.T("Replay")}: {(replay ? Loc.T("On") : Loc.T("Off"))}";
        ReplayChipDot.Fill = (Brush)FindResource(replay ? "Ok" : "Muted");
        ReplayStatus.Text = replay ? Loc.F("Buffered", WindowHelper.FormatDuration(buffered), WindowHelper.FormatDuration(total)) : Loc.T("Off");
        BufferBar.Width = replay ? 208 * Math.Clamp(buffered.TotalSeconds / total.TotalSeconds, 0, 1) : 0;
        SaveReplayBtn.IsEnabled = replay;
        ReplayToggleBtn.Content = replay ? Loc.T("TurnOff") : Loc.T("TurnOn");

        bool rec = e.IsRecording;
        RecChip.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        RecChipText.Text = WindowHelper.FormatDuration(e.RecordingElapsed);
        RecordStatus.Text = rec ? Loc.F("RecordingFor", WindowHelper.FormatDuration(e.RecordingElapsed)) : Loc.T("NotRecording");
        RecordBtn.Content = rec ? Loc.T("StopRecording") : Loc.T("StartRecording");
        RecordBtn.Style = rec ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button));
        if (rec) RecordBtn.Foreground = Brushes.White;

        ActiveAppText.Text = Loc.F("ActiveApp", e.ActiveAppName);

        if (e.LastError != null)
        {
            ErrorText.Text = Loc.F("EngineError", e.LastError);
            ErrorText.Visibility = Visibility.Visible;
        }
        else ErrorText.Visibility = Visibility.Collapsed;

        var levels = e.AudioLevels;
        for (int i = 0; i < _meters.Count; i++)
        {
            double target = levels != null ? Math.Sqrt(Math.Clamp(levels[i], 0, 1)) * 150 : 0;
            _meters[i].Width = Math.Max(target, _meters[i].Width * 0.82);
        }

        _storageCacheAge += _timer.Interval.TotalSeconds;
        if (_storageCacheAge > 5)
        {
            _storageCacheAge = 0;
            _ = UpdateStorageAsync();
        }
    }

    private async Task UpdateStorageAsync()
    {
        var u = await Task.Run(() => _app.Engine.GetStorageUsage());
        if (u.LimitBytes > 0)
        {
            StorageText.Text = Loc.F("StorageUsed", WindowHelper.FormatBytes(u.UsedBytes), WindowHelper.FormatBytes(u.LimitBytes));
            StorageBar.Width = 208 * Math.Clamp(u.Percent / 100, 0, 1);
            StorageBar.Background = (Brush)FindResource(u.Percent >= _app.Settings.WarnAtPercent ? "Warn" : "Info");
        }
        else
        {
            StorageText.Text = Loc.F("StorageNoLimit", WindowHelper.FormatBytes(u.UsedBytes));
            StorageBar.Width = 0;
        }
        UsageText.Text = Loc.F("S.Usage", WindowHelper.FormatBytes(u.UsedBytes));
    }

    private void OnSaveReplay(object sender, RoutedEventArgs e) { Close2(); _app.SaveReplay(); }
    private void OnToggleReplay(object sender, RoutedEventArgs e) => _app.ToggleReplay();
    private void OnToggleRecord(object sender, RoutedEventArgs e) { Close2(); _app.ToggleRecording(); }
    private async void OnScreenshot(object sender, RoutedEventArgs e)
    {
        Close2();
        await Task.Delay(250); // let the overlay disappear (it's excluded from capture anyway)
        _app.Screenshot();
    }
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _app.OpenFolder();
    private void OnOpenGallery(object sender, RoutedEventArgs e) { Close2(); _app.ShowGallery(); }
    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowSettings();

    // ---------- Settings ----------

    private void ShowSettings()
    {
        _draft = _app.Settings.Clone();
        BuildOptions();
        DataContext = _draft;
        _exclusions.Clear();
        foreach (var x in _draft.ExcludedProcesses) _exclusions.Add(x);
        VersionText.Text = Loc.F("A.Version", App.VersionString);
        UpdateStatus.Text = "";
        SystemInfo.Text = $"{_app.Engine.VideoSummary}\n{Environment.OSVersion.VersionString}\n{Log.Directory}";
        HomePanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        OnDraftChanged(this, new RoutedEventArgs());
    }

    private void BuildOptions()
    {
        LanguageCombo.ItemsSource = new[] { new Opt("ru", "Русский"), new Opt("en", "English") };

        string Min(int sec) => sec < 60 ? $"{sec} {(Loc.I.Language == "en" ? "sec" : "сек")}" : $"{sec / 60} {(Loc.I.Language == "en" ? "min" : "мин")}";
        ReplayLengthCombo.ItemsSource = new[] { 30, 60, 120, 180, 300, 600, 900, 1200 }.Select(s => new Opt(s, Min(s))).ToList();

        var all = Monitors.Enumerate();
        _monitors = all.Select(m => new MonitorOption
        {
            Info = m,
            Label = $"{Loc.T("S.Monitors").TrimEnd('ы', 's')} {m.DisplayName}",
            Selected = _draft.Monitors.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase),
        }).ToList();
        if (!_monitors.Any(m => m.Selected)) (_monitors.FirstOrDefault(m => m.Info.IsPrimary) ?? _monitors.FirstOrDefault())?.Let(m => m.Selected = true);
        MonitorList.ItemsSource = _monitors;

        FpsCombo.ItemsSource = new[] { 24, 30, 60, 120, 144 }.Select(f => new Opt(f, Loc.T("Fps" + f))).ToList();

        int maxH = all.Count > 0 ? all.Max(m => m.Height) : 2160;
        var res = new List<Opt> { new(0, Loc.T("S.ResNative")) };
        foreach (var h in new[] { 2160, 1440, 1080, 720, 480 }.Where(h => h < maxH)) res.Add(new Opt(h, $"{h}p"));
        ResolutionCombo.ItemsSource = res;

        CodecCombo.ItemsSource = new[]
        {
            CodecOpt(VideoCodec.H264, "H.264 (AVC)", "CodecH264"),
            CodecOpt(VideoCodec.Hevc, "HEVC (H.265)", "CodecHevc"),
            CodecOpt(VideoCodec.Av1, "AV1", "CodecAv1"),
        };

        QualityCombo.ItemsSource = new[]
        {
            new Opt(QualityPreset.Low, Loc.T("QLow")),
            new Opt(QualityPreset.Medium, Loc.T("QMedium")),
            new Opt(QualityPreset.High, Loc.T("QHigh")),
            new Opt(QualityPreset.Ultra, Loc.T("QUltra")),
            new Opt(QualityPreset.Custom, Loc.T("QCustom")),
        };

        var mics = new List<Opt> { new("", Loc.T("S.MicDefault")) };
        mics.AddRange(CoreAudio.ListDevices(EDataFlow.Capture).Select(d => new Opt(d.Id, d.Name)));
        if (!mics.Any(m => (string)m.Value == _draft.Audio.MicDeviceId)) _draft.Audio.MicDeviceId = "";
        MicCombo.ItemsSource = mics;

        MicModeCombo.ItemsSource = new[]
        {
            new Opt(MicMode.AlwaysOn, Loc.T("MicAlways")),
            new Opt(MicMode.PushToTalk, Loc.T("MicPtt")),
            new Opt(MicMode.PushToMute, Loc.T("MicPtm")),
        };
        AudioBitrateCombo.ItemsSource = new[] { 128, 160, 192, 256, 320 }.Select(b => new Opt(b, $"{b} kbps")).ToList();
        ScreenshotCombo.ItemsSource = new[] { new Opt(ScreenshotFormat.Png, Loc.T("PngHint")), new Opt(ScreenshotFormat.Jpg, Loc.T("JpgHint")) };
    }

    private static Opt CodecOpt(VideoCodec c, string name, string descKey)
    {
        bool ok = EncoderProbe.IsUsable(c);
        return new Opt(c, ok ? name : Loc.F("CodecUnavailable", name), Loc.T(descKey), ok);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !IsInitialized) return;
        foreach (var name in new[] { "General", "Video", "Audio", "Storage", "Hotkeys", "Privacy", "About" })
            if (FindName("Sec_" + name) is FrameworkElement fe)
                fe.Visibility = (string)rb.Tag == name ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll?.ScrollToTop();
    }

    /// <summary>Recomputes size estimates and dependent visibility after any change.</summary>
    private void OnDraftChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _draft == null) return;
        var selected = _monitors.Where(m => m.Selected).ToList();
        // "Only the primary monitor" is stored as an empty list so it keeps following the primary display.
        _draft.Monitors = selected.Count == 1 && selected[0].Info.IsPrimary ? new List<string>() : selected.Select(m => m.Info.DeviceName).ToList();
        CustomBitratePanel.Visibility = _draft.Quality == QualityPreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        PushKeyPanel.Visibility = _draft.Audio.MicMode == MicMode.AlwaysOn ? Visibility.Collapsed : Visibility.Visible;

        var outputs = _monitors.Where(m => m.Selected).Select(m =>
        {
            int h = _draft.ResolutionHeight <= 0 || _draft.ResolutionHeight >= m.Info.Height ? m.Info.Height : _draft.ResolutionHeight;
            return ((int)Math.Round(h * (double)m.Info.Width / m.Info.Height), h);
        }).ToList();
        long bps = Storage.TotalBitrate(_draft, outputs);
        double mbMin = Storage.MegabytesPerMinute(bps);
        var bufferSize = WindowHelper.FormatBytes((long)(mbMin * _draft.ReplaySeconds / 60 * 1024 * 1024));
        EstimateText.Text = Loc.F("S.Estimate", mbMin.ToString("0"), bufferSize);
        ReplayLengthHint.Text = Loc.F("S.ReplayLengthHint", bufferSize);

        RestartNote.Visibility = _draft.CaptureDiffers(_app.Settings) ? Visibility.Visible : Visibility.Collapsed;

        var keys = new[] { _draft.Hotkeys.SaveReplay, _draft.Hotkeys.ToggleReplay, _draft.Hotkeys.ToggleRecording, _draft.Hotkeys.Screenshot, _draft.Hotkeys.Overlay }
            .Where(k => !string.IsNullOrEmpty(k)).ToList();
        HotkeyWarning.Text = keys.Count != keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() ? Loc.T("H.Duplicate") : _app.HotkeyConflictText;
    }

    private void OnHotkeyLostFocus(object sender, KeyboardFocusChangedEventArgs e) => OnDraftChanged(sender, e);
    private void OnTextChanged(object sender, TextChangedEventArgs e) => OnDraftChanged(sender, e);

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => Browse(p => _draft.OutputFolder = p, _draft.OutputFolder);
    private void OnBrowseBuffer(object sender, RoutedEventArgs e) => Browse(p => _draft.BufferFolder = p, _draft.BufferFolder);
    private void OnBrowseRecordings(object sender, RoutedEventArgs e) => Browse(p => _draft.RecordingsFolder = p, _draft.EffectiveRecordingsFolder);
    private void OnBrowseScreenshots(object sender, RoutedEventArgs e) => Browse(p => _draft.ScreenshotsFolder = p, _draft.EffectiveScreenshotsFolder);
    private void OnResetRecordings(object sender, RoutedEventArgs e) { _draft.RecordingsFolder = ""; Rebind(); }
    private void OnResetScreenshots(object sender, RoutedEventArgs e) { _draft.ScreenshotsFolder = ""; Rebind(); }

    private void Rebind()
    {
        DataContext = null;
        DataContext = _draft;
        OnDraftChanged(this, new RoutedEventArgs());
    }

    private void Browse(Action<string> set, string current)
    {
        Topmost = false;
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(current) ? current : "" };
            if (dlg.ShowDialog(this) == true)
            {
                set(dlg.FolderName);
                DataContext = null;
                DataContext = _draft;
                OnDraftChanged(this, new RoutedEventArgs());
            }
        }
        finally { Topmost = true; }
    }

    private void OnRunningOpened(object? sender, EventArgs e)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId) names.Add(p.ProcessName + ".exe"); }
            catch { }
            finally { p.Dispose(); }
        }
        RunningCombo.ItemsSource = names.ToList();
    }

    private void OnAddRunning(object sender, RoutedEventArgs e) => AddExclusion(RunningCombo.SelectedItem as string);

    private void OnAddManual(object sender, RoutedEventArgs e)
    {
        AddExclusion(ManualExclusion.Text);
        ManualExclusion.Text = "";
    }

    private void AddExclusion(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!_exclusions.Contains(name, StringComparer.OrdinalIgnoreCase)) _exclusions.Add(name);
    }

    private void OnRemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (ExclusionList.SelectedItem is string s) _exclusions.Remove(s);
    }

    private void OnCancelSettings(object sender, RoutedEventArgs e) => ShowHome();

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        OnDraftChanged(this, e);
        _draft.ExcludedProcesses = _exclusions.ToList();
        _draft.ReplaySeconds = Math.Clamp(_draft.ReplaySeconds, 10, 3600);
        _draft.WarnAtPercent = Math.Clamp(_draft.WarnAtPercent, 1, 100);
        _draft.MaxFolderSizeGb = Math.Max(0, _draft.MaxFolderSizeGb);
        _draft.CustomBitrateMbps = Math.Clamp(_draft.CustomBitrateMbps, 1, 300);
        if (string.IsNullOrWhiteSpace(_draft.OutputFolder)) _draft.OutputFolder = AppPaths.DefaultOutput;
        if (string.IsNullOrWhiteSpace(_draft.BufferFolder)) _draft.BufferFolder = AppPaths.DefaultBuffer;
        ShowHome();
        await _app.ApplySettingsAsync(_draft);
        RefreshLabels();
    }

    // ---------- About ----------

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        try { await _app.Updater.CheckAsync(interactive: true, status: t => UpdateStatus.Text = t); }
        finally { UpdateBtn.IsEnabled = true; }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => App.OpenPath(Log.Directory);
    private void OnOpenRepo(object sender, RoutedEventArgs e) => App.OpenPath(Updater.RepoUrl);
}

internal static class ObjectExtensions
{
    public static void Let<T>(this T obj, Action<T> action) => action(obj);
}
