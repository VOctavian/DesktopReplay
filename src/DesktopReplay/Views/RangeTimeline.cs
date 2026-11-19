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
    private enum Drag { None, From, To, Head, Move }
    private Drag _drag;
    private double _moveGrab; // seconds between the grab point and "from"
    private double _moveLen;

    private double _duration, _from, _to, _position = -1;
    private IList<ImageSource?>? _strip;
    private float[]? _wave;
    private static readonly Brush WaveBrush = Freeze(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public double Step { get; set; } = 0.5;
    public bool Compact { get; set; }
    public Brush Accent { get; set; } = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    public Brush TrackFill { get; set; } = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x39));

    public double Duration { get => _duration; set { _duration = Math.Max(0, value); InvalidateVisual(); } }
    public double From { get => _from; set { _from = value; InvalidateVisual(); } }
    public double To { get => _to; set { _to = value; InvalidateVisual(); } }
    public double Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public IList<ImageSource?>? Strip { get => _strip; set { _strip = value; InvalidateVisual(); } }
    /// <summary>Peak envelope (0..1) over the whole duration, drawn as a waveform.</summary>
    public float[]? Wave { get => _wave; set { _wave = value; _waveMax = value is { Length: > 0 } ? value.Max() : 0; InvalidateVisual(); } }
    private float _waveMax;

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
        if (_wave is { Length: > 0 } wave) DrawWave(dc, wave, top, bh);
        if (_duration <= 0) return;

        double xf = X(_from), xt = X(_to);
        var dim = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
        if (xf > HandleW) dc.DrawRectangle(dim, null, new Rect(HandleW, top, xf - HandleW, bh));
        if (xt < HandleW + Inner) dc.DrawRectangle(dim, null, new Rect(xt, top, HandleW + Inner - xt, bh));
        if (Compact && _strip == null)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(_wave != null ? 45 : 90), 0xEF, 0x44, 0x44)), null, new Rect(xf, top, Math.Max(0, xt - xf), bh));

        // selection frame
        var pen = new Pen(Accent, Compact ? 2 : 3);
        dc.DrawLine(pen, new Point(xf, top + pen.Thickness / 2), new Point(xt, top + pen.Thickness / 2));
        dc.DrawLine(pen, new Point(xf, top + bh - pen.Thickness / 2), new Point(xt, top + bh - pen.Thickness / 2));

        // handles: brackets outside the selection
        DrawHandle(dc, new Rect(xf - HandleW, top, HandleW, bh), true);
        DrawHandle(dc, new Rect(xt, top, HandleW, bh), false);

        // grip in the middle of the selection: drag it to move the whole fragment
        if (GripRect() is { } gr)
        {
            dc.DrawRoundedRectangle(Accent, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), gr, gr.Height / 2, gr.Height / 2);
            var dot = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            double cy = gr.Y + gr.Height / 2;
            for (int i = -1; i <= 1; i++) dc.DrawEllipse(dot, null, new Point(gr.X + gr.Width / 2 + i * 5, cy), 1.4, 1.4);
        }

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

    private void DrawWave(DrawingContext dc, float[] wave, double top, double bh)
    {
        const double col = 2; // px per bar
        int cols = (int)(Inner / col);
        if (cols <= 0) return;
        double mid = top + bh / 2, half = bh / 2 - 2;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int c = 0; c < cols; c++)
            {
                int b0 = (int)((long)c * wave.Length / cols), b1 = Math.Max(b0 + 1, (int)((long)(c + 1) * wave.Length / cols));
                float peak = 0;
                for (int b = b0; b < b1 && b < wave.Length; b++) peak = Math.Max(peak, wave[b]);
                // normalize to the loudest moment (but not below -34 dBFS, so silence stays flat);
                // the square root keeps quiet speech visible next to loud shots
                double h = Math.Max(0.5, Math.Sqrt(Math.Min(1, peak / Math.Max(_waveMax, 0.02f))) * half);
                double x = HandleW + c * col;
                g.BeginFigure(new Point(x, mid - h), true, true);
                g.LineTo(new Point(x + col - 0.6, mid - h), false, false);
                g.LineTo(new Point(x + col - 0.6, mid + h), false, false);
                g.LineTo(new Point(x, mid + h), false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(WaveBrush, null, geo);
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

    /// <summary>The move grip, or null when the selection is too narrow (or covers the whole clip).</summary>
    private Rect? GripRect()
    {
        if (_duration <= 0) return null;
        double xf = X(_from), xt = X(_to);
        double w = Compact ? 26 : 34, h = Compact ? 10 : 14;
        if (xt - xf < w + 10) return null;
        if (_from <= 0.0001 && _to >= _duration - 0.0001) return null;
        double y = Compact ? (ActualHeight - h) / 2 : ActualHeight - h - 4;
        return new Rect((xf + xt) / 2 - w / 2, y, w, h);
    }

    private Drag HitTest(Point p)
    {
        if (GripRect() is { } gr)
        {
            var hit = gr; hit.Inflate(6, 6);
            if (hit.Contains(p)) return Drag.Move;
        }
        return HitTest(p.X);
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
            var hit = _duration > 0 ? HitTest(e.GetPosition(this)) : Drag.Head;
            Cursor = hit == Drag.Move ? Cursors.SizeAll : hit != Drag.Head ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        Apply(x);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_duration <= 0) return;
        double x = e.GetPosition(this).X;
        _drag = HitTest(e.GetPosition(this));
        if (_drag == Drag.Move) { _moveGrab = T(x) - _from; _moveLen = _to - _from; }
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
            case Drag.Move:
                double f = Math.Clamp(Math.Round((t - _moveGrab) / Step) * Step, 0, Math.Max(0, _duration - _moveLen));
                if (f + _moveLen > _duration - Step / 4) f = Math.Max(0, _duration - _moveLen); // stick to the end
                _from = f;
                _to = Math.Min(_duration, f + _moveLen);
                InvalidateVisual();
                RangeDragging?.Invoke(true);
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
