using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopReplay.Services;

namespace DesktopReplay.Views;

public enum ToastKind { Ok, Recording, Warning, Error, Info }

public sealed record ToastAction(string Label, string Glyph, Action Run);

/// <summary>
/// Toast in the corner of the monitor with the active window. Never captured and never takes focus from the game.
/// Plain toasts are click-through; toasts with action buttons are clickable and stay while hovered.
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;
    private readonly bool _interactive;

    private ToastWindow(string title, string body, ToastKind kind, TimeSpan duration, IReadOnlyList<ToastAction>? actions)
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
        _interactive = actions is { Count: > 0 };
        if (_interactive)
        {
            ActionsPanel.Visibility = Visibility.Visible;
            foreach (var a in actions!)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = a.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = a.Label, FontSize = 12 });
                var b = new Button { Content = content, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
                b.Click += (_, _) =>
                {
                    try { a.Run(); } catch (Exception ex) { DesktopReplay.Core.Log.Warn($"Toast action failed: {ex.Message}"); }
                    FadeOut();
                };
                ActionsPanel.Children.Add(b);
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

    public static void Show(string title, string body = "", ToastKind kind = ToastKind.Ok, double seconds = 3.5, IReadOnlyList<ToastAction>? actions = null)
    {
        _current?.Close();
        _current = new ToastWindow(title, body, kind, TimeSpan.FromSeconds(seconds), actions) { Opacity = 0 };
        var me = _current;
        me.Closed += (_, _) => { if (_current == me) _current = null; };
        me.Show();
    }
}
