using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Microsoft.Win32;
using SweaxIris.Native;

namespace SweaxIris.Core;

/// <summary>An invisible window for the tray icon, theme changes and session notifications.</summary>
public sealed class HostWindow : IDisposable
{
    public const int WM_TRAY = Win32.WM_APP + 1;
    readonly HwndSource _src;
    readonly uint _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");

    public IntPtr Handle => _src.Handle;
    public event Action<int>? TrayMessage;
    public event Action? TaskbarCreated;
    public event Action? ThemeChanged;
    public event Action? DisplaysChanged;

    public HostWindow()
    {
        var p = new HwndSourceParameters("SweaxIris.Host")
        {
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = (int)Win32.WS_EX_TOOLWINDOW,
        };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_TRAY:
                TrayMessage?.Invoke((int)((long)lParam & 0xFFFF));
                handled = true;
                break;
            case 0x001A when lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet":
                ThemeChanged?.Invoke();
                break;
            case 0x007E: // WM_DISPLAYCHANGE
                DisplaysChanged?.Invoke();
                break;
            default:
                if (msg == _taskbarCreated) TaskbarCreated?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _src.Dispose();
}

/// <summary>Notification-area icon drawn with plain Shell_NotifyIcon; it can switch to a recording icon.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly HostWindow _host;
    IntPtr _icon, _current;
    bool _added;
    string _tip;

    public event Action? LeftClick;
    public event Action? RightClick;

    public TrayIcon(HostWindow host, string tip)
    {
        _host = host;
        _tip = tip;
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SweaxIris.exe");
        var small = new IntPtr[1];
        if (Win32.ExtractIconEx(exe, 0, null, small, 1) > 0) _icon = small[0];
        _current = _icon;
        host.TrayMessage += OnMessage;
        host.TaskbarCreated += () => { _added = false; Add(); };
        Add();
    }

    void OnMessage(int msg)
    {
        switch (msg)
        {
            case Win32.NIN_SELECT or Win32.NIN_KEYSELECT: LeftClick?.Invoke(); break;
            case Win32.WM_CONTEXTMENU: RightClick?.Invoke(); break;
        }
    }

    Win32.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _host.Handle,
        uID = 1,
        uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
        uCallbackMessage = HostWindow.WM_TRAY,
        hIcon = _current,
        szTip = _tip.Length > 120 ? _tip[..120] : _tip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = Win32.NOTIFYICON_VERSION_4,
    };

    void Add()
    {
        if (_added) return;
        var d = Data();
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
        if (_added) Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref d);
    }

    /// <summary>Shows <paramref name="icon"/> (or the app icon when zero) with a new tooltip.</summary>
    public void Set(IntPtr icon, string tip)
    {
        _current = icon == IntPtr.Zero ? _icon : icon;
        _tip = tip;
        var d = Data();
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        var d = Data();
        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
    }
}

/// <summary>Commands from later launches (shortcuts and launchers run "SweaxIris.exe --record" etc.).</summary>
public sealed class Ipc : IDisposable
{
    public const string PipeName = "SweaxIris.Commands.4c19";
    readonly CancellationTokenSource _stop = new();

    /// <summary>Raised on a worker thread.</summary>
    public event Action<string>? Received;

    public void Start() => Task.Run(Loop);

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? command = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(command)) Received?.Invoke(command);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Paths.Log("pipe: " + ex.Message); await Task.Delay(200); }
        }
    }

    public static bool Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var w = new StreamWriter(client, new UTF8Encoding(false));
            w.WriteLine(command);
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>Start-up entry and the Start menu shortcut, per user.</summary>
public static class Shell
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SweaxIris.exe");

    public static void AutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (on) k.SetValue("SweaxIris", $"\"{Exe}\" --background");
            else k.DeleteValue("SweaxIris", false);
        }
        catch (Exception ex) { Paths.Log("autostart: " + ex.Message); }
    }

    public static bool AutoStartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("SweaxIris") is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a file or folder with its default app.</summary>
    public static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Paths.Log("open: " + ex.Message); }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Paths.Log("reveal: " + ex.Message); }
    }

    /// <summary>Moves a file to the Recycle Bin (never deletes for good).</summary>
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0400 | 0x0004, // allow undo, no confirmation, no error ui, silent
        };
        int r = SHFileOperation(ref op);
        if (r != 0) Paths.Log($"recycle {path}: {r}");
        return r == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
