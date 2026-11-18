namespace DesktopReplay.Core.Settings;

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>A key combination such as "Ctrl+Alt+Z" or a single key / mouse button ("Mouse4").</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Vk)
{
    public bool IsEmpty => Vk == 0;

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = HotkeyModifiers.None;
        int vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": mods |= HotkeyModifiers.Win; break;
                default: vk = KeyNames.ToVk(raw); break;
            }
        }
        return new Hotkey(mods, vk);
    }

    public override string ToString()
    {
        if (Vk == 0) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.ToName(Vk));
        return string.Join("+", parts);
    }
}

public static class KeyNames
{
    private static readonly Dictionary<int, string> VkToName = new();
    private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase);

    static KeyNames()
    {
        void Add(int vk, string name)
        {
            VkToName.TryAdd(vk, name);
            NameToVk.TryAdd(name, vk);
        }
        Add(0x01, "Mouse1"); Add(0x02, "Mouse2"); Add(0x04, "Mouse3"); Add(0x05, "Mouse4"); Add(0x06, "Mouse5");
        Add(0x08, "Backspace"); Add(0x09, "Tab"); Add(0x0D, "Enter"); Add(0x13, "Pause"); Add(0x14, "CapsLock");
        Add(0x1B, "Esc"); Add(0x20, "Space"); Add(0x21, "PageUp"); Add(0x22, "PageDown"); Add(0x23, "End"); Add(0x24, "Home");
        Add(0x25, "Left"); Add(0x26, "Up"); Add(0x27, "Right"); Add(0x28, "Down"); Add(0x2C, "PrintScreen");
        Add(0x2D, "Insert"); Add(0x2E, "Delete");
        for (int i = 0; i <= 9; i++) Add(0x30 + i, i.ToString());
        for (char c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        for (int i = 0; i <= 9; i++) Add(0x60 + i, "Num" + i);
        Add(0x6A, "Num*"); Add(0x6B, "Num+"); Add(0x6D, "Num-"); Add(0x6E, "Num."); Add(0x6F, "Num/");
        for (int i = 1; i <= 24; i++) Add(0x6F + i, "F" + i);
        Add(0x90, "NumLock"); Add(0x91, "ScrollLock");
        Add(0xA0, "LShift"); Add(0xA1, "RShift"); Add(0xA2, "LCtrl"); Add(0xA3, "RCtrl"); Add(0xA4, "LAlt"); Add(0xA5, "RAlt");
        Add(0x10, "ShiftKey"); Add(0x11, "CtrlKey"); Add(0x12, "AltKey");
        Add(0xBA, ";"); Add(0xBB, "="); Add(0xBC, ","); Add(0xBD, "-"); Add(0xBE, "."); Add(0xBF, "/"); Add(0xC0, "`");
        Add(0xDB, "["); Add(0xDC, "\\"); Add(0xDD, "]"); Add(0xDE, "'");
        Add(0x1B, "Escape");
    }

    public static int ToVk(string name)
    {
        if (NameToVk.TryGetValue(name, out int vk)) return vk;
        if (name.StartsWith("VK", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[2..], System.Globalization.NumberStyles.HexNumber, null, out vk)) return vk;
        return 0;
    }

    public static string ToName(int vk) => VkToName.TryGetValue(vk, out var n) ? n : $"VK{vk:X2}";

    public static bool IsModifierVk(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
}
