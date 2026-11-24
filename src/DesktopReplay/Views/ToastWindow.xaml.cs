using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopReplay.Services;

namespace DesktopReplay.Views;

public enum ToastKind { Ok, Recording, Warning, Error, Info }

/// <param name="Close">Close the toast after running (false when the action shows a new toast itself).</param>
public sealed record ToastAction(string Label, string Glyph, Action Run, string? Tip = null, bool Danger = false, bool Close = true);

/// <summary>A labelled row of actions (one per monitor).</summary>
public sealed record ToastRow(string Label, IReadOnlyList<ToastAction> Actions);

/// <summary>
/// Toast in the corner of the monitor with the active window. Never captured and never takes focus from the game.
/// Plain toasts are click-through; toasts with action buttons are clickable and stay while hovered.
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;
    private readonly bool _interactive;

    private ToastWindow(string title, string body, ToastKind kind, TimeSpan duration, IReadOnlyList<ToastAction>? actions, IReadOnlyList<ToastRow>? rows)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        BodyText.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
        Dot.Fill = (Brush)FindResource(kind switch
        {
            ToastKind.Recording => "Accent",
            ToastKind.Warning => "Warn",
            ToastKind.Error => "Accent",
            ToastKind.Info => "Info",
            _ => "Ok",
        });
        _interactive = actions is { Count: > 0 } || rows is { Count: > 0 };
        if (_interactive)
        {
            Card.MaxWidth = 620; // room for a full row of buttons
            if (rows is { Count: > 0 })
            {
                Card.MaxWidth = 620;
                RowsPanel.Visibility = Visibility.Visible;
                foreach (var r in rows)
                {
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "label" });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    var label = new TextBlock { Text = r.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    var buttons = new WrapPanel();
                    foreach (var a in r.Actions) buttons.Children.Add(MakeButton(a));
                    Grid.SetColumn(buttons, 1);
                    g.Children.Add(label);
                    g.Children.Add(buttons);
                    RowsPanel.Children.Add(g);
                }
            }
            if (actions is { Count: > 0 })
            {
                ActionsPanel.Visibility = Visibility.Visible;
                if (rows is { Count: > 0 }) ActionsPanel.Margin = new Thickness(0, 4, 0, 0);
                foreach (var a in actions) ActionsPanel.Children.Add(MakeButton(a));
            }
            // stay while the pointer is over the toast
            MouseEnter += (_, _) => _timer!.Stop();
            MouseLeave += (_, _) => _timer!.Start();
        }
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
        SourceInitialized += (_, _) =>
        {
            WindowHelper.MakeToolWindow(this, clickThrough: !_interactive, noActivate: true);
            WindowHelper.ExcludeFromCapture(this);
        };
        Loaded += (_, _) => Position();
    }

    private Button MakeButton(ToastAction a)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        bool iconOnly = string.IsNullOrEmpty(a.Label);
        var glyph = new TextBlock { Text = a.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, iconOnly ? 0 : 6, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(glyph);
        if (!iconOnly) content.Children.Add(new TextBlock { Text = a.Label, FontSize = 12 });
        if (a.Danger)
            foreach (TextBlock t in content.Children) t.Foreground = (Brush)FindResource("Accent"); // the implicit TextBlock style would override the button's
        var b = new Button { Content = content, Padding = new Thickness(iconOnly ? 8 : 10, 5, iconOnly ? 8 : 10, 5), Margin = new Thickness(0, 0, 6, 0), ToolTip = a.Tip };
        b.Click += (_, _) =>
        {
            try { a.Run(); } catch (Exception ex) { DesktopReplay.Core.Log.Warn($"Toast action failed: {ex.Message}"); }
            if (a.Close) FadeOut();
        };
        return b;
    }

    private void Position()
    {
        var (_, work) = WindowHelper.ForegroundMonitor();
        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        int w = (int)(ActualWidth * sx), h = (int)(ActualHeight * sy);
        WindowHelper.PlacePhysical(this, new WindowHelper.RECT { Left = work.Right - w - 8, Top = work.Bottom - h - 8, Right = work.Right - 8, Bottom = work.Bottom - 8 });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        _timer.Start();
    }

    private bool _closing;

    private void FadeOut()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(250));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    /// <summary>The toast currently on screen (null when none).</summary>
    public static ToastWindow? Current => _current;

    /// <summary>Changes the title in place (progress, "copied") and restarts the timer if the toast is not hovered.</summary>
    public void SetTitle(string title)
    {
        if (_closing) return;
        TitleText.Text = title;
        if (!IsMouseOver) { _timer.Stop(); _timer.Start(); }
    }

    public static ToastWindow Show(string title, string body = "", ToastKind kind = ToastKind.Ok, double seconds = 3.5,
        IReadOnlyList<ToastAction>? actions = null, IReadOnlyList<ToastRow>? rows = null)
    {
        if (_current != null) { _current._closing = true; _current.Close(); }
        _current = new ToastWindow(title, body, kind, TimeSpan.FromSeconds(seconds), actions, rows) { Opacity = 0 };
        var me = _current;
        me.Closed += (_, _) => { if (_current == me) _current = null; };
        me.Show();
        return me;
    }

    public bool IsOpen => !_closing && IsLoaded;
}
