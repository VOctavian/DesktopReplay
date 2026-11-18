using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DesktopReplay.Core;
using DesktopReplay.Core.Settings;

namespace DesktopReplay.Services;

/// <summary>
/// Global hotkeys. Uses RegisterHotKey; if a combination is already taken by another program
/// (e.g. NVIDIA overlay) it falls back to a low-level keyboard hook on its own thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private readonly HwndSource _window;
    private readonly Dictionary<int, (Hotkey Key, Action Action)> _registered = new();
    private readonly List<(Hotkey Key, Action Action)> _hooked = new();
    private int _nextId = 1;
    private KeyboardHook? _hook;

    /// <summary>While true (e.g. a hotkey field is being edited) hotkeys do nothing.</summary>
    public bool Suspended { get; set; }

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("DesktopReplayHotkeys") { ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
    }

    /// <summary>Registers all hotkeys; returns the ones that had to fall back to the hook.</summary>
    public List<Hotkey> Register(IEnumerable<(Hotkey Key, Action Action)> bindings)
    {
        Clear();
        var conflicts = new List<Hotkey>();
        foreach (var (key, action) in bindings)
        {
            if (key.IsEmpty) continue;
            uint mods = MOD_NOREPEAT;
            if (key.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= MOD_ALT;
            if (key.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= MOD_CONTROL;
            if (key.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= MOD_SHIFT;
            if (key.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= MOD_WIN;
            int id = _nextId++;
            if (RegisterHotKey(_window.Handle, id, mods, (uint)key.Vk))
            {
                _registered[id] = (key, action);
            }
            else
            {
                Log.Warn($"RegisterHotKey {key} failed ({Marshal.GetLastWin32Error()}), using keyboard hook");
                _hooked.Add((key, action));
                conflicts.Add(key);
            }
        }
        if (_hooked.Count > 0)
        {
            _hook = new KeyboardHook(OnHookKey);
        }
        return conflicts;
    }

    private void Clear()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        _hooked.Clear();
        _hook?.Dispose();
        _hook = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var b))
        {
            handled = true;
            Log.Info($"Hotkey {b.Key}{(Suspended ? " (suspended)" : "")}");
            if (!Suspended) Invoke(b.Action);
        }
        return IntPtr.Zero;
    }

    /// <summary>Called on the hook thread; returns true to swallow the key.</summary>
    private bool OnHookKey(int vk, HotkeyModifiers mods)
    {
        if (Suspended) return false;
        foreach (var (key, action) in _hooked)
        {
            if (key.Vk == vk && key.Modifiers == mods)
            {
                Log.Info($"Hotkey {key} (hook)");
                _window.Dispatcher.BeginInvoke(() => Invoke(action));
                return true;
            }
        }
        return false;
    }

    private static void Invoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("Hotkey action failed", ex); }
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }

    /// <summary>WH_KEYBOARD_LL hook running on a dedicated thread with its own message loop.</summary>
    private sealed class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hmod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        private readonly Func<int, HotkeyModifiers, bool> _callback;
        private readonly HookProc _proc;
        private readonly Thread _thread;
        private uint _threadId;
        private IntPtr _hook;

        public KeyboardHook(Func<int, HotkeyModifiers, bool> callback)
        {
            _callback = callback;
            _proc = Proc;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName), 0);
                ready.Set();
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                UnhookWindowsHookEx(_hook);
            }) { IsBackground = true, Name = "KeyboardHook", Priority = ThreadPriority.Highest };
            _thread.Start();
            ready.Wait(2000);
        }

        private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                int vk = Marshal.ReadInt32(lParam);
                if (!KeyNames.IsModifierVk(vk))
                {
                    var mods = HotkeyModifiers.None;
                    if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= HotkeyModifiers.Alt;
                    if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= HotkeyModifiers.Ctrl;
                    if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= HotkeyModifiers.Shift;
                    if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) mods |= HotkeyModifiers.Win;
                    if (_callback(vk, mods)) return new IntPtr(1);
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
        }
    }
}
