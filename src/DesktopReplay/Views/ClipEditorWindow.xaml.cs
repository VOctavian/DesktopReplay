using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopReplay.Core;
using DesktopReplay.Core.Library;
using DesktopReplay.Services;

namespace DesktopReplay.Views;

/// <summary>Preview player and cutter: trim range, 9:16 crop, per-track audio edits, export.</summary>
public partial class ClipEditorWindow : Window
{
    private sealed class TrackRow
    {
        public required AudioStreamInfo Info;
        public required CheckBox Include;
        public required Slider Volume;
        public required RangeTimeline Range;
        public required string Title;
    }

    private readonly LibraryEntry _entry;
    private readonly bool _autoPlay;
    private readonly DispatcherTimer _tick;
    private readonly List<TrackRow> _tracks = new();
    private MediaInfo? _info;
    private double _duration, _fps = 30;
    private bool _playing, _opened, _fullscreen, _busy;
    private double _shortsX = 0.5, _shortsGrab = -1;
    private WindowState _prevState;
    private CancellationTokenSource? _exportCts;
    private AudioPreview? _audio;
    private readonly List<PreviewTrack> _preview = new();
    private DateTime _playStarted, _lastResync;

    /// <summary>True when a file was created, replaced or deleted (the gallery rescans).</summary>
    public bool FilesChanged { get; private set; }

    public ClipEditorWindow(LibraryEntry entry, bool edit)
    {
        _entry = entry;
        _autoPlay = !edit;
        InitializeComponent();
        Title = entry.Name;
        NameBox.Text = Path.GetFileNameWithoutExtension(entry.Path);
        PresetCombo.ItemsSource = new[] { Loc.T("E.PresetOriginal"), Loc.T("E.PresetDiscord"), Loc.T("E.PresetGif") };
        PresetCombo.SelectedIndex = 0;
        VolumeSlider.Value = 0.8;

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _tick.Tick += (_, _) => OnTick();

        Timeline.RangeDragging += isFrom => { Pause(); Seek(isFrom ? Timeline.From : Timeline.To); UpdateSelectionInfo(); };
        Timeline.Seeking += Scrub;
        Timeline.DragCompleted += EndScrub;

        SourceInitialized += (_, _) =>
        {
            WindowHelper.UseDarkTitleBar(this);
            WindowHelper.ExcludeFromCapture(this);
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _exportCts?.Cancel();
            _tick.Stop();
            ClosePlayer();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            _info = await Task.Run(() => MediaProbe.Probe(_entry.Path));
        }
        catch (Exception ex)
        {
            ShowError(Loc.F("E.OpenFailed", ex.Message));
            SetBusy(true);
            DeleteBtn.IsEnabled = true;
            return;
        }
        _duration = _info.Duration;
        _fps = _info.Fps;
        Timeline.Duration = _duration;
        Timeline.From = 0;
        Timeline.To = _duration;
        Timeline.Position = 0;
        BuildTracks();
        UpdateSelectionInfo();
        UpdateTime();

        // The WPF player can't choose an audio track (it plays the last one), so it stays muted
        // and AudioPreview plays the tracks itself.
        Player.IsMuted = true;
        _audio = new AudioPreview(_entry.Path, _preview) { Volume = VolumeSlider.Value };
        Player.Source = new Uri(_entry.Path);
        Player.Pause(); // opens the media and shows the first frame
        _tick.Start();

        var waves = _tracks.Count > 0 ? Task.Run(() => Waveform.Compute(_entry.Path, 1500)) : null;
        try
        {
            var strip = await Task.Run(() => MediaLibrary.Filmstrip(_entry.Path, 14, 180));
            Timeline.Strip = strip.Cast<ImageSource?>().ToList();
        }
        catch (Exception ex) { Log.Warn($"Filmstrip failed: {ex.Message}"); }
        if (waves != null)
        {
            try
            {
                var w = await waves;
                foreach (var t in _tracks) if (w.TryGetValue(t.Info.Index, out var peaks)) t.Range.Wave = peaks;
            }
            catch (Exception ex) { Log.Warn($"Waveform failed: {ex.Message}"); }
        }
    }

    // ---------- audio tracks ----------

    private void BuildTracks()
    {
        TracksPanel.Children.Clear();
        _tracks.Clear();
        _preview.Clear();
        int n = 1;
        foreach (var a in _info!.Audio)
        {
            string title = string.IsNullOrWhiteSpace(a.Title) ? Loc.F("E.Track", n) : a.Title;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var include = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("E.TrackInclude") };
            include.Content = new TextBlock { Text = $"{n}. {title}", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 };
            var range = new RangeTimeline { Compact = true, Height = 26, Duration = _duration, From = 0, To = _duration, Margin = new Thickness(4, 0, 8, 0) };
            var vol = new Slider { Minimum = 0, Maximum = 2, Value = 1, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("E.TrackVolume") };
            var volText = new TextBlock { Text = "100%", Foreground = (Brush)FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            vol.ValueChanged += (_, _) => volText.Text = $"{vol.Value * 100:0}%";
            include.Checked += (_, _) => { range.Opacity = vol.Opacity = 1; range.IsEnabled = vol.IsEnabled = true; UpdateMergeState(); };
            include.Unchecked += (_, _) => { range.Opacity = vol.Opacity = 0.35; range.IsEnabled = vol.IsEnabled = false; UpdateMergeState(); };
            range.RangeDragging += isFrom => { Pause(); Seek(isFrom ? range.From : range.To); };
            range.Seeking += Scrub;
            range.DragCompleted += EndScrub;

            Grid.SetColumn(range, 1); Grid.SetColumn(vol, 2); Grid.SetColumn(volText, 3);
            grid.Children.Add(include); grid.Children.Add(range); grid.Children.Add(vol); grid.Children.Add(volText);
            TracksPanel.Children.Add(grid);
            _tracks.Add(new TrackRow { Info = a, Include = include, Volume = vol, Range = range, Title = title });
            _preview.Add(new PreviewTrack { StreamIndex = a.Index });
            n++;
        }
        var listen = new List<string> { Loc.T("E.ListenResult") };
        listen.AddRange(_tracks.Select((t, i) => $"{i + 1}. {t.Title}"));
        ListenCombo.ItemsSource = listen;
        ListenCombo.SelectedIndex = 0;
        ListenCombo.IsEnabled = _tracks.Count > 0;
        NoAudioText.Visibility = _tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMergeState();
    }

    /// <summary>Copies the track settings into the live preview mix.</summary>
    private void UpdateAudible()
    {
        int mode = ListenCombo.SelectedIndex; // 0 = as in the saved file, k = only track k
        bool merge = MergeCheck.IsChecked == true;
        int firstIncluded = _tracks.FindIndex(t => t.Include.IsChecked == true);
        for (int i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            var p = _preview[i];
            p.Audible = mode > 0 ? i == mode - 1
                : t.Include.IsChecked == true && (merge || i == firstIncluded); // players play the first track of the file
            p.Volume = t.Volume.Value;
            bool fullRange = mode > 0;
            p.From = fullRange ? 0 : Math.Max(t.Range.From, Timeline.From);
            p.To = fullRange ? double.MaxValue : Math.Min(t.Range.To, Timeline.To);
        }
    }

    private void OnListenChanged(object sender, SelectionChangedEventArgs e) => UpdateAudible();

    private void UpdateMergeState()
    {
        int included = _tracks.Count(t => t.Include.IsChecked == true);
        MergeCheck.IsEnabled = included > 1;
        if (included <= 1) MergeCheck.IsChecked = false;
    }

    // ---------- playback ----------

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        PlayerError.Visibility = Visibility.Collapsed;
        LayoutShorts();
        if (_autoPlay) Play();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn($"Player failed for {_entry.Path}: {e.ErrorException?.Message}");
        ShowError(Loc.T("E.PlayerFailed"));
    }

    private void ShowError(string text)
    {
        PlayerError.Text = text;
        PlayerError.Visibility = Visibility.Visible;
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e) => Pause();

    private double Position => _opened ? Player.Position.TotalSeconds : Timeline.Position;

    private void Play()
    {
        if (!_opened) return;
        double pos = Position;
        if (pos < Timeline.From - 0.01 || pos >= Timeline.To - 0.02) { Seek(Timeline.From); pos = Timeline.From; }
        UpdateAudible();
        _audio?.Play(pos);
        _playStarted = _lastResync = DateTime.UtcNow;
        Player.Play();
        _playing = true;
        PlayBtn.Content = "";
    }

    private void Pause()
    {
        if (!_playing) return;
        Player.Pause();
        _audio?.Stop();
        _playing = false;
        PlayBtn.Content = "";
        Timeline.Position = Position;
        UpdateTime();
    }

    private void Seek(double t)
    {
        t = Math.Clamp(t, 0, _duration);
        if (_opened) Player.Position = TimeSpan.FromSeconds(t);
        Timeline.Position = t;
        foreach (var tr in _tracks) tr.Range.Position = t;
        UpdateTime();
    }

    private void OnTick()
    {
        if (!_playing) return;
        double pos = Position;
        if (pos >= Timeline.To - 0.001)
        {
            Pause();
            Seek(Timeline.To);
            return;
        }
        Timeline.Position = pos;
        foreach (var tr in _tracks) tr.Range.Position = pos;
        UpdateTime();
        UpdateAudible();

        // keep our audio in step with the video clock (the player starts with a variable delay)
        var now = DateTime.UtcNow;
        if (_audio != null && !_scrubbing && (now - _playStarted).TotalSeconds > 1 && (now - _lastResync).TotalSeconds > 2 &&
            Math.Abs(_audio.Position - pos) > 0.12)
        {
            _lastResync = now;
            _audio.Play(pos);
        }
    }

    private bool _scrubbing;

    /// <summary>Clicking/dragging the playhead: keeps playing; audio pauses while dragging and resumes on release.</summary>
    private void Scrub(double t)
    {
        if (_playing && !_scrubbing) { _scrubbing = true; _audio?.Stop(); }
        Seek(t);
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        if (_playing) RestartAudio(Position);
    }

    private void RestartAudio(double t)
    {
        UpdateAudible();
        _audio?.Play(t);
        _playStarted = _lastResync = DateTime.UtcNow;
    }

    /// <summary>Jumps by <paramref name="delta"/> seconds without pausing.</summary>
    private void SeekBy(double delta)
    {
        double t = Math.Clamp(Position + delta, 0, _duration);
        Seek(t);
        if (_playing)
        {
            if (t >= Timeline.To - 0.02) { Pause(); return; }
            RestartAudio(t);
        }
    }

    private void UpdateTime() =>
        TimeText.Text = $"{RangeTimeline.FormatTime(Timeline.Position)} / {RangeTimeline.FormatTime(_duration)}";

    private void OnPlayPause(object sender, RoutedEventArgs e) { if (_playing) Pause(); else Play(); }
    private void OnToStart(object sender, RoutedEventArgs e) { Pause(); Seek(Timeline.From); }
    private void OnPrevFrame(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void OnNextFrame(object sender, RoutedEventArgs e) => StepFrame(1);

    private void StepFrame(int dir)
    {
        Pause();
        double frame = 1.0 / Math.Max(1, _fps);
        // align to the frame grid so repeated steps don't drift
        double cur = Math.Round(Position / frame) * frame;
        Seek(cur + dir * frame);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audio == null) return;
        _audio.Volume = VolumeSlider.Value;
        if (_audio.Muted && VolumeSlider.Value > 0) { _audio.Muted = false; MuteBtn.Content = ""; }
    }

    private void OnMute(object sender, RoutedEventArgs e)
    {
        if (_audio == null) return;
        _audio.Muted = !_audio.Muted;
        MuteBtn.Content = _audio.Muted ? "" : "";
    }

    private void OnVideoClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
        else if (_playing) Pause(); else Play();
    }

    private void OnFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _prevState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // re-maximize so the taskbar is covered
            WindowState = WindowState.Maximized;
            EditPanel.Visibility = Footer.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _prevState;
            EditPanel.Visibility = Footer.Visibility = Visibility.Visible;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: if (_playing) Pause(); else Play(); break;
            case Key.Left or Key.Right:
            {
                int dir = e.Key == Key.Right ? 1 : -1;
                var mods = Keyboard.Modifiers;
                if ((mods & ModifierKeys.Control) != 0) StepFrame(dir);
                else SeekBy(dir * ((mods & ModifierKeys.Shift) != 0 ? 15 : 5));
                break;
            }
            case Key.Home: Pause(); Seek(Timeline.From); break;
            case Key.End: Pause(); Seek(Timeline.To); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- selection ----------

    private void UpdateSelectionInfo()
    {
        double len = Timeline.To - Timeline.From;
        SelectionText.Text = $"{RangeTimeline.FormatTime(Timeline.From)} – {RangeTimeline.FormatTime(Timeline.To)}  ·  {RangeTimeline.FormatTime(len)}";
        long est = _duration > 0 ? (long)(_entry.Size * len / _duration) : 0;
        SizeText.Text = Preset switch
        {
            ExportPreset.Discord => "≤ " + WindowHelper.FormatBytes(ClipExporter.DiscordLimit),
            // ~100 KB per second at 480 px / 15 fps; strongly depends on the scene
            ExportPreset.Gif => "≈ " + WindowHelper.FormatBytes((long)(len * (ShortsCheck.IsChecked == true ? 140_000 : 100_000))),
            _ => "≈ " + WindowHelper.FormatBytes(est),
        };
        PresetHint.Text = Preset switch
        {
            ExportPreset.Discord when len > 150 => Loc.T("E.DiscordLong"),
            ExportPreset.Discord => Loc.T("E.DiscordHint"),
            ExportPreset.Gif when len > 30 => Loc.T("E.GifLong"),
            ExportPreset.Gif => Loc.T("E.GifHint"),
            _ => "",
        };
        if (!LengthBox.IsKeyboardFocused) LengthBox.Text = len.ToString("0.#", CultureInfo.CurrentCulture);
    }

    private void ApplyLength(double n)
    {
        if (_duration <= 0) return;
        n = Math.Clamp(Math.Round(n / Timeline.Step) * Timeline.Step, Timeline.Step, _duration);
        double from = Timeline.From, to = from + n;
        if (to > _duration) { to = _duration; from = Math.Max(0, Timeline.SnapValue(to - n)); }
        Timeline.From = from;
        Timeline.To = to;
        Pause();
        Seek(from);
        UpdateSelectionInfo();
    }

    private void OnQuickLength(object sender, RoutedEventArgs e)
    {
        if ((string)((Button)sender).Tag == "all")
        {
            Timeline.From = 0;
            Timeline.To = _duration;
            Pause();
            Seek(0);
            UpdateSelectionInfo();
        }
        else ApplyLength(double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
    }

    private void OnLengthKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnLengthCommit(sender, e); e.Handled = true; }
    }

    private void OnLengthCommit(object sender, RoutedEventArgs e)
    {
        var s = LengthBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) ApplyLength(v);
        else UpdateSelectionInfo();
        if (e is KeyEventArgs) Keyboard.ClearFocus();
        UpdateSelectionInfo();
    }

    // ---------- shorts crop ----------

    private void OnShortsToggled(object sender, RoutedEventArgs e)
    {
        ShortsLayer.Visibility = ShortsCenterBtn.Visibility = ShortsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LayoutShorts();
    }

    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e) => LayoutShorts();

    private (Rect Video, double CropW) VideoGeometry()
    {
        double nw = _info?.Width ?? Player.NaturalVideoWidth, nh = _info?.Height ?? Player.NaturalVideoHeight;
        double hw = VideoHost.ActualWidth, hh = VideoHost.ActualHeight;
        if (nw <= 0 || nh <= 0 || hw <= 0 || hh <= 0) return (Rect.Empty, 0);
        double s = Math.Min(hw / nw, hh / nh);
        var r = new Rect((hw - nw * s) / 2, (hh - nh * s) / 2, nw * s, nh * s);
        return (r, Math.Min(r.Width, r.Height * 9 / 16));
    }

    private void LayoutShorts()
    {
        if (ShortsLayer.Visibility != Visibility.Visible) return;
        var (v, cw) = VideoGeometry();
        if (v.IsEmpty) return;
        double cx = v.X + (v.Width - cw) * _shortsX;
        Place(ShortsDimL, v.X, v.Y, cx - v.X, v.Height);
        Place(ShortsDimR, cx + cw, v.Y, v.Right - cx - cw, v.Height);
        Place(ShortsFrame, cx, v.Y, cw, v.Height);
        bool centered = Math.Abs(_shortsX - 0.5) < 0.0001;
        // the guide marks the video's center line while dragging; it lights up when the frame snaps to it
        Place(ShortsGuide, v.X + v.Width / 2 - 1, v.Y, 2, v.Height);
        ShortsGuide.Visibility = _shortsGrab >= 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortsGuide.Opacity = centered ? 0.95 : 0.35;
        ShortsCenterBtn.IsEnabled = !centered;
        ShortsLabel.Text = centered ? Loc.T("E.ShortsCentered") : Loc.T("E.ShortsDrag");
    }

    private void OnShortsCenter(object sender, RoutedEventArgs e)
    {
        _shortsX = 0.5;
        LayoutShorts();
    }

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }

    private void OnShortsDown(object sender, MouseButtonEventArgs e)
    {
        var (v, cw) = VideoGeometry();
        if (v.IsEmpty) return;
        double x = e.GetPosition(ShortsLayer).X;
        double cx = v.X + (v.Width - cw) * _shortsX;
        if (e.ClickCount == 2) { OnShortsCenter(sender, e); e.Handled = true; return; }
        _shortsGrab = x >= cx && x <= cx + cw ? x - cx : cw / 2;
        ShortsLayer.CaptureMouse();
        MoveShorts(x);
        e.Handled = true;
    }

    private void OnShortsMove(object sender, MouseEventArgs e)
    {
        if (_shortsGrab >= 0) MoveShorts(e.GetPosition(ShortsLayer).X);
    }

    private void OnShortsUp(object sender, MouseButtonEventArgs e)
    {
        _shortsGrab = -1;
        ShortsLayer.ReleaseMouseCapture();
        LayoutShorts();
        e.Handled = true;
    }

    private void MoveShorts(double x)
    {
        var (v, cw) = VideoGeometry();
        double free = v.Width - cw;
        _shortsX = free > 0 ? Math.Clamp((x - _shortsGrab - v.X) / free, 0, 1) : 0.5;
        // magnet: within 12 screen pixels of the center the frame snaps to it
        if (free > 0 && Math.Abs((_shortsX - 0.5) * free) < 12) _shortsX = 0.5;
        LayoutShorts();
    }

    // ---------- export ----------

    private string CleanName()
    {
        var name = NameBox.Text.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(_entry.Path) : name;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private ExportPreset Preset => (ExportPreset)Math.Max(0, PresetCombo.SelectedIndex);

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded && PresetCombo.SelectedIndex < 0) return;
        bool gif = Preset == ExportPreset.Gif;
        ExtText.Text = gif ? ".gif" : ".mp4";
        // replacing the original only makes sense for a same-quality cut
        ReplaceBtn.IsEnabled = !_busy && Preset == ExportPreset.Original;
        TracksPanel.Opacity = MergeCheck.Opacity = gif ? 0.4 : 1;
        TracksPanel.IsEnabled = MergeCheck.IsEnabled = !gif;
        if (!gif) UpdateMergeState();
        if (_duration > 0) UpdateSelectionInfo();
    }

    private ExportRequest BuildRequest(string output) => new(
        _entry.Path, output, Timeline.From, Timeline.To,
        ShortsCheck.IsChecked == true, _shortsX,
        _tracks.Select(t => new AudioTrackEdit(t.Info.Index, t.Title, t.Include.IsChecked == true, t.Volume.Value, t.Range.From, t.Range.To)).ToList(),
        MergeCheck.IsChecked == true, Loc.T("E.MergedTitle"), Preset, ClipExporter.DiscordLimit);

    private string OutputExt => Preset == ExportPreset.Gif ? ".gif" : ".mp4";

    private async Task<string?> ExportToTempAsync(string name)
    {
        var dir = Path.GetDirectoryName(_entry.Path)!;
        var temp = Path.Combine(dir, name + MediaLibrary.TempMarker + OutputExt);
        return await RunExportAsync(BuildRequest(temp), Loc.T("E.Exporting"), p => Loc.F("E.ExportingPct", p));
    }

    /// <summary>"Discord" button: the fragment with the current edits, sized for Discord, into %TEMP% and onto the clipboard.</summary>
    private async void OnDiscordQuick(object sender, RoutedEventArgs e)
    {
        var output = DiscordShare.NewTempPath(_entry.Path);
        var request = BuildRequest(output) with { Preset = ExportPreset.Discord, TargetBytes = ClipExporter.DiscordLimit };
        var done = await RunExportAsync(request, Loc.F("D.Working", 0), p => Loc.F("D.Working", p));
        if (done == null) return;
        try
        {
            _lastSaved = done;
            StatusText.Text = DiscordShare.Finish(done);
            CopyResultBtn.Visibility = FolderResultBtn.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { StatusText.Text = Loc.F("D.Failed", ex.Message); }
    }

    private async Task<string?> RunExportAsync(ExportRequest request, string startText, Func<int, string> progressText)
    {
        var temp = request.Output;
        Pause();
        SetBusy(true);
        _exportCts = new CancellationTokenSource();
        Progress.Value = 0;
        Progress.Visibility = CancelBtn.Visibility = Visibility.Visible;
        StatusText.Text = startText;
        try
        {
            var progress = new Progress<double>(p => { Progress.Value = p; StatusText.Text = progressText((int)(p * 100)); });
            await ClipExporter.ExportAsync(request, progress, _exportCts.Token);
            return temp;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("E.Cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            StatusText.Text = Loc.F("E.ExportFailed", ex.Message);
            return null;
        }
        finally
        {
            Progress.Visibility = CancelBtn.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        EditPanel.IsEnabled = !busy;
        Timeline.IsEnabled = !busy;
        DeleteBtn.IsEnabled = SaveCopyBtn.IsEnabled = DiscordBtn.IsEnabled = !busy;
        ReplaceBtn.IsEnabled = !busy && Preset == ExportPreset.Original;
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private string? _lastSaved;
    private void OnCopyResult(object sender, RoutedEventArgs e)
    {
        if (_lastSaved != null && FileActions.CopyToClipboard(new[] { _lastSaved })) StatusText.Text = Loc.F("G.Copied", 1);
    }
    private void OnFolderResult(object sender, RoutedEventArgs e) { if (_lastSaved != null) FileActions.ShowInFolder(_lastSaved); }

    private async void OnSaveCopy(object sender, RoutedEventArgs e)
    {
        var name = CleanName();
        bool same = string.Equals(name, Path.GetFileNameWithoutExtension(_entry.Path), StringComparison.OrdinalIgnoreCase);
        if (same) name += Preset switch { ExportPreset.Discord => "_discord", ExportPreset.Gif => "", _ => "_cut" };
        var temp = await ExportToTempAsync(name);
        if (temp == null) return;
        try
        {
            var target = Unique(Path.Combine(Path.GetDirectoryName(_entry.Path)!, name + OutputExt));
            File.Move(temp, target);
            FilesChanged = true;
            _lastSaved = target;
            StatusText.Text = Loc.F("E.Saved", Path.GetFileName(target)) + $" · {WindowHelper.FormatBytes(new FileInfo(target).Length)}";
            CopyResultBtn.Visibility = FolderResultBtn.Visibility = Visibility.Visible;
            Log.Info($"Gallery: saved cut {target}");
        }
        catch (Exception ex)
        {
            Log.Error("Save copy failed", ex);
            StatusText.Text = Loc.F("E.ExportFailed", ex.Message);
            TryDelete(temp);
        }
    }

    private async void OnReplace(object sender, RoutedEventArgs e)
    {
        var name = CleanName();
        var temp = await ExportToTempAsync(name);
        if (temp == null) return;
        try
        {
            var created = File.GetCreationTime(_entry.Path);
            var modified = File.GetLastWriteTime(_entry.Path);
            ClosePlayer();
            RecycleWithRetry(_entry.Path); // the original goes to the Recycle Bin, not gone for good
            var target = Path.Combine(Path.GetDirectoryName(_entry.Path)!, name + ".mp4");
            if (File.Exists(target)) target = Unique(target);
            File.Move(temp, target);
            File.SetCreationTime(target, created);
            File.SetLastWriteTime(target, modified);
            FilesChanged = true;
            Log.Info($"Gallery: replaced {_entry.Path} -> {target}");
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Replace failed", ex);
            StatusText.Text = Loc.F("E.ExportFailed", ex.Message);
            if (File.Exists(_entry.Path)) TryDelete(temp);
            else StatusText.Text = Loc.F("E.KeptTemp", Path.GetFileName(temp));
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, Loc.F("G.DeleteConfirm", Path.GetFileName(_entry.Path)), Loc.T("G.Delete"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            ClosePlayer();
            RecycleWithRetry(_entry.Path);
            FilesChanged = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Delete failed", ex);
            StatusText.Text = ex.Message;
        }
    }

    private void ClosePlayer()
    {
        _audio?.Dispose();
        _playing = false;
        _opened = false;
        try
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        catch { }
    }

    private static void RecycleWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { FileActions.Recycle(path); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(150); }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
