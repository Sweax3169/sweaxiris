using System.Runtime.InteropServices;
using SweaxIris.Native;

namespace SweaxIris.Core;

/// <summary>A global shortcut such as Ctrl+Alt+R, or a lone key nobody types with (Pause, F13–F24) for macro keys.</summary>
public readonly record struct Hotkey(bool Win, bool Ctrl, bool Alt, bool Shift, int Key)
{
    public static Hotkey? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        bool win = false, ctrl = false, alt = false, shift = false;
        int key = 0;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "win": win = true; break;
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
                    else if (part.Length > 1 && part[0] is 'F' or 'f' && int.TryParse(part[1..], out int f) && f is >= 1 and <= 24) key = 0x6F + f;
                    else if (Named.FirstOrDefault(n => n.Value.Equals(part, StringComparison.OrdinalIgnoreCase)) is { Value: not null } hit) key = hit.Key;
                    break;
            }
        }
        if (key == 0) return null;
        var hk = new Hotkey(win, ctrl, alt, shift, key);
        // never steal a bare key that is used for typing
        return hk.HasModifier || IsFree(key) ? hk : null;
    }

    static readonly Dictionary<int, string> Named = new()
    {
        [0x20] = "Space", [0x2D] = "Insert", [0x2E] = "Delete", [0x2C] = "PrtSc", [0x24] = "Home", [0x23] = "End", [0x21] = "PgUp", [0x22] = "PgDn",
        [0x13] = "Pause", [0x91] = "ScrollLock", [0xB6] = "App1", [0xB7] = "App2",
        [0x60] = "Num0", [0x61] = "Num1", [0x62] = "Num2", [0x63] = "Num3", [0x64] = "Num4", [0x65] = "Num5", [0x66] = "Num6", [0x67] = "Num7", [0x68] = "Num8", [0x69] = "Num9",
    };

    /// <summary>Keys a shortcut can end on: letters, digits, F1–F24, the number pad and a few named keys.</summary>
    public static bool IsBindable(int vk) => vk is (>= 0x41 and <= 0x5A) or (>= 0x30 and <= 0x39) or (>= 0x70 and <= 0x87) || Named.ContainsKey(vk);

    /// <summary>Keys nobody types with (F13–F24, Pause, Scroll Lock, the launch keys): a shortcut on their own, which suits macro keys.</summary>
    public static bool IsFree(int vk) => vk is (>= 0x7C and <= 0x87) or 0x13 or 0x91 or 0xB6 or 0xB7;

    public bool HasModifier => Win || Ctrl || Alt;

    /// <summary>The form saved in settings: "Ctrl+Alt+R".</summary>
    public string Spec => string.Join("+", Parts);

    public IEnumerable<string> Parts
    {
        get
        {
            if (Win) yield return "Win";
            if (Ctrl) yield return "Ctrl";
            if (Alt) yield return "Alt";
            if (Shift) yield return "Shift";
            yield return KeyName(Key);
        }
    }

    public static string KeyName(int vk) => vk is >= 0x70 and <= 0x87 ? "F" + (vk - 0x6F) : Named.TryGetValue(vk, out var n) ? n : ((char)vk).ToString();

    public override string ToString() => string.Join(" + ", Parts);
}

public enum HotkeyAction { Record, Pause, Region, Mic, Show }

/// <summary>
/// Low-level keyboard hook on its own thread with its own message loop, so a busy UI can never make
/// Windows drop it. A matching shortcut is swallowed (it never reaches the app in front, so a game does
/// not see it either). Keys we send ourselves carry <see cref="Win32.SweaxMark"/>; keys injected by macro
/// software and keyboard drivers are welcome, so a macro key can press a shortcut.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    const uint WM_REHOOK = Win32.WM_APP + 7;

    readonly Win32.HookProc _proc;
    readonly Thread _thread;
    uint _threadId;
    IntPtr _hook;
    volatile int _swallowKey;
    volatile Action<Hotkey?, int>? _record;
    readonly ManualResetEventSlim _ready = new();
    volatile KeyValuePair<HotkeyAction, Hotkey>[] _bindings = [];

    public volatile bool Enabled = true;

    /// <summary>Raised on the hook thread — handlers must only post work elsewhere.</summary>
    public event Action<HotkeyAction>? Pressed;

    public void SetBindings(IEnumerable<KeyValuePair<HotkeyAction, Hotkey>> bindings) => _bindings = bindings.ToArray();

    /// <summary>
    /// Captures the next shortcut pressed anywhere and reports it instead of acting on it. Esc reports
    /// (null, 0x1B); a key that cannot be a shortcut on its own reports (null, vk). Runs on the hook thread.
    /// </summary>
    public void Record(Action<Hotkey?, int> done) => _record = done;
    public void StopRecording() => _record = null;

    public KeyboardHook()
    {
        _proc = Callback;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SweaxIris.Hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(2000);
    }

    void Loop()
    {
        _threadId = Win32.GetCurrentThreadId();
        Install();
        _ready.Set();
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REHOOK) { Uninstall(); Install(); continue; }
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
        Uninstall();
    }

    void Install()
    {
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Paths.Log("keyboard hook failed: " + Marshal.GetLastWin32Error());
    }

    void Uninstall()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Windows silently removes hooks that ever time out; re-arming now and then keeps us alive for years.</summary>
    public void Rearm() => Win32.PostThreadMessage(_threadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
            bool ours = (kb.flags & Win32.LLKHF_INJECTED) != 0 && kb.dwExtraInfo == Win32.SweaxMark;
            if (!ours)
            {
                int vk = (int)kb.vkCode;
                if (down && _record is { } record)
                {
                    if (vk == 0x1B) { _record = null; _swallowKey = vk; record(null, vk); return 1; }
                    if (Hotkey.IsBindable(vk))
                    {
                        var got = Current(vk);
                        _record = null;
                        Swallow(got);
                        record(got.HasModifier || Hotkey.IsFree(vk) ? got : null, vk);
                        return 1;
                    }
                }
                else if (Enabled && down)
                {
                    if (_swallowKey == vk) return 1; // auto-repeat while held
                    foreach (var (action, hk) in _bindings)
                    {
                        if (vk == hk.Key && ModifiersMatch(hk))
                        {
                            Swallow(hk);
                            Pressed?.Invoke(action);
                            return 1;
                        }
                    }
                }
                if (up && _swallowKey == vk)
                {
                    _swallowKey = 0;
                    return 1;
                }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Eats the key's release too, and taps an unused key so a held Win or Alt doesn't open Start or the menu bar.</summary>
    void Swallow(Hotkey hk)
    {
        _swallowKey = hk.Key;
        if (hk.Win || hk.Alt) Win32.Send(Win32.Key(Win32.VK_MASK, false), Win32.Key(Win32.VK_MASK, true));
    }

    static Hotkey Current(int vk) => new(
        Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN), Win32.IsKeyDown(Win32.VK_CONTROL),
        Win32.IsKeyDown(Win32.VK_MENU), Win32.IsKeyDown(Win32.VK_SHIFT), vk);

    static bool ModifiersMatch(Hotkey hk)
    {
        bool win = Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN);
        return win == hk.Win && Win32.IsKeyDown(Win32.VK_CONTROL) == hk.Ctrl && Win32.IsKeyDown(Win32.VK_MENU) == hk.Alt && Win32.IsKeyDown(Win32.VK_SHIFT) == hk.Shift;
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
