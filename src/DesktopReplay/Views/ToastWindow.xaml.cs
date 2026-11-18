using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopReplay.Services;

namespace DesktopReplay.Views;

public enum ToastKind { Ok, Recording, Warning, Error, Info }

/// <summary>Non-activating, click-through toast in the corner of the monitor with the active window. Never captured.</summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;

    private ToastWindow(string title, string body, ToastKind kind, TimeSpan duration)
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
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
        SourceInitialized += (_, _) =>
        {
            WindowHelper.MakeToolWindow(this, clickThrough: true);
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

    private void FadeOut()
    {
        _timer.Stop();
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(250));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    public static void Show(string title, string body = "", ToastKind kind = ToastKind.Ok, double seconds = 3.5)
    {
        _current?.Close();
        _current = new ToastWindow(title, body, kind, TimeSpan.FromSeconds(seconds)) { Opacity = 0 };
        var me = _current;
        me.Closed += (_, _) => { if (_current == me) _current = null; };
        me.Show();
    }
}
