using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SweaxIris.Audio;
using SweaxIris.Core;
using SweaxIris.Encode;
using SweaxIris.Recording;

namespace SweaxIris.UI;

/// <summary>
/// Every setting, in six pages: a list on the left, the page on the right. Built in code so each row is
/// the same shape: what it does, one line on why, the control.
/// </summary>
public sealed class SettingsView : Grid
{
    readonly Studio _studio;
    readonly MainWindow _main;
    Settings S => _studio.S;
    readonly StackPanel _nav = new() { Margin = new Thickness(16, 18, 12, 18) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _page = new() { Margin = new Thickness(28, 22, 32, 32), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
    string _current = "general";
    static readonly string[] Pages = ["general", "keys", "video", "sound", "files", "look"];

    /// <summary>Set by the app while a shortcut button waits for keys.</summary>
    public static Action<Action<Hotkey?, int>>? RecordKey;
    public static Action? KeysChanged;

    public SettingsView(Studio studio, MainWindow main)
    {
        _studio = studio;
        _main = main;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = _nav };
        left.SetResourceReference(Border.BorderBrushProperty, "Line");
        left.SetResourceReference(Border.BackgroundProperty, "Surface");
        Children.Add(left);
        _scroll.Content = _page;
        SetColumn(_scroll, 1);
        Children.Add(_scroll);
        studio.EncodersReady += () => { if (_current == "video") Show("video"); };
    }

    public void Reload()
    {
        _nav.Children.Clear();
        var back = new Button { Style = (Style)FindResource("BtnGhost"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 12, 6), Margin = new Thickness(0, 0, 0, 14) };
        var bp = new StackPanel { Orientation = Orientation.Horizontal };
        bp.Children.Add(new Ico { Data = (Geometry)FindResource("IcoBack"), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        bp.Children.Add(new TextBlock { Text = Loc.T("settings.back") });
        back.Content = bp;
        back.Click += (_, _) => _main.ShowSettings(false);
        _nav.Children.Add(back);
        var title = new TextBlock { Text = Loc.T("settings"), Style = (Style)FindResource("H1"), Margin = new Thickness(8, 0, 0, 14) };
        _nav.Children.Add(title);
        foreach (var p in Pages)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Row"), GroupName = "page", IsChecked = p == _current, Margin = new Thickness(0, 0, 0, 2) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ico { Data = (Geometry)FindResource(p switch { "general" => "IcoGear", "keys" => "IcoKey", "video" => "IcoFilm", "sound" => "IcoSpeaker", "files" => "IcoFolder", _ => "IcoBrush" }), Width = 17, Height = 17, Margin = new Thickness(0, 0, 10, 0) });
            row.Children.Add(new TextBlock { Text = Loc.T("set." + p) });
            rb.Content = row;
            rb.SetResourceReference(Control.ForegroundProperty, "Text");
            var page = p;
            rb.Checked += (_, _) => Show(page);
            _nav.Children.Add(rb);
        }
        var about = new TextBlock { Style = (Style)FindResource("Faint"), Margin = new Thickness(8, 24, 0, 0), FontSize = 11,
            Text = Loc.F("about", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "", "7.1.1") };
        _nav.Children.Add(about);
        Show(_current);
    }

    /// <summary>Opens a page and ticks it in the list.</summary>
    public void Open(string page)
    {
        var rows = _nav.Children.OfType<RadioButton>().ToList();
        int i = Array.IndexOf(Pages, page);
        if (i >= 0 && i < rows.Count) { if (rows[i].IsChecked == true) Show(page); else rows[i].IsChecked = true; }
        else Show(page);
    }

    void Show(string page)
    {
        _current = page;
        _page.Children.Clear();
        _page.Children.Add(new TextBlock { Text = Loc.T("set." + page), Style = (Style)FindResource("H1"), FontSize = 24, Margin = new Thickness(0, 0, 0, 16) });
        switch (page)
        {
            case "general": General(); break;
            case "keys": Keys(); break;
            case "video": Video(); break;
            case "sound": Sound(); break;
            case "files": Files(); break;
            case "look": Look(); break;
        }
        _scroll.ScrollToTop();
        _page.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        _page.RenderTransform = tt;
        Springs.To(_page, OpacityProperty, 1, 260, 1);
        Springs.To(tt, TranslateTransform.YProperty, 0, 300, 0.8);
    }

    // ── building blocks ─────────────────────────────────────────────────────

    Border Card(params UIElement[] rows)
    {
        var sp = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "Line");
                sp.Children.Add(line);
            }
            sp.Children.Add(rows[i]);
        }
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = sp, Margin = new Thickness(0, 0, 0, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        _page.Children.Add(card);
        return card;
    }

    FrameworkElement Row(string title, string? desc, FrameworkElement? control, double controlWidth = double.NaN)
    {
        var g = new Grid { Margin = new Thickness(16, 12, 16, 12), MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Label"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(desc)) text.Children.Add(new TextBlock { Text = desc, Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            if (!double.IsNaN(controlWidth)) control.Width = controlWidth;
            SetColumn(control, 1);
            g.Children.Add(control);
        }
        return g;
    }

    Toggle Switch(bool value, Action<bool> set)
    {
        var t = new Toggle { IsChecked = value };
        t.Click += (_, _) => { set(t.IsChecked == true); S.Save(); };
        return t;
    }

    ComboBox Combo<T>(IEnumerable<(T Value, string Label)> items, T current, Action<T> set)
    {
        var list = items.Select(i => new ComboBoxItem { Content = i.Label, Tag = i.Value }).ToList();
        var cb = new ComboBox { ItemsSource = list, SelectedItem = list.FirstOrDefault(i => Equals(i.Tag, current)) ?? list.FirstOrDefault() };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem i) { set((T)i.Tag); S.Save(); } };
        return cb;
    }

    Segments Seg<T>(IEnumerable<(T Value, string Label)> items, T current, Action<T> set) where T : notnull
    {
        var s = new Segments();
        s.SetItems(items.Select(i => ((object)i.Value, i.Label)));
        s.Value = current;
        s.Changed += v => { set((T)v); S.Save(); };
        return s;
    }

    // ── pages ───────────────────────────────────────────────────────────────

    void General()
    {
        Card(
            Row(Loc.T("set.startup"), Loc.T("set.startup.d"), Switch(Core.Shell.AutoStartOn(), v => { S.StartWithWindows = v; Core.Shell.AutoStart(v); })),
            Row(Loc.T("set.countdown"), Loc.T("set.countdown.d"), Seg(new[] { (0, Loc.T("set.none")), (3, "3"), (5, "5"), (10, "10") }, S.Countdown, v => S.Countdown = v), 220),
            Row(Loc.T("set.sounds"), Loc.T("set.sounds.d"), Switch(S.Sounds, v => S.Sounds = v)));
        Card(
            Row(Loc.T("set.controller"), Loc.T("set.controller.d"), Switch(S.ShowController, v => S.ShowController = v)),
            Row(Loc.T("set.hide"), Loc.T("set.hide.d"), Switch(S.HideWhileRecording, v => S.HideWhileRecording = v)),
            Row(Loc.T("set.exclude"), Loc.T("set.exclude.d"), Switch(S.HideFromCapture, v => { S.HideFromCapture = v; _main.ApplyCaptureExclusion(); })));
        Card(Row(Loc.T("set.lang"), null, Combo(new[] { ("auto", Loc.T("set.auto")), ("en", "English"), ("tr", "Türkçe") }, S.Language, v => { S.Language = v; App.Relanguage(); }), 200));
    }

    void Keys()
    {
        Card(Row(Loc.T("set.keys.enabled"), Loc.T("set.keys.enabled.d"), Switch(S.ShortcutsEnabled, v => { S.ShortcutsEnabled = v; KeysChanged?.Invoke(); })));
        var rows = new List<UIElement>();
        foreach (var (action, key) in new[] { (HotkeyAction.Record, "key.record"), (HotkeyAction.Pause, "key.pause"), (HotkeyAction.Region, "key.region"), (HotkeyAction.Mic, "key.mic"), (HotkeyAction.Show, "key.show") })
            rows.Add(KeyRow(action, Loc.T(key)));
        Card(rows.ToArray());
        // the macro key note, with the iris as its badge
        var note = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        note.ColumnDefinitions.Add(new ColumnDefinition());
        note.Children.Add(new Ico { Data = (Geometry)FindResource("IcoKey"), Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) });
        var txt = new StackPanel();
        txt.Children.Add(new TextBlock { Text = Loc.T("key.macro.t"), Style = (Style)FindResource("Label") });
        txt.Children.Add(new TextBlock { Text = Loc.T("key.macro.d"), Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 3, 0, 0) });
        SetColumn(txt, 1);
        note.Children.Add(txt);
        var c = Card(note);
        c.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        c.SetResourceReference(Border.BorderBrushProperty, "AccentLine");
    }

    static string Get(Settings s, HotkeyAction a) => a switch
    {
        HotkeyAction.Record => s.KeyRecord, HotkeyAction.Pause => s.KeyPause, HotkeyAction.Region => s.KeyRegion, HotkeyAction.Mic => s.KeyMic, _ => s.KeyShow,
    };

    static void Put(Settings s, HotkeyAction a, string v)
    {
        switch (a)
        {
            case HotkeyAction.Record: s.KeyRecord = v; break;
            case HotkeyAction.Pause: s.KeyPause = v; break;
            case HotkeyAction.Region: s.KeyRegion = v; break;
            case HotkeyAction.Mic: s.KeyMic = v; break;
            default: s.KeyShow = v; break;
        }
    }

    FrameworkElement KeyRow(HotkeyAction action, string title)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        var btn = new Button { Style = (Style)FindResource("Btn"), MinWidth = 150, Padding = new Thickness(10, 6, 10, 6) };
        var clear = new Button { Style = (Style)FindResource("IconBtn"), Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.T("key.clear"), Content = new Ico { Data = (Geometry)FindResource("IcoClose"), Width = 14, Height = 14 } };
        var warn = new TextBlock { Style = (Style)FindResource("Dim"), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "Tally");
        void Paint() => btn.Content = KeyCaps.Build(Hotkey.Parse(Get(S, action)), 12);
        Paint();
        btn.Click += (_, _) =>
        {
            var wait = new TextBlock { Text = Loc.T("key.press"), FontSize = 12.5 };
            wait.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            btn.Content = wait;
            btn.SetResourceReference(Button.BorderBrushProperty, "Accent");
            warn.Visibility = Visibility.Collapsed;
            RecordKey?.Invoke((hk, vk) => Dispatcher.BeginInvoke(() =>
            {
                btn.SetResourceReference(Button.BorderBrushProperty, "Line");
                if (hk is { } got)
                {
                    // one key, one job
                    foreach (var other in Enum.GetValues<HotkeyAction>())
                        if (other != action && Hotkey.Parse(Get(S, other)) == got)
                        {
                            warn.Text = Loc.F("key.taken", Loc.T("key." + other.ToString().ToLowerInvariant()));
                            warn.Visibility = Visibility.Visible;
                            Paint();
                            return;
                        }
                    Put(S, action, got.Spec);
                    S.Save();
                    KeysChanged?.Invoke();
                }
                else if (vk != 0x1B)
                {
                    warn.Text = Loc.F("key.bare", Hotkey.KeyName(vk));
                    warn.Visibility = Visibility.Visible;
                }
                Paint();
                _main.Refresh();
            }));
        };
        clear.Click += (_, _) => { Put(S, action, ""); S.Save(); KeysChanged?.Invoke(); Paint(); _main.Refresh(); };
        host.Children.Add(btn);
        host.Children.Add(clear);
        var row = (Grid)Row(title, null, host);
        var col = (StackPanel)row.Children[0];
        col.Children.Add(warn);
        return row;
    }

    void Video()
    {
        Card(
            Row(Loc.T("set.res"), Loc.T("set.res.d"), Combo(new[] { 0, 2160, 1440, 1080, 720, 480 }.Select(h => (h, h == 0 ? Loc.T("video.native") : $"{h}p")), S.Resolution, v => S.Resolution = v), 190),
            Row(Loc.T("set.fps"), Loc.T("set.fps.d"), Combo(new[] { 24, 30, 48, 50, 60, 90, 120, 144, 165, 240 }.Select(f => (f, Loc.F("video.fps", f))), S.Fps, v => S.Fps = v), 190),
            Row(Loc.T("set.cursor"), null, Switch(S.Cursor, v => S.Cursor = v)));

        // encoders: automatic, then every one this computer can run
        var encs = new List<(string, string)> { ("auto", Loc.T("set.encoder.auto") + (_studio.Encoders != null ? " — " + _studio.PickEncoder(_studio.CurrentTarget()) : "")) };
        if (_studio.Encoders != null)
            foreach (var e in EncoderSpec.All.Where(e => _studio.Works(e))) encs.Add((e.Key, e.ToString()));
        var encDesc = _studio.Encoders == null ? Loc.T("set.encoder.checking") : Loc.T("set.encoder.d");
        Card(
            Row(Loc.T("set.encoder"), encDesc, Combo(encs, S.Encoder, v => S.Encoder = v), 270),
            Row(Loc.T("set.speed"), Loc.T("set.speed.d"), Seg(new[] { (0, Loc.T("speed.fast")), (1, Loc.T("speed.balanced")), (2, Loc.T("speed.quality")) }, S.Speed, v => S.Speed = v), 270),
            Row(Loc.T("set.container"), Loc.T("set.container.d"), Seg(new[] { ("mp4", "MP4"), ("mkv", "MKV") }, S.Container, v => S.Container = v), 160));

        var qualityRow = Row(Loc.T("set.rate"), Loc.T("set.quality.d"), Seg(new[] { (0.30, Loc.T("q.small")), (0.55, Loc.T("q.balanced")), (0.72, Loc.T("q.high")), (0.92, Loc.T("q.max")) },
            new[] { 0.30, 0.55, 0.72, 0.92 }.OrderBy(x => Math.Abs(x - S.Quality)).First(), v => S.Quality = v), 340);
        var bitrate = new Slider { Minimum = 2, Maximum = 150, Value = S.BitrateKbps / 1000.0, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1 };
        var bitrateText = new TextBlock { Text = $"{S.BitrateKbps / 1000} Mbps", Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("PlexMedium") };
        bitrate.ValueChanged += (_, e) => { S.BitrateKbps = (int)e.NewValue * 1000; bitrateText.Text = $"{(int)e.NewValue} Mbps"; };
        bitrate.LostMouseCapture += (_, _) => S.Save();
        var bp = new StackPanel { Orientation = Orientation.Horizontal };
        bp.Children.Add(bitrate);
        bp.Children.Add(bitrateText);
        var bitrateRow = Row(Loc.T("set.bitrate"), Loc.T("set.bitrate.d"), bp);
        var mode = Seg(new[] { (true, Loc.T("set.rate.cq")), (false, Loc.T("set.rate.cbr")) }, S.ConstantQuality, v =>
        {
            S.ConstantQuality = v;
            qualityRow.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
            bitrateRow.Visibility = v ? Visibility.Collapsed : Visibility.Visible;
        });
        qualityRow.Visibility = S.ConstantQuality ? Visibility.Visible : Visibility.Collapsed;
        bitrateRow.Visibility = S.ConstantQuality ? Visibility.Collapsed : Visibility.Visible;
        Card(Row(Loc.T("set.rate"), null, mode, 280), qualityRow, bitrateRow,
            Row(Loc.T("set.keyframe"), Loc.T("set.keyframe.d"), Combo(new[] { 1, 2, 3, 5, 10 }.Select(k => (k, $"{k} s")), S.KeyframeSeconds, v => S.KeyframeSeconds = v), 120));
    }

    void Sound()
    {
        var outs = new List<(string, string)> { ("", Loc.T("sound.default")) };
        outs.AddRange(AudioDevices.List(EDataFlow.Render).Select(d => (d.Id, d.Name)));
        var ins = new List<(string, string)> { ("", Loc.T("sound.default")) };
        ins.AddRange(AudioDevices.List(EDataFlow.Capture).Select(d => (d.Id, d.Name)));
        Card(
            Row(Loc.T("sound.system"), Loc.T("set.system.d"), Switch(S.SystemAudio, v => S.SystemAudio = v)),
            Row(Loc.T("sound.device"), null, Combo(outs, S.SystemDevice, v => S.SystemDevice = v), 300),
            Row(Loc.T("sound.apponly"), Loc.T("set.apponly.d"), Switch(S.AppAudioOnly, v => S.AppAudioOnly = v)),
            Row(Loc.T("set.offset"), Loc.T("set.offset.d"), Offset(S.SystemOffsetMs, v => S.SystemOffsetMs = v)));
        Card(
            Row(Loc.T("sound.mic"), Loc.T("set.mic.d"), Switch(S.Microphone, v => S.Microphone = v)),
            Row(Loc.T("sound.device"), null, Combo(ins, S.MicDevice, v => S.MicDevice = v), 300),
            Row(Loc.T("set.offset"), Loc.T("set.offset.d"), Offset(S.MicOffsetMs, v => S.MicOffsetMs = v)));
        Card(
            Row(Loc.T("set.tracks"), Loc.T("set.tracks.d"), Switch(S.SeparateTracks, v => S.SeparateTracks = v)),
            Row(Loc.T("set.abitrate"), "AAC, 48 kHz", Seg(new[] { (128, "128"), (192, "192"), (256, "256"), (320, "320") }, S.AudioBitrate, v => S.AudioBitrate = v), 240));
    }

    FrameworkElement Offset(int value, Action<int> set)
    {
        var s = new Slider { Minimum = -500, Maximum = 500, Value = value, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 10 };
        var t = new TextBlock { Text = $"{value:+0;−0;0} ms", Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("PlexMedium") };
        s.ValueChanged += (_, e) => { set((int)e.NewValue); t.Text = $"{(int)e.NewValue:+0;−0;0} ms"; };
        s.LostMouseCapture += (_, _) => S.Save();
        s.MouseDoubleClick += (_, _) => s.Value = 0;
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(s);
        p.Children.Add(t);
        return p;
    }

    void Files()
    {
        var path = new TextBlock { Text = S.Folder, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        var change = new Button { Style = (Style)FindResource("Btn"), Content = Loc.T("set.folder.change"), Margin = new Thickness(10, 0, 0, 0) };
        var open = new Button { Style = (Style)FindResource("BtnGhost"), Content = Loc.T("set.folder.open"), Margin = new Thickness(4, 0, 0, 0) };
        change.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(S.Folder) ? S.Folder : null };
            if (dlg.ShowDialog() == true) { S.OutputFolder = dlg.FolderName; S.Save(); path.Text = S.Folder; }
        };
        open.Click += (_, _) => { Directory.CreateDirectory(S.Folder); Core.Shell.Open(S.Folder); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(path);
        row.Children.Add(change);
        row.Children.Add(open);
        var name = new TextBox { Text = S.NamePattern, Width = 260 };
        name.LostFocus += (_, _) => { S.NamePattern = string.IsNullOrWhiteSpace(name.Text) ? "SweaxIris {date} {time}" : name.Text; S.Save(); };
        Card(
            Row(Loc.T("set.folder"), null, row),
            Row(Loc.T("set.name"), Loc.T("set.name.d"), name),
            Row(Loc.T("set.openafter"), null, Switch(S.OpenFolderAfter, v => S.OpenFolderAfter = v)));
    }

    void Look()
    {
        Card(Row(Loc.T("set.theme"), null, Seg(new[] { ("system", Loc.T("theme.system")), ("light", Loc.T("theme.light")), ("dark", Loc.T("theme.dark")) }, S.Theme, v => { S.Theme = v; Theme.Apply(S.Theme, S.Palette); }), 300));
        // the coatings: each swatch is a small iris in that colour
        var wrap = new WrapPanel { Margin = new Thickness(12, 10, 12, 14) };
        foreach (var p in Theme.Palettes)
        {
            var c = Theme.Dark ? p.Dark : p.Light;
            bool on = p.Key == S.Palette;
            var iris = new Iris { Width = 54, Height = 54, ShowScale = false, Aperture = on ? 0.62 : 0.3, Core = c };
            var label = new TextBlock { Text = Loc.T("pal." + p.Key), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
            label.SetResourceReference(TextBlock.ForegroundProperty, on ? "Text" : "TextDim");
            var sp = new StackPanel { Margin = new Thickness(6), Width = 76, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            sp.Children.Add(iris);
            sp.Children.Add(label);
            var key = p.Key;
            sp.MouseEnter += (_, _) => Springs.To(iris, Iris.ApertureProperty, 0.7, 300, 0.6);
            sp.MouseLeave += (_, _) => Springs.To(iris, Iris.ApertureProperty, key == S.Palette ? 0.62 : 0.3, 300, 0.8);
            sp.MouseLeftButtonUp += (_, _) => { S.Palette = key; S.Save(); Theme.Apply(S.Theme, S.Palette); Show("look"); };
            wrap.Children.Add(sp);
        }
        var head = new TextBlock { Text = Loc.T("set.palette"), Style = (Style)FindResource("Label"), Margin = new Thickness(16, 14, 16, 0) };
        var both = new StackPanel();
        both.Children.Add(head);
        both.Children.Add(wrap);
        Card(both);
    }
}
