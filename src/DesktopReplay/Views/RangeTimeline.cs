using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopReplay.Views;

/// <summary>
/// Timeline with a thumbnail strip, two trim handles ("from"/"to") and a playhead.
/// Handles snap to <see cref="Step"/> seconds (except the very end of the clip).
/// </summary>
public sealed class RangeTimeline : FrameworkElement
{
    private const double HandleW = 12;
    private enum Drag { None, From, To, Head }
    private Drag _drag;

    private double _duration, _from, _to, _position = -1;
    private IList<ImageSource?>? _strip;

    public double Step { get; set; } = 0.5;
    public bool Compact { get; set; }
    public Brush Accent { get; set; } = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    public Brush TrackFill { get; set; } = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x39));

    public double Duration { get => _duration; set { _duration = Math.Max(0, value); InvalidateVisual(); } }
    public double From { get => _from; set { _from = value; InvalidateVisual(); } }
    public double To { get => _to; set { _to = value; InvalidateVisual(); } }
    public double Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public IList<ImageSource?>? Strip { get => _strip; set { _strip = value; InvalidateVisual(); } }

    /// <summary>A handle moved (true = "from"); the value is already snapped.</summary>
    public event Action<bool>? RangeDragging;
    public event Action? DragCompleted;
    /// <summary>User clicked/dragged the playhead to a position.</summary>
    public event Action<double>? Seeking;

    public RangeTimeline()
    {
        Focusable = false;
        Cursor = Cursors.Hand;
    }

    private double Inner => Math.Max(1, ActualWidth - 2 * HandleW);
    private double X(double t) => HandleW + (_duration > 0 ? t / _duration : 0) * Inner;
    private double T(double x) => Math.Clamp((x - HandleW) / Inner, 0, 1) * _duration;

    public double SnapValue(double t)
    {
        if (_duration <= 0) return 0;
        if (t >= _duration - Step / 4) return _duration;
        return Math.Clamp(Math.Round(t / Step) * Step, 0, _duration);
    }

    private double MinGap => Math.Min(Step, _duration);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double top = Compact ? 2 : 0, bh = h - 2 * top;
        var area = new Rect(HandleW, top, Inner, bh);
        dc.DrawRoundedRectangle(TrackFill, null, area, 4, 4);

        if (_strip is { Count: > 0 } strip)
        {
            dc.PushClip(new RectangleGeometry(area, 4, 4));
            double cw = Inner / strip.Count;
            for (int i = 0; i < strip.Count; i++)
            {
                var img = strip[i];
                if (img == null) continue;
                var cell = new Rect(HandleW + i * cw, top, cw + 0.5, bh);
                // UniformToFill inside the cell
                double s = Math.Max(cell.Width / img.Width, cell.Height / img.Height);
                double iw = img.Width * s, ih = img.Height * s;
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawImage(img, new Rect(cell.X + (cell.Width - iw) / 2, cell.Y + (cell.Height - ih) / 2, iw, ih));
                dc.Pop();
            }
            dc.Pop();
        }
        if (_duration <= 0) return;

        double xf = X(_from), xt = X(_to);
        var dim = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
        if (xf > HandleW) dc.DrawRectangle(dim, null, new Rect(HandleW, top, xf - HandleW, bh));
        if (xt < HandleW + Inner) dc.DrawRectangle(dim, null, new Rect(xt, top, HandleW + Inner - xt, bh));
        if (Compact && _strip == null)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0xEF, 0x44, 0x44)), null, new Rect(xf, top, Math.Max(0, xt - xf), bh));

        // selection frame
        var pen = new Pen(Accent, Compact ? 2 : 3);
        dc.DrawLine(pen, new Point(xf, top + pen.Thickness / 2), new Point(xt, top + pen.Thickness / 2));
        dc.DrawLine(pen, new Point(xf, top + bh - pen.Thickness / 2), new Point(xt, top + bh - pen.Thickness / 2));

        // handles: brackets outside the selection
        DrawHandle(dc, new Rect(xf - HandleW, top, HandleW, bh), true);
        DrawHandle(dc, new Rect(xt, top, HandleW, bh), false);

        if (_position >= 0 && _position <= _duration + 0.001)
        {
            double xp = X(Math.Min(_position, _duration));
            dc.DrawRectangle(Brushes.White, null, new Rect(xp - 1, 0, 2, h));
            if (!Compact)
            {
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(xp - 6, 0), true, true);
                    g.LineTo(new Point(xp + 6, 0), false, false);
                    g.LineTo(new Point(xp, 7), false, false);
                }
                tri.Freeze();
                dc.DrawGeometry(Brushes.White, null, tri);
            }
        }
    }

    private void DrawHandle(DrawingContext dc, Rect r, bool left)
    {
        var geo = new StreamGeometry();
        double rad = 4;
        using (var g = geo.Open())
        {
            if (left)
            {
                g.BeginFigure(new Point(r.Right, r.Top), true, true);
                g.LineTo(new Point(r.Left + rad, r.Top), false, false);
                g.ArcTo(new Point(r.Left, r.Top + rad), new Size(rad, rad), 0, false, SweepDirection.Counterclockwise, false, false);
                g.LineTo(new Point(r.Left, r.Bottom - rad), false, false);
                g.ArcTo(new Point(r.Left + rad, r.Bottom), new Size(rad, rad), 0, false, SweepDirection.Counterclockwise, false, false);
                g.LineTo(new Point(r.Right, r.Bottom), false, false);
            }
            else
            {
                g.BeginFigure(new Point(r.Left, r.Top), true, true);
                g.LineTo(new Point(r.Right - rad, r.Top), false, false);
                g.ArcTo(new Point(r.Right, r.Top + rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise, false, false);
                g.LineTo(new Point(r.Right, r.Bottom - rad), false, false);
                g.ArcTo(new Point(r.Right - rad, r.Bottom), new Size(rad, rad), 0, false, SweepDirection.Clockwise, false, false);
                g.LineTo(new Point(r.Left, r.Bottom), false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(Accent, null, geo);
        var grip = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5);
        double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, gh = Math.Min(10, r.Height / 4);
        dc.DrawLine(grip, new Point(cx - 1.5, cy - gh), new Point(cx - 1.5, cy + gh));
        dc.DrawLine(grip, new Point(cx + 1.5, cy - gh), new Point(cx + 1.5, cy + gh));
    }

    private Drag HitTest(double x)
    {
        double xf = X(_from), xt = X(_to);
        bool onFrom = x >= xf - HandleW - 3 && x <= xf + 3;
        bool onTo = x >= xt - 3 && x <= xt + HandleW + 3;
        if (onFrom && onTo) return x < (xf + xt) / 2 ? Drag.From : Drag.To;
        return onFrom ? Drag.From : onTo ? Drag.To : Drag.Head;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            Cursor = _duration > 0 && HitTest(x) != Drag.Head ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        Apply(x);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_duration <= 0) return;
        double x = e.GetPosition(this).X;
        _drag = HitTest(x);
        CaptureMouse();
        Apply(x);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        ReleaseMouseCapture();
        DragCompleted?.Invoke();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != Drag.None) { _drag = Drag.None; DragCompleted?.Invoke(); }
    }

    private void Apply(double x)
    {
        double t = T(x);
        switch (_drag)
        {
            case Drag.From:
                _from = Math.Clamp(SnapValue(t), 0, Math.Max(0, _to - MinGap));
                InvalidateVisual();
                RangeDragging?.Invoke(true);
                break;
            case Drag.To:
                _to = Math.Clamp(SnapValue(t), Math.Min(_duration, _from + MinGap), _duration);
                InvalidateVisual();
                RangeDragging?.Invoke(false);
                break;
            case Drag.Head:
                Seeking?.Invoke(t);
                break;
        }
    }

    public static string FormatTime(double s)
    {
        if (s < 0) s = 0;
        long tenths = (long)Math.Floor(s * 10 + 1e-6);
        var t = TimeSpan.FromSeconds(tenths / 10);
        return (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")) + "." + (tenths % 10);
    }
}

/// <summary>Wraps tiles into as many equal columns as fit at roughly <see cref="TileWidth"/> each.</summary>
public sealed class AdaptiveGrid : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }

    private int Columns(double width) => Math.Max(1, (int)Math.Floor(width / Math.Max(80, TileWidth)));

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? TileWidth * 4 : available.Width;
        int cols = Columns(width);
        double cw = width / cols, total = 0, row = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            c.Measure(new Size(cw, double.PositiveInfinity));
            row = Math.Max(row, c.DesiredSize.Height);
            if (i % cols == cols - 1 || i == InternalChildren.Count - 1) { total += row; row = 0; }
        }
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double cw = final.Width / cols, y = 0;
        for (int start = 0; start < InternalChildren.Count; start += cols)
        {
            double row = 0;
            for (int i = start; i < Math.Min(start + cols, InternalChildren.Count); i++) row = Math.Max(row, InternalChildren[i].DesiredSize.Height);
            for (int i = start; i < Math.Min(start + cols, InternalChildren.Count); i++)
                InternalChildren[i].Arrange(new Rect((i - start) * cw, y, cw, row));
            y += row;
        }
        return final;
    }
}

/// <summary>Gives its child a 16:9 height for whatever width it gets.</summary>
public sealed class AspectBox : System.Windows.Controls.Decorator
{
    public double Ratio { get; set; } = 16.0 / 9.0;

    protected override Size MeasureOverride(Size constraint)
    {
        double w = double.IsInfinity(constraint.Width) ? 320 : constraint.Width;
        var s = new Size(w, w / Ratio);
        Child?.Measure(s);
        return s;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(new Size(arrangeSize.Width, arrangeSize.Width / Ratio)));
        return new Size(arrangeSize.Width, arrangeSize.Width / Ratio);
    }
}
