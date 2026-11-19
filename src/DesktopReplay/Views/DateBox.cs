using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DesktopReplay.Views;

/// <summary>Dark-themed date input: typed date (culture short format) plus a month popup.</summary>
public sealed class DateBox : UserControl
{
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private readonly TextBox _text;
    private readonly TextBlock _placeholder;
    private readonly Popup _popup;
    private readonly TextBlock _monthTitle;
    private readonly UniformGrid _days;
    private readonly TextBlock[] _weekdays = new TextBlock[7];
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime? _selected;
    private bool _suppressText;

    public event EventHandler? SelectedDateChanged;

    public DateTime? SelectedDate
    {
        get => _selected;
        set
        {
            var v = value?.Date;
            if (v == _selected) { ShowText(); return; }
            _selected = v;
            if (v != null) _month = new DateTime(v.Value.Year, v.Value.Month, 1);
            ShowText();
            if (_popup.IsOpen) BuildMonth();
            SelectedDateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static CultureInfo Culture => CultureInfo.GetCultureInfo(Loc.I.Language == "en" ? "en-US" : "ru-RU");
    private Brush Res(string key) => (Brush)FindResource(key);

    public DateBox()
    {
        Width = 138;
        var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Height = 32 };
        border.SetResourceReference(Border.BackgroundProperty, "Panel2");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _text = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        _text.Template = PlainTextBoxTemplate();
        _text.SetResourceReference(ForegroundProperty, "Text");
        _text.SetResourceReference(TextBox.CaretBrushProperty, "Text");
        _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } };
        _text.LostKeyboardFocus += (_, _) => CommitText();
        _text.TextChanged += (_, _) => _placeholder!.Visibility = _text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, FontSize = 12 };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var btn = new Button { Content = "", ToolTip = Loc.T("G.PickDate"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 2, 0) };
        btn.SetResourceReference(StyleProperty, "IconButton");
        btn.FontSize = 14;
        btn.Click += (_, _) => Toggle();
        Grid.SetColumn(btn, 1);
        grid.Children.Add(_text);
        grid.Children.Add(_placeholder);
        grid.Children.Add(btn);
        border.Child = grid;

        // ---- popup ----
        _monthTitle = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var prev = NavButton("", -1);
        var next = NavButton("", 1);
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_monthTitle, 1); Grid.SetColumn(next, 2);
        header.Children.Add(prev); header.Children.Add(_monthTitle); header.Children.Add(next);

        var week = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        for (int i = 0; i < 7; i++)
        {
            _weekdays[i] = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            _weekdays[i].SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            week.Children.Add(_weekdays[i]);
        }
        _days = new UniformGrid { Columns = 7, Rows = 6 };

        var today = FooterButton(Loc.T("G.Today"), () => { SelectedDate = DateTime.Today; _popup!.IsOpen = false; });
        var clear = FooterButton(Loc.T("G.Clear"), () => { SelectedDate = null; _popup!.IsOpen = false; });
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(today); footer.Children.Add(clear);

        var panel = new StackPanel { Width = 252 };
        panel.Children.Add(header); panel.Children.Add(week); panel.Children.Add(_days); panel.Children.Add(footer);
        var card = new Border
        {
            Child = panel, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(8),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _popup = new Popup
        {
            Child = card, PlacementTarget = border, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = -4, HorizontalOffset = -8,
        };
        _popup.Opened += (_, _) => BuildMonth();

        var root = new Grid();
        root.Children.Add(border);
        root.Children.Add(_popup);
        Content = root;
        // follow language switches while open (subscribed only while on screen, so closed windows are not kept alive)
        System.ComponentModel.PropertyChangedEventHandler onLang = (_, _) => Dispatcher.BeginInvoke(ShowText);
        Loaded += (_, _) => { ShowText(); Loc.I.PropertyChanged += onLang; };
        Unloaded += (_, _) => Loc.I.PropertyChanged -= onLang;
    }

    private static ControlTemplate PlainTextBoxTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(MarginProperty, new Thickness(0));
        sv.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        t.VisualTree = sv;
        return t;
    }

    private Button NavButton(string glyph, int dir)
    {
        var b = new Button { Content = glyph, Padding = new Thickness(8, 4, 8, 4) };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.FontSize = 12;
        b.Click += (_, _) => { _month = _month.AddMonths(dir); BuildMonth(); };
        return b;
    }

    private Button FooterButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
        b.SetResourceReference(StyleProperty, "GhostButton");
        b.Click += (_, _) => click();
        return b;
    }

    private void Toggle()
    {
        if (_popup.IsOpen) { _popup.IsOpen = false; return; }
        CommitText();
        _month = new DateTime((_selected ?? DateTime.Today).Year, (_selected ?? DateTime.Today).Month, 1);
        _popup.IsOpen = true;
    }

    private void BuildMonth()
    {
        var c = Culture;
        var name = c.DateTimeFormat.MonthNames[_month.Month - 1];
        _monthTitle.Text = $"{char.ToUpper(name[0], c)}{name[1..]} {_month.Year}";
        var first = (int)c.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++) _weekdays[i].Text = c.DateTimeFormat.AbbreviatedDayNames[(first + i) % 7];

        _days.Children.Clear();
        int offset = ((int)_month.DayOfWeek - first + 7) % 7;
        var start = _month.AddDays(-offset);
        for (int i = 0; i < 42; i++) _days.Children.Add(DayCell(start.AddDays(i)));
    }

    private UIElement DayCell(DateTime d)
    {
        bool inMonth = d.Month == _month.Month, isToday = d == DateTime.Today, isSel = d == _selected;
        var text = new TextBlock { Text = d.Day.ToString(), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Foreground = isSel ? Brushes.White : Res(inMonth ? "Text" : "Muted");
        if (!inMonth && !isSel) text.Opacity = 0.55;
        if (isSel) text.FontWeight = FontWeights.SemiBold;
        var cell = new Border
        {
            Child = text, Height = 32, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
            Background = isSel ? Res("Accent") : Brushes.Transparent,
            BorderBrush = isToday && !isSel ? Res("Accent") : Brushes.Transparent, BorderThickness = new Thickness(1),
        };
        if (!isSel)
        {
            cell.MouseEnter += (_, _) => cell.Background = Res("Hover");
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        }
        cell.MouseLeftButtonUp += (_, _) => { SelectedDate = d; _popup.IsOpen = false; };
        return cell;
    }

    private void ShowText()
    {
        _suppressText = true;
        _text.Text = _selected?.ToString("d", Culture) ?? "";
        _placeholder.Text = Culture.DateTimeFormat.ShortDatePattern.ToLowerInvariant()
            .Replace("d", Loc.I.Language == "en" ? "d" : "д").Replace("m", Loc.I.Language == "en" ? "m" : "м").Replace("y", Loc.I.Language == "en" ? "y" : "г");
        _suppressText = false;
    }

    private void CommitText()
    {
        if (_suppressText) return;
        var s = _text.Text.Trim();
        if (s.Length == 0) { SelectedDate = null; return; }
        if (DateTime.TryParse(s, Culture, DateTimeStyles.None, out var d)) SelectedDate = d;
        else ShowText(); // revert bad input
    }
}
