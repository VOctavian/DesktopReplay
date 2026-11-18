using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopReplay.Core.Settings;

namespace DesktopReplay.Views;

/// <summary>
/// Read-only text box that records a key combination when focused.
/// SingleKey mode (push-to-talk) accepts one key or a mouse button without modifiers.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool SingleKey { get; set; }

    /// <summary>Raised when editing starts/ends so global hotkeys can be suspended.</summary>
    public static event Action<bool>? EditingChanged;

    private bool _editing;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox)); // use the dark TextBox theme
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        MinWidth = 180;
        Loaded += (_, _) => Refresh();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _editing = true;
        EditingChanged?.Invoke(true);
        Text = Loc.T("H.PressKeys");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _editing = false;
        EditingChanged?.Invoke(false);
        Refresh();
    }

    private void Refresh()
    {
        if (!_editing) Text = string.IsNullOrEmpty(Hotkey) ? "—" : Hotkey;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Finish(); return; }
        if (key is Key.Back or Key.Delete) { Hotkey = ""; Finish(); return; }
        if (key == Key.Tab) { e.Handled = false; return; }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (SingleKey)
        {
            Hotkey = new Hotkey(HotkeyModifiers.None, vk).ToString();
            Finish();
            return;
        }
        if (KeyNames.IsModifierVk(vk) || key is Key.LWin or Key.RWin)
        {
            Text = Describe(Keyboard.Modifiers) + "…";
            return;
        }
        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        // Plain letters/digits would fire while typing in games — require a modifier for them.
        bool isFunctionKey = vk is >= 0x70 and <= 0x87 or 0x13 or 0x91 or 0x2C or >= 0x60 and <= 0x6F;
        if (mods == HotkeyModifiers.None && !isFunctionKey) return;
        Hotkey = new Hotkey(mods, vk).ToString();
        Finish();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_editing && SingleKey && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2 or MouseButton.Middle)
        {
            int vk = e.ChangedButton switch { MouseButton.XButton1 => 0x05, MouseButton.XButton2 => 0x06, _ => 0x04 };
            Hotkey = new Hotkey(HotkeyModifiers.None, vk).ToString();
            e.Handled = true;
            Finish();
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    private static string Describe(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    private void Finish()
    {
        _editing = false;
        Refresh();
        Keyboard.ClearFocus();
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        EditingChanged?.Invoke(false);
    }
}
