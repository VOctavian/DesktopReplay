using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DesktopReplay.Core;
using DesktopReplay.Core.Engine;
using DesktopReplay.Core.Settings;
using DesktopReplay.Services;
using DesktopReplay.Views;
using Forms = System.Windows.Forms;

namespace DesktopReplay;

/// <summary>Application controller: tray icon, hotkeys, notifications, overlay and the capture engine.</summary>
public partial class App : Application
{
    private readonly SingleInstance _single;
    private readonly bool _startMinimized;
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _iconOn, _iconOff, _iconRec;
    private HotkeyManager? _hotkeys;
    private OverlayWindow? _overlay;
    private List<Hotkey> _conflicts = new();

    public ReplayEngine Engine { get; private set; } = null!;
    public AppSettings Settings => _settings;
    public Updater Updater { get; } = new();
    public static string VersionString => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string HotkeyConflictText => _conflicts.Count == 0 ? "" : string.Join("\n", _conflicts.Select(k => Loc.F("N.HotkeyConflict", k)));

    public App(SingleInstance single, bool startMinimized)
    {
        _single = single;
        _startMinimized = startMinimized;
        DispatcherUnhandledException += OnDispatcherException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _settings = SettingsStore.Load();
        Loc.I.SetLanguage(_settings.Language);

        bool firstRun = !_settings.FirstRunDone;
        if (firstRun)
        {
            _settings.FirstRunDone = true;
            // Only the installed build registers autostart (a dev build would point Windows at bin\Debug).
            if (Updater.IsInstalled) AutoStart.Set(_settings.AutoStart);
            SaveSettings();
        }

        Engine = new ReplayEngine(_settings);
        Engine.Notify += n => Dispatcher.BeginInvoke(() => OnNotice(n));
        Engine.StateChanged += () => Dispatcher.BeginInvoke(UpdateTray);

        CreateTray();
        _hotkeys = new HotkeyManager();
        HotkeyBox.EditingChanged += editing => { if (_hotkeys != null) _hotkeys.Suspended = editing; };
        RegisterHotkeys();
        _single.Listen(() => Dispatcher.BeginInvoke(() => ShowOverlay()));

        _overlay = new OverlayWindow(this);
        await Engine.StartAsync();
        UpdateTray();

        if (firstRun || !_startMinimized) ShowOverlay();
        else if (Engine.ReplayActive && _settings.NotifyPopup)
            ToastWindow.Show(Loc.T("N.ReplayOn"), _settings.Hotkeys.SaveReplay, ToastKind.Info, 2.5);

        if (_settings.CheckUpdates)
        {
            Updater.UpdateReady += v => Dispatcher.BeginInvoke(() => ToastWindow.Show(Loc.F("N.UpdateReady", v), "", ToastKind.Info, 6));
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Updater.CheckAsync(false));
        }
    }

    // ---------- Actions ----------

    public void ShowOverlay(bool settings = false)
    {
        if (_overlay == null) return;
        if (_overlay.IsOpen && !settings) { _overlay.Close2(); return; }
        _overlay.Open(settings);
    }

    public async void SaveReplay()
    {
        if (!Engine.ReplayActive)
        {
            Notify(Loc.T("N.ReplayNotActive"), "", ToastKind.Warning, error: true);
            return;
        }
        await Engine.SaveReplayAsync();
    }

    public async void ToggleReplay() => await Engine.SetReplayEnabledAsync(!Engine.ReplayActive).ContinueWith(_ =>
        Dispatcher.BeginInvoke(() =>
        {
            _settings.ReplayEnabled = Engine.Settings.ReplayEnabled;
            SaveSettings();
        }));

    public async void ToggleRecording() => await Engine.ToggleRecordingAsync();
    public async void Screenshot() => await Engine.ScreenshotAsync();

    public void OpenFolder()
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        OpenPath(_settings.OutputFolder);
    }

    public async Task ApplySettingsAsync(AppSettings s)
    {
        bool hotkeysChanged = System.Text.Json.JsonSerializer.Serialize(s.Hotkeys) != System.Text.Json.JsonSerializer.Serialize(_settings.Hotkeys);
        _settings = s.Clone();
        SaveSettings();
        Loc.I.SetLanguage(_settings.Language);
        if (Updater.IsInstalled && AutoStart.IsEnabled() != _settings.AutoStart) AutoStart.Set(_settings.AutoStart);
        if (hotkeysChanged) RegisterHotkeys();
        RebuildTrayMenu();
        await Engine.ApplySettingsAsync(_settings);
        UpdateTray();
    }

    /// <summary>Persists UI-only preferences (gallery layout) without touching the engine.</summary>
    public void SaveUiSettings()
    {
        SaveSettings();
        RebuildTrayMenu(); // the gallery may have switched the language
    }

    private Views.GalleryWindow? _gallery;

    public void ShowGallery()
    {
        if (_gallery != null)
        {
            if (_gallery.WindowState == WindowState.Minimized) _gallery.WindowState = WindowState.Normal;
            _gallery.Activate();
            return;
        }
        _gallery = new Views.GalleryWindow(() => Settings, SaveUiSettings, () => ShowOverlay(settings: true));
        _gallery.Closed += (_, _) => _gallery = null;
        _gallery.Show();
        _gallery.Activate();
    }

    private void SaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log.Error("Save settings failed", ex); }
    }

    private void RegisterHotkeys()
    {
        var h = _settings.Hotkeys;
        _conflicts = _hotkeys!.Register(new (Hotkey, Action)[]
        {
            (Hotkey.Parse(h.SaveReplay), SaveReplay),
            (Hotkey.Parse(h.ToggleReplay), ToggleReplay),
            (Hotkey.Parse(h.ToggleRecording), ToggleRecording),
            (Hotkey.Parse(h.Screenshot), Screenshot),
            (Hotkey.Parse(h.Overlay), () => ShowOverlay()),
        });
        foreach (var c in _conflicts) Log.Warn(Loc.F("N.HotkeyConflict", c));
    }

    // ---------- Notifications ----------

    private void OnNotice(Notice n)
    {
        string Files() => string.Join(", ", n.Paths.Select(Path.GetFileName));
        switch (n.Kind)
        {
            case NoticeKind.ReplaySaved:
                if (_settings.NotifySound) Sounds.Saved();
                Popup(Loc.T("N.ReplaySaved"), Files(), ToastKind.Ok, 7, VideoActions(n.Paths));
                break;
            case NoticeKind.RecordingStarted:
                if (_settings.NotifySound) Sounds.Start();
                Popup(Loc.T("N.RecordingStarted"), _settings.Hotkeys.ToggleRecording, ToastKind.Recording);
                break;
            case NoticeKind.RecordingStopped:
                if (_settings.NotifySound) Sounds.Stop();
                Popup(Loc.T("N.RecordingStopped"), Files(), ToastKind.Ok, 7, VideoActions(n.Paths));
                break;
            case NoticeKind.ScreenshotSaved:
                if (_settings.NotifySound) Sounds.Shot();
                Popup(Loc.T("N.ScreenshotSaved"), Files(), ToastKind.Ok, 4, n.Paths.Count == 0 ? null : new[]
                {
                    new ToastAction(Loc.T("Open"), "\uE8A7", () => OpenPath(n.Paths[0])),
                    new ToastAction(Loc.T("G.Copy"), "\uE8C8", () => Views.FileActions.CopyToClipboard(n.Paths)),
                    new ToastAction(Loc.T("N.Folder"), "\uE838", () => Views.FileActions.ShowInFolder(n.Paths[0])),
                });
                break;
            case NoticeKind.DiskBufferPaused:
                Notify(Loc.F("N.DiskBufferPaused", n.Detail ?? ""), Loc.F("N.DiskFree", WindowHelper.FormatBytes(n.FreeBytes)), ToastKind.Error, error: true);
                break;
            case NoticeKind.DiskBufferResumed:
                Popup(Loc.T("N.DiskBufferResumed"), Loc.F("N.DiskFree", WindowHelper.FormatBytes(n.FreeBytes)), ToastKind.Info, 4);
                break;
            case NoticeKind.DiskRecordingStopped:
                Notify(Loc.F("N.DiskRecordingStopped", n.Detail ?? ""), Loc.F("N.DiskFree", WindowHelper.FormatBytes(n.FreeBytes)), ToastKind.Error, error: true);
                break;
            case NoticeKind.DiskNoSpaceForSave:
                Notify(Loc.F("N.DiskNoSpaceForSave", n.Detail ?? ""),
                    Loc.F("N.DiskNeed", WindowHelper.FormatBytes(n.NeededBytes), WindowHelper.FormatBytes(n.FreeBytes)), ToastKind.Error, error: true);
                break;
            case NoticeKind.ReplayOn:
                Popup(Loc.T("N.ReplayOn"), "", ToastKind.Info, 2);
                break;
            case NoticeKind.ReplayOff:
                Popup(Loc.T("N.ReplayOff"), "", ToastKind.Info, 2);
                break;
            case NoticeKind.StorageWarning when n.Storage != null:
                var u = n.Storage;
                Popup(Loc.F("N.Storage", u.Percent),
                    Loc.F("N.StorageDetail", WindowHelper.FormatBytes(u.UsedBytes), WindowHelper.FormatBytes(u.LimitBytes),
                        WindowHelper.FormatBytes(u.RemainingBytes), Math.Floor(n.MinutesLeft).ToString("0")),
                    ToastKind.Warning, 6);
                break;
            case NoticeKind.Error:
                Notify(Loc.T("N.Error"), n.Detail ?? "", ToastKind.Error, error: true);
                break;
        }
        UpdateTray();
    }

    private void Popup(string title, string body, ToastKind kind, double seconds = 3.5, IReadOnlyList<ToastAction>? actions = null)
    {
        if (_settings.NotifyPopup) ToastWindow.Show(title, body, kind, seconds, actions);
    }

    private ToastAction[]? VideoActions(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var first = paths[0];
        return new[]
        {
            new ToastAction(Loc.T("Open"), "\uE768", () => OpenPath(first)),
            new ToastAction(Loc.T("G.Cut"), "\uE8C6", () => ShowEditor(first)),
            new ToastAction(Loc.T("N.Folder"), "\uE838", () => Views.FileActions.ShowInFolder(first)),
        };
    }

    /// <summary>Opens the cut editor for a file (from a toast).</summary>
    public async void ShowEditor(string path)
    {
        if (!File.Exists(path)) return;
        var entry = await Task.Run(() => DesktopReplay.Core.Library.MediaLibrary.EntryFor(path));
        var w = new Views.ClipEditorWindow(entry, edit: true);
        w.Show();
        w.Activate();
    }

    private void Notify(string title, string body, ToastKind kind, bool error)
    {
        if (error && _settings.NotifySound) Sounds.Error();
        // Errors are always shown, even with pop-ups disabled.
        ToastWindow.Show(title, body, kind, 5);
    }

    // ---------- Tray ----------

    private void CreateTray()
    {
        var info = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using (var s = info!.Stream) _iconOn = new System.Drawing.Icon(s, 32, 32);
        _iconOff = Tint(_iconOn, grayscale: true);
        _iconRec = Tint(_iconOn, grayscale: false);
        _tray = new Forms.NotifyIcon { Icon = _iconOn, Visible = true, Text = "DesktopReplay" };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowOverlay(); };
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_tray == null) return;
        var h = _settings.Hotkeys;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add($"{Loc.T("Tray.Overlay")}\t{h.Overlay}", null, (_, _) => ShowOverlay());
        var replay = new Forms.ToolStripMenuItem(Loc.T("Tray.Replay")) { CheckOnClick = false };
        replay.Click += (_, _) => ToggleReplay();
        menu.Items.Add(replay);
        menu.Items.Add($"{Loc.T("Tray.Save")}\t{h.SaveReplay}", null, (_, _) => SaveReplay());
        menu.Items.Add($"{Loc.T("Tray.Record")}\t{h.ToggleRecording}", null, (_, _) => ToggleRecording());
        menu.Items.Add($"{Loc.T("Tray.Screenshot")}\t{h.Screenshot}", null, (_, _) => Screenshot());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Folder"), null, (_, _) => OpenFolder());
        menu.Items.Add(Loc.T("Settings"), null, (_, _) => ShowOverlay(settings: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Exit"), null, (_, _) => _ = ExitAsync());
        menu.Opening += (_, _) => replay.Checked = Engine.ReplayActive;
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void UpdateTray()
    {
        if (_tray == null) return;
        if (Engine.IsRecording) { _tray.Icon = _iconRec; _tray.Text = Loc.T("Tray.TooltipRec"); }
        else if (Engine.ReplayActive) { _tray.Icon = _iconOn; _tray.Text = Loc.T("Tray.TooltipOn"); }
        else { _tray.Icon = _iconOff; _tray.Text = Loc.T("Tray.TooltipOff"); }
    }

    /// <summary>Grayscale (replay off) or red-ringed (recording) variant of the tray icon.</summary>
    private static System.Drawing.Icon Tint(System.Drawing.Icon src, bool grayscale)
    {
        using var bmp = src.ToBitmap();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (grayscale)
                {
                    int g = (int)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
                    bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(c.A, g, g, g));
                }
            }
        if (!grayscale)
        {
            using var gr = System.Drawing.Graphics.FromImage(bmp);
            gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 239, 68, 68), 3);
            gr.DrawEllipse(pen, 2, 2, bmp.Width - 5, bmp.Height - 5);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ---------- Exit ----------

    private bool _exiting;

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        _overlay?.Close2();
        if (_tray != null) _tray.Visible = false;
        try { await Engine.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception ex) { Log.Error("Shutdown", ex); }
        Engine.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Updater.ApplyOnExit();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        try { Engine.ShutdownAsync().Wait(TimeSpan.FromSeconds(8)); } catch { }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
    }

    public static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Open {path}", ex); }
    }
}
