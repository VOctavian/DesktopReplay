using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopReplay.Core.Settings;

namespace DesktopReplay.Views;

/// <summary>
/// Focusable field that records a key combination. Click it, then press the keys.
/// SingleKey mode (push-to-talk) accepts one key or a mouse button without modifiers.
/// </summary>
public sealed class HotkeyBox : ContentControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    /// <summary>True while waiting for keys (used by the template to highlight the field).</summary>
    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        private set => SetValue(IsEditingProperty, value);
    }

    public bool SingleKey { get; set; }

    /// <summary>Raised when editing starts/ends so global hotkeys can be suspended.</summary>
    public static event Action<bool>? EditingChanged;

    public HotkeyBox()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        MinWidth = 180;
        FocusVisualStyle = null;
        Loaded += (_, _) => Refresh();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsKeyboardFocused) Focus();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        IsEditing = true;
        EditingChanged?.Invoke(true);
        Content = Loc.T("H.PressKeys");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsEditing)
        {
            IsEditing = false;
            EditingChanged?.Invoke(false);
        }
        Refresh();
    }

    private void Refresh()
    {
        if (!IsEditing) Content = string.IsNullOrEmpty(Hotkey) ? "—" : Hotkey;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsEditing) { base.OnPreviewKeyDown(e); return; }
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
            Content = Describe(Keyboard.Modifiers) + " + …";
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
        if (IsEditing && SingleKey && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2 or MouseButton.Middle)
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
        IsEditing = false;
        EditingChanged?.Invoke(false);
        Refresh();
        // Move focus back to the window so the field stops listening.
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.Focus(Window.GetWindow(this));
    }
}
