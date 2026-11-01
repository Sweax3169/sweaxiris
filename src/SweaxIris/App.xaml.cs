using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SweaxIris.Capture;
using SweaxIris.Core;
using SweaxIris.Native;
using SweaxIris.Recording;
using SweaxIris.UI;

namespace SweaxIris;

public partial class App : Application
{
    const string InstanceName = "SweaxIris.SingleInstance.81f3";
    public static bool Quitting { get; private set; }
    static App? _app;

    Mutex? _instance;
    Settings S = null!;
    Studio _studio = null!;
    MainWindow _main = null!;
    KeyboardHook? _hook;
    HostWindow? _host;
    TrayIcon? _tray;
    Ipc? _ipc;
    ControllerWindow? _controller;
    RegionFrame? _frame;
    CountdownWindow? _count;
    IntPtr _recIcon;
    bool _hidMain, _selecting;
    readonly DispatcherTimer _rearm = new() { Interval = TimeSpan.FromMinutes(10) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _app = this;
        var args = e.Args;
        bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
        string? Arg(string a) { int i = Array.FindIndex(args, x => x.Equals(a, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

        if (Has("--iris")) { Paths.Init(); Dev.Snap.Irises(Arg("--iris")!); Shutdown(); return; }
        if (Has("--fonts")) { Paths.Init(); Dev.Snap.Fonts(Arg("--fonts")!); Shutdown(); return; }
        if (Has("--icon")) { Paths.Init(); Dev.Snap.Icon(Arg("--icon")!); Shutdown(); return; }
        if (Has("--selftest")) { Paths.Init(); Dev.SelfTest.Run(this, args); return; }
        if (Has("--testcard"))
        {
            // a test pattern window of our own at x y w h for n seconds (end-to-end tests record it)
            var v = Array.ConvertAll(args.SkipWhile(a => a != "--testcard").Skip(1).Take(5).ToArray(), int.Parse);
            var card = new Dev.TestCardWindow(Clock.Now, v[0], v[1], v[2], v[3], topmost: true);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(v[4]) };
            t.Tick += (_, _) => { t.Stop(); card.Dispose(); Shutdown(); };
            t.Start();
            return;
        }
        if (Has("--shots")) { Dev.Shots.Run(this, args); return; }

        _instance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // already running: pass the request on (macro launchers can run "SweaxIris.exe --record")
            string cmd = Has("--quit") ? "quit" : Has("--record") ? "record" : Has("--stop") ? "stop" : Has("--pause") ? "pause" : Has("--region") ? "region" : Has("--mic") ? "mic" : "show";
            Ipc.Send(cmd);
            Shutdown();
            return;
        }
        if (Has("--quit")) { Shutdown(); return; }

        Paths.Init(Arg("--data"));
        DispatcherUnhandledException += (_, ex) => { Paths.Log("ui: " + ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Paths.Log("fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Paths.Log("task: " + ex.Exception); ex.SetObserved(); };

        S = Settings.Load();
        Loc.Set(S.Language);
        Theme.Apply(S.Theme, S.Palette);
        _ = WgcSource.AllowBorderlessAsync();
        _studio = new Studio(S, Dispatcher);
        _studio.ProbeEncoders();
        _studio.StateChanged += OnStudioState;
        _studio.CountdownTick += OnCountdown;
        _studio.Saved += take => { Sound.Saved(S); new ToastWindow(take).Show(); };
        _studio.Error += ShowError;

        _host = new HostWindow();
        _host.ThemeChanged += () => { if (S.Theme == "system") Theme.Apply(S.Theme, S.Palette); };
        _host.DisplaysChanged += () => _main?.Refresh();
        _tray = new TrayIcon(_host, "SweaxIris");
        _tray.LeftClick += () => _main.ShowAndFocus();
        _tray.RightClick += TrayMenu;

        _hook = new KeyboardHook();
        _hook.Pressed += a => Dispatcher.BeginInvoke(() => OnHotkey(a));
        Bind();
        SettingsView.RecordKey = cb => _hook.Record(cb);
        SettingsView.KeysChanged = Bind;
        _rearm.Tick += (_, _) => _hook?.Rearm();
        _rearm.Start();

        _ipc = new Ipc();
        _ipc.Received += cmd => Dispatcher.BeginInvoke(() => OnCommand(cmd));
        _ipc.Start();

        CreateMain();
        if (!Has("--background")) _main.Show();
        if (Has("--record")) _ = Record();
        if (Has("--region")) _ = Region();
    }

    void CreateMain()
    {
        _main = new MainWindow(_studio)
        {
            RecordRequested = () => _ = Record(),
            RegionRequested = () => _ = Region(),
            PauseRequested = () => _studio.TogglePause(),
            MicRequested = () => _studio.ToggleMic(),
        };
    }

    /// <summary>The language changed: rebuild the window in it.</summary>
    public static void Relanguage()
    {
        if (_app == null) return;
        Loc.Set(_app.S.Language);
        var old = _app._main;
        bool visible = old.IsVisible;
        var rect = new Rect(old.Left, old.Top, old.Width, old.Height);
        Quitting = true;
        old.Close();
        Quitting = false;
        _app.CreateMain();
        _app._main.WindowStartupLocation = WindowStartupLocation.Manual;
        _app._main.Left = rect.Left; _app._main.Top = rect.Top; _app._main.Width = rect.Width; _app._main.Height = rect.Height;
        if (visible) { _app._main.Show(); _app._main.ShowSettings(true); }
    }

    void Bind()
    {
        if (_hook == null) return;
        _hook.Enabled = S.ShortcutsEnabled;
        var list = new List<KeyValuePair<HotkeyAction, Hotkey>>();
        void Add(HotkeyAction a, string spec) { if (Hotkey.Parse(spec) is { } hk) list.Add(new(a, hk)); }
        Add(HotkeyAction.Record, S.KeyRecord);
        Add(HotkeyAction.Pause, S.KeyPause);
        Add(HotkeyAction.Region, S.KeyRegion);
        Add(HotkeyAction.Mic, S.KeyMic);
        Add(HotkeyAction.Show, S.KeyShow);
        _hook.SetBindings(list);
    }

    void OnHotkey(HotkeyAction a)
    {
        switch (a)
        {
            case HotkeyAction.Record: _ = Record(); break;
            case HotkeyAction.Pause: _studio.TogglePause(); break;
            case HotkeyAction.Region:
                if (_studio.State == StudioState.Idle) _ = Region();
                else if (_studio.State == StudioState.Arming) _studio.CancelArming();
                break;
            case HotkeyAction.Mic: _studio.ToggleMic(); break;
            case HotkeyAction.Show: _main.ShowAndFocus(); break;
        }
    }

    void OnCommand(string cmd)
    {
        switch (cmd)
        {
            case "record": _ = Record(); break;
            case "stop": _ = _studio.StopAsync(); break;
            case "pause": _studio.TogglePause(); break;
            case "region": if (_studio.State == StudioState.Idle) _ = Region(); break;
            case "mic": _studio.ToggleMic(); break;
            case "quit": _ = Quit(); break;
            default: _main.ShowAndFocus(); break;
        }
    }

    /// <summary>Start, or stop, recording what the studio window has chosen.</summary>
    async Task Record()
    {
        switch (_studio.State)
        {
            case StudioState.Idle:
                if (_selecting) return;
                if (S.Source == "region" && _studio.LastRegion == null) { await Region(); return; }
                var t = _studio.CurrentTarget();
                if (t == null) { _main.ShowAndFocus(); ShowError(Loc.T(S.Source == "window" ? "src.nowindow" : "src.noregion")); return; }
                await StartWith(t);
                break;
            case StudioState.Arming:
                _studio.CancelArming();
                break;
            case StudioState.Recording or StudioState.Paused:
                await _studio.StopAsync();
                break;
        }
    }

    /// <summary>The region picker, then straight into recording what was drawn.</summary>
    async Task Region()
    {
        if (_studio.State != StudioState.Idle || _selecting) return;
        _selecting = true;
        bool mainWasVisible = _main.IsVisible;
        if (mainWasVisible) _main.Hide();
        Win32.RECT? r;
        try { r = await RegionSelector.PickAsync(_studio.LastRegion); }
        finally { _selecting = false; }
        if (r is not { } rect)
        {
            if (mainWasVisible) _main.Show();
            return;
        }
        _studio.SetRegion(rect);
        _main.RegionChanged();
        if (mainWasVisible && !S.HideWhileRecording) _main.Show();
        else if (mainWasVisible) _hidMain = true;
        await StartWith(CaptureTarget.OfRegion(rect));
    }

    async Task StartWith(CaptureTarget t)
    {
        if (S.Countdown > 0)
        {
            var area = t.Kind switch
            {
                SourceKind.Region => t.Region,
                SourceKind.Window => Win32.FrameOf(t.Window),
                _ => Win32.MonitorInfo(t.Monitor).rcMonitor,
            };
            _count = new CountdownWindow(area);
        }
        await _studio.StartAsync(t);
        if (_studio.State != StudioState.Recording)
        {
            _count?.Close();
            _count = null;
            if (_hidMain) { _hidMain = false; _main.Show(); }
        }
    }

    void OnCountdown(int n)
    {
        if (n > 0) { _count?.Show(n); Sound.Tick(S); }
        else { _count?.Go(); _count = null; }
    }

    void OnStudioState()
    {
        var st = _studio.State;
        switch (st)
        {
            case StudioState.Recording when _controller == null && _frame == null && _studio.Target is { } t:
                if (S.HideWhileRecording && _main.IsVisible) { _hidMain = true; _main.Hide(); }
                if (S.ShowController)
                {
                    _controller = new ControllerWindow(_studio)
                    {
                        PauseClicked = () => _studio.TogglePause(),
                        MicClicked = () => _studio.ToggleMic(),
                        StopClicked = () => _ = _studio.StopAsync(),
                    };
                    _controller.Show();
                    _controller.Place(t);
                }
                if (t.Kind == SourceKind.Region) { _frame = new RegionFrame(t.Region); _frame.Show(); }
                break;
            case StudioState.Finishing or StudioState.Idle:
                _controller?.Dismiss();
                _controller = null;
                _frame?.Close();
                _frame = null;
                if (st == StudioState.Idle && _hidMain) { _hidMain = false; _main.Show(); }
                break;
        }
        _controller?.Refresh();
        _frame?.SetPaused(st == StudioState.Paused);
        SetTray(st is StudioState.Recording or StudioState.Paused, st == StudioState.Paused);
    }

    void SetTray(bool recording, bool paused)
    {
        if (_tray == null) return;
        if (recording && _recIcon == IntPtr.Zero) _recIcon = Dev.Snap.TrayIcon();
        _tray.Set(recording ? _recIcon : IntPtr.Zero, recording ? $"SweaxIris · {Loc.T(paused ? "paused" : "recording")}" : "SweaxIris");
    }

    void ShowError(string msg)
    {
        var take = new Take("", DateTime.Now, 0, 0, 0, 0, 0, "", null, 0);
        new ToastWindow(take, msg, error: true).Show();
    }

    // ── tray menu ───────────────────────────────────────────────────────────

    Window? _menuHost;

    void TrayMenu()
    {
        _menuHost ??= new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Width = 1, Height = 1, Topmost = true, Left = -100, Top = -100 };
        _menuHost.Show();
        _menuHost.Activate();
        var m = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Item(string text, string? keys, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, InputGestureText = keys ?? "", IsEnabled = enabled };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        bool live = _studio.State is StudioState.Recording or StudioState.Paused;
        Item(live ? Loc.T("tray.stop") : Loc.T("tray.record"), Hotkey.Parse(S.KeyRecord)?.ToString(), () => _ = Record());
        if (live) Item(_studio.State == StudioState.Paused ? Loc.T("tray.resume") : Loc.T("tray.pause"), Hotkey.Parse(S.KeyPause)?.ToString(), () => _studio.TogglePause());
        Item(Loc.T("tray.region"), Hotkey.Parse(S.KeyRegion)?.ToString(), () => _ = Region(), !live);
        m.Items.Add(new Separator());
        Item(Loc.T("tray.open"), null, () => _main.ShowAndFocus());
        Item(Loc.T("tray.folder"), null, () => { Directory.CreateDirectory(S.Folder); Core.Shell.Open(S.Folder); });
        m.Items.Add(new Separator());
        Item(Loc.T("tray.quit"), null, () => _ = Quit());
        m.Closed += (_, _) => _menuHost?.Hide();
        m.PlacementTarget = _menuHost;
        m.IsOpen = true;
    }

    async Task Quit()
    {
        if (_studio.State is StudioState.Recording or StudioState.Paused) await _studio.StopAsync();
        Quitting = true;
        _main.Close();
        _hook?.Dispose();
        _ipc?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _host?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Tiny synthesized sounds: a soft tick for the countdown and a shutter click when a take is saved.</summary>
public static class Sound
{
    static byte[]? _tick, _saved;

    public static void Tick(Settings s) { if (s.Sounds) Play(_tick ??= Make(0.05, t => Math.Sin(2 * Math.PI * 1760 * t) * Math.Exp(-t * 90) * 0.22)); }

    public static void Saved(Settings s)
    {
        if (!s.Sounds) return;
        Play(_saved ??= Make(0.16, t =>
        {
            // two quick clicks, like a shutter closing and opening
            double a = Math.Exp(-t * 160) * Math.Sin(2 * Math.PI * 2400 * t);
            double t2 = t - 0.06;
            double b = t2 > 0 ? Math.Exp(-t2 * 120) * Math.Sin(2 * Math.PI * 1500 * t2) : 0;
            return (a + b) * 0.2;
        }));
    }

    static void Play(byte[] wav)
    {
        try { new System.Media.SoundPlayer(new MemoryStream(wav)).Play(); } catch { }
    }

    static byte[] Make(double seconds, Func<double, double> f)
    {
        const int rate = 44100;
        int n = (int)(seconds * rate);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)Math.Clamp(f((double)i / rate) * 32767, -32768, 32767));
        return ms.ToArray();
    }
}
