using System.Globalization;

namespace SweaxIris.Core;

/// <summary>English and Turkish text. <c>Loc.T("key")</c>; XAML uses <c>{core:L key}</c>.</summary>
public static class Loc
{
    public static bool Turkish { get; private set; }

    public static void Set(string lang)
    {
        Turkish = lang switch
        {
            "tr" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr",
        };
    }

    public static string T(string key) => (Turkish ? Tr : En).TryGetValue(key, out var s) ? s : En.TryGetValue(key, out var e) ? e : key;
    public static string F(string key, params object[] args) => string.Format(T(key), args);

    static readonly Dictionary<string, string> En = new()
    {
        ["app.tagline"] = "Screen recorder",
        ["tab.record"] = "Record", ["tab.library"] = "Takes", ["tab.settings"] = "Settings",
        ["pick.screen"] = "Choose a screen", ["pick.window"] = "Choose a window", ["pick.region"] = "Choose a region",
        ["live.paused"] = "Paused · Resume keeps writing the same file",
        ["map.title"] = "How Windows arranges them",
        ["tile.chosen"] = "Chosen", ["tile.mouse"] = "Where the mouse is", ["tile.mouse.d"] = "Follows you between monitors",
        ["tile.draw"] = "Draw a new region", ["tile.draw.d"] = "Drag over the screen, or click a window", ["tile.last"] = "Last region",
        ["tile.primary"] = "primary", ["search"] = "Search windows", ["more.settings"] = "All video settings",
        ["q.bitrate"] = "Bitrate {0} Mb/s", ["vol"] = "{0} %", ["strip.ready"] = "Ready to record", ["strip.nothing"] = "Choose something to record",
        ["src.screen"] = "Screen", ["src.window"] = "Window", ["src.region"] = "Region",
        ["src.screen.hint"] = "A whole monitor", ["src.window.hint"] = "One app, even behind others", ["src.region.hint"] = "A rectangle you draw",
        ["src.mouse"] = "The one with the mouse", ["src.pick"] = "Choose a window", ["src.nowindow"] = "No window chosen",
        ["src.draw"] = "Draw a region", ["src.redraw"] = "Draw again", ["src.noregion"] = "No region yet",
        ["src.primary"] = "Primary", ["src.monitor"] = "Monitor {0}", ["mon.title"] = "Display {0} · {1}", ["mon.only"] = "Display {0}",
        ["sound"] = "Sound", ["sound.system"] = "System sound", ["sound.mic"] = "Microphone", ["sound.default"] = "Default device",
        ["sound.apponly"] = "Only this app's sound", ["sound.device"] = "Device", ["sound.muted"] = "Muted", ["sound.off"] = "Off",
        ["video"] = "Video", ["video.native"] = "As captured", ["video.fps"] = "{0} fps",
        ["record"] = "Record", ["stop"] = "Stop", ["pause"] = "Pause", ["resume"] = "Resume", ["recording"] = "Recording", ["paused"] = "Paused",
        ["arming"] = "Getting ready…", ["finishing"] = "Finishing the file…", ["cancel"] = "Cancel",
        ["takes"] = "Takes", ["takes.empty"] = "Your recordings appear here.", ["takes.folder"] = "Open folder", ["takes.play"] = "Play",
        ["takes.show"] = "Show in folder", ["takes.delete"] = "Move to Recycle Bin", ["takes.copy"] = "Copy file",
        ["settings"] = "Settings", ["settings.back"] = "Back",
        ["set.general"] = "General", ["set.keys"] = "Shortcuts", ["set.video"] = "Video", ["set.sound"] = "Sound", ["set.files"] = "Files", ["set.look"] = "Look",
        ["set.startup"] = "Start with Windows", ["set.startup.d"] = "Waits quietly in the tray so the shortcuts always work.",
        ["set.hide"] = "Hide this window while recording", ["set.hide.d"] = "It comes back when you stop.",
        ["set.controller"] = "Show the recording bar", ["set.controller.d"] = "Timer, pause, mic and stop, next to what you record.",
        ["set.exclude"] = "Keep SweaxIris out of recordings", ["set.exclude.d"] = "Its windows, bar and frame never appear in the video.",
        ["set.countdown"] = "Countdown", ["set.countdown.d"] = "Seconds before recording starts.", ["set.none"] = "None",
        ["set.sounds"] = "Sounds", ["set.sounds.d"] = "A soft tick on the countdown and a click when the file is saved.",
        ["set.lang"] = "Language", ["set.auto"] = "Automatic",
        ["set.keys.enabled"] = "Shortcuts", ["set.keys.enabled.d"] = "Work everywhere, even inside games.",
        ["key.record"] = "Start / stop recording", ["key.pause"] = "Pause / resume", ["key.region"] = "Record a region", ["key.mic"] = "Mute the microphone", ["key.show"] = "Show SweaxIris",
        ["key.press"] = "Press the keys…", ["key.none"] = "Not set", ["key.clear"] = "Clear", ["key.bare"] = "{0} alone would get in the way of typing. Add Ctrl or Alt, or use Pause, Scroll Lock or F13–F24.",
        ["key.taken"] = "Already used for “{0}”.",
        ["key.macro.t"] = "Macro keys", ["key.macro.d"] = "A keyboard macro key works too. On a Monster Pusat K3 Pro: REC → G1 → Pause → REC teaches G1 to press Pause. Then click a shortcut here and press G1. Pause, Scroll Lock and F13–F24 can be shortcuts on their own.",
        ["set.res"] = "Resolution", ["set.res.d"] = "Scaled on the graphics card, sharp and fast.",
        ["set.fps"] = "Frame rate", ["set.fps.d"] = "Above your monitor's refresh rate repeats frames.",
        ["set.encoder"] = "Encoder", ["set.encoder.d"] = "The graphics card's own video engine is fastest. Automatic picks the best one.",
        ["set.encoder.auto"] = "Automatic", ["set.encoder.checking"] = "Checking this computer…",
        ["set.rate"] = "Quality level", ["set.ratemode"] = "Quality mode", ["set.ratemode.d"] = "Constant quality spends bits where the picture needs them; bitrate keeps the file size predictable.", ["set.rate.cq"] = "Constant quality", ["set.rate.cbr"] = "Bitrate",
        ["set.quality.d"] = "Higher keeps more detail and makes larger files.",
        ["q.small"] = "Small", ["q.balanced"] = "Balanced", ["q.high"] = "High", ["q.max"] = "Near lossless",
        ["set.bitrate"] = "Bitrate", ["set.bitrate.d"] = "Megabits per second. 15–40 suits 1080p60, 40–80 suits 1440p or more.",
        ["set.speed"] = "Encoder effort", ["set.speed.d"] = "More effort, better picture for the same size.",
        ["speed.fast"] = "Light", ["speed.balanced"] = "Balanced", ["speed.quality"] = "Thorough",
        ["set.keyframe"] = "Keyframe every", ["set.keyframe.d"] = "Shorter makes editing and seeking snappier.",
        ["set.container"] = "File type", ["set.container.d"] = "MKV survives a crash or power cut; MP4 opens everywhere.",
        ["set.cursor"] = "Record the mouse pointer",
        ["set.system.d"] = "Everything you hear from this computer.", ["set.mic.d"] = "Your voice.",
        ["set.apponly.d"] = "When recording a window, take the sound of that app only.",
        ["set.tracks"] = "Separate tracks", ["set.tracks.d"] = "Also keep system and microphone on their own tracks for editing.",
        ["set.abitrate"] = "Sound quality", ["set.offset"] = "Sync offset", ["set.offset.d"] = "Move a device earlier (−) or later (+) if it lags, e.g. a Bluetooth headset.",
        ["set.folder"] = "Save recordings to", ["set.folder.change"] = "Change…", ["set.folder.open"] = "Open",
        ["set.name"] = "File name", ["set.name.d"] = "{date}, {time} and {source} are filled in.",
        ["set.openafter"] = "Open the folder after each recording",
        ["set.theme"] = "Theme", ["theme.system"] = "Like Windows", ["theme.light"] = "Light", ["theme.dark"] = "Dark",
        ["set.palette"] = "Colour",
        ["pal.coating"] = "Coating", ["pal.cobalt"] = "Cobalt", ["pal.ember"] = "Ember", ["pal.orchid"] = "Orchid", ["pal.lime"] = "Lime", ["pal.graphite"] = "Graphite",
        ["sel.hint"] = "Drag to draw · click a window to take it · Shift keeps 16:9 · Alt draws without snapping · Esc cancels",
        ["sel.hint.adjust"] = "Drag the frame or its edges · arrows nudge, Ctrl+arrows resize · double-click or Enter records",
        ["sel.free"] = "Free", ["sel.sizes"] = "Sizes", ["sel.full"] = "Full screen", ["sel.cancel"] = "Cancel · Esc",
        ["sel.record"] = "Record", ["sel.retry"] = "Redraw", ["sel.enter"] = "Enter",
        ["toast.saved"] = "Saved", ["toast.play"] = "Play", ["toast.folder"] = "Folder",
        ["err.title"] = "Could not record", ["err.window"] = "That window is gone.",
        ["tray.record"] = "Record", ["tray.stop"] = "Stop recording", ["tray.region"] = "Record a region…", ["tray.pause"] = "Pause", ["tray.resume"] = "Resume",
        ["tray.open"] = "Open SweaxIris", ["tray.folder"] = "Open recordings folder", ["tray.quit"] = "Quit",
        ["stats.dropped"] = "{0} frames dropped", ["stats.smooth"] = "Every frame kept",
        ["ago.now"] = "just now", ["ago.min"] = "{0} min ago", ["ago.hour"] = "{0} h ago", ["ago.yesterday"] = "yesterday",
        ["about"] = "SweaxIris {0} · Sweax Studios · GPLv3 · FFmpeg {1}",
    };

    static readonly Dictionary<string, string> Tr = new()
    {
        ["app.tagline"] = "Ekran kaydedici",
        ["tab.record"] = "Kayıt", ["tab.library"] = "Çekimler", ["tab.settings"] = "Ayarlar",
        ["pick.screen"] = "Bir ekran seç", ["pick.window"] = "Bir pencere seç", ["pick.region"] = "Bir bölge seç",
        ["live.paused"] = "Duraklatıldı · Devam aynı dosyaya yazmayı sürdürür",
        ["map.title"] = "Windows'ta nasıl dizildikleri",
        ["tile.chosen"] = "Seçili", ["tile.mouse"] = "Farenin olduğu ekran", ["tile.mouse.d"] = "Monitörler arasında seni izler",
        ["tile.draw"] = "Yeni bölge çiz", ["tile.draw.d"] = "Ekranda sürükle ya da bir pencereye tıkla", ["tile.last"] = "Son bölge",
        ["tile.primary"] = "ana ekran", ["search"] = "Pencere ara", ["more.settings"] = "Tüm görüntü ayarları",
        ["q.bitrate"] = "Bit hızı {0} Mb/s", ["vol"] = "%{0}", ["strip.ready"] = "Kayda hazır", ["strip.nothing"] = "Kaydedilecek bir şey seç",
        ["src.screen"] = "Ekran", ["src.window"] = "Pencere", ["src.region"] = "Bölge",
        ["src.screen.hint"] = "Bütün bir monitör", ["src.window.hint"] = "Tek uygulama, arkada kalsa bile", ["src.region.hint"] = "Çizdiğin bir dikdörtgen",
        ["src.mouse"] = "Farenin olduğu", ["src.pick"] = "Pencere seç", ["src.nowindow"] = "Pencere seçilmedi",
        ["src.draw"] = "Bölge çiz", ["src.redraw"] = "Yeniden çiz", ["src.noregion"] = "Henüz bölge yok",
        ["src.primary"] = "Ana ekran", ["src.monitor"] = "Monitör {0}", ["mon.title"] = "Ekran {0} · {1}", ["mon.only"] = "Ekran {0}",
        ["sound"] = "Ses", ["sound.system"] = "Sistem sesi", ["sound.mic"] = "Mikrofon", ["sound.default"] = "Varsayılan aygıt",
        ["sound.apponly"] = "Yalnız bu uygulamanın sesi", ["sound.device"] = "Aygıt", ["sound.muted"] = "Sessizde", ["sound.off"] = "Kapalı",
        ["video"] = "Görüntü", ["video.native"] = "Yakalandığı gibi", ["video.fps"] = "{0} fps",
        ["record"] = "Kaydet", ["stop"] = "Durdur", ["pause"] = "Duraklat", ["resume"] = "Devam", ["recording"] = "Kaydediliyor", ["paused"] = "Duraklatıldı",
        ["arming"] = "Hazırlanıyor…", ["finishing"] = "Dosya bitiriliyor…", ["cancel"] = "Vazgeç",
        ["takes"] = "Çekimler", ["takes.empty"] = "Kayıtların burada görünür.", ["takes.folder"] = "Klasörü aç", ["takes.play"] = "Oynat",
        ["takes.show"] = "Klasörde göster", ["takes.delete"] = "Geri Dönüşüm Kutusu'na taşı", ["takes.copy"] = "Dosyayı kopyala",
        ["settings"] = "Ayarlar", ["settings.back"] = "Geri",
        ["set.general"] = "Genel", ["set.keys"] = "Kısayollar", ["set.video"] = "Görüntü", ["set.sound"] = "Ses", ["set.files"] = "Dosyalar", ["set.look"] = "Görünüm",
        ["set.startup"] = "Windows ile başlat", ["set.startup.d"] = "Tepside sessizce bekler, kısayollar hep çalışır.",
        ["set.hide"] = "Kayıt sırasında bu pencereyi gizle", ["set.hide.d"] = "Durdurunca geri gelir.",
        ["set.controller"] = "Kayıt çubuğunu göster", ["set.controller.d"] = "Süre, duraklat, mikrofon ve durdur; kaydettiğin yerin yanında.",
        ["set.exclude"] = "SweaxIris kayda girmesin", ["set.exclude.d"] = "Pencereleri, çubuğu ve çerçevesi videoda hiç görünmez.",
        ["set.countdown"] = "Geri sayım", ["set.countdown.d"] = "Kayıt başlamadan önceki saniye.", ["set.none"] = "Yok",
        ["set.sounds"] = "Sesler", ["set.sounds.d"] = "Geri sayımda yumuşak bir tık, dosya kaydedilince bir klik.",
        ["set.lang"] = "Dil", ["set.auto"] = "Otomatik",
        ["set.keys.enabled"] = "Kısayollar", ["set.keys.enabled.d"] = "Her yerde çalışır, oyunların içinde bile.",
        ["key.record"] = "Kaydı başlat / durdur", ["key.pause"] = "Duraklat / devam", ["key.region"] = "Bölge kaydet", ["key.mic"] = "Mikrofonu sustur", ["key.show"] = "SweaxIris'i göster",
        ["key.press"] = "Tuşlara bas…", ["key.none"] = "Atanmadı", ["key.clear"] = "Temizle", ["key.bare"] = "Tek başına {0} yazmanı engeller. Ctrl ya da Alt ekle, ya da Pause, Scroll Lock veya F13–F24 kullan.",
        ["key.taken"] = "Zaten “{0}” için kullanılıyor.",
        ["key.macro.t"] = "Makro tuşları", ["key.macro.d"] = "Klavyenin makro tuşu da çalışır. Monster Pusat K3 Pro'da: REC → G1 → Pause → REC ile G1 Pause'a basar. Sonra burada bir kısayola tıklayıp G1'e bas. Pause, Scroll Lock ve F13–F24 tek başına kısayol olabilir.",
        ["set.res"] = "Çözünürlük", ["set.res.d"] = "Ekran kartında ölçeklenir; keskin ve hızlı.",
        ["set.fps"] = "Kare hızı", ["set.fps.d"] = "Monitörünün yenileme hızından yükseği kareleri tekrarlar.",
        ["set.encoder"] = "Kodlayıcı", ["set.encoder.d"] = "Ekran kartının kendi video motoru en hızlısıdır. Otomatik en iyisini seçer.",
        ["set.encoder.auto"] = "Otomatik", ["set.encoder.checking"] = "Bu bilgisayar inceleniyor…",
        ["set.rate"] = "Kalite düzeyi", ["set.ratemode"] = "Kalite modu", ["set.ratemode.d"] = "Sabit kalite bitleri görüntünün ihtiyacına göre harcar; bit hızı dosya boyutunu öngörülebilir tutar.", ["set.rate.cq"] = "Sabit kalite", ["set.rate.cbr"] = "Bit hızı",
        ["set.quality.d"] = "Yüksek, daha çok ayrıntı ve daha büyük dosya demek.",
        ["q.small"] = "Küçük", ["q.balanced"] = "Dengeli", ["q.high"] = "Yüksek", ["q.max"] = "Kayıpsıza yakın",
        ["set.bitrate"] = "Bit hızı", ["set.bitrate.d"] = "Saniyede megabit. 1080p60 için 15–40, 1440p ve üstü için 40–80.",
        ["set.speed"] = "Kodlayıcı çabası", ["set.speed.d"] = "Daha çok çaba, aynı boyutta daha iyi görüntü.",
        ["speed.fast"] = "Hafif", ["speed.balanced"] = "Dengeli", ["speed.quality"] = "Titiz",
        ["set.keyframe"] = "Anahtar kare aralığı", ["set.keyframe.d"] = "Kısa aralık kurguda ve sarmada daha akıcıdır.",
        ["set.container"] = "Dosya türü", ["set.container.d"] = "MKV çökmede ya da elektrik kesintisinde kurtulur; MP4 her yerde açılır.",
        ["set.cursor"] = "Fare imlecini kaydet",
        ["set.system.d"] = "Bu bilgisayardan duyduğun her şey.", ["set.mic.d"] = "Senin sesin.",
        ["set.apponly.d"] = "Pencere kaydederken yalnız o uygulamanın sesini al.",
        ["set.tracks"] = "Ayrı kanallar", ["set.tracks.d"] = "Kurgu için sistem sesi ve mikrofonu ayrıca kendi kanallarında da tut.",
        ["set.abitrate"] = "Ses kalitesi", ["set.offset"] = "Senkron kaydırma", ["set.offset.d"] = "Bir aygıt gecikiyorsa (ör. Bluetooth kulaklık) erkene (−) ya da geçe (+) kaydır.",
        ["set.folder"] = "Kayıtların yeri", ["set.folder.change"] = "Değiştir…", ["set.folder.open"] = "Aç",
        ["set.name"] = "Dosya adı", ["set.name.d"] = "{date}, {time} ve {source} doldurulur.",
        ["set.openafter"] = "Her kayıttan sonra klasörü aç",
        ["set.theme"] = "Tema", ["theme.system"] = "Windows gibi", ["theme.light"] = "Açık", ["theme.dark"] = "Koyu",
        ["set.palette"] = "Renk",
        ["pal.coating"] = "Kaplama", ["pal.cobalt"] = "Kobalt", ["pal.ember"] = "Köz", ["pal.orchid"] = "Orkide", ["pal.lime"] = "Limon", ["pal.graphite"] = "Grafit",
        ["sel.hint"] = "Çizmek için sürükle · pencereyi almak için tıkla · Shift 16:9 tutar · Alt yapışmadan çizer · Esc vazgeçer",
        ["sel.hint.adjust"] = "Çerçeveyi ya da kenarlarını sürükle · oklar kaydırır, Ctrl+oklar boyutlar · çift tık ya da Enter kaydeder",
        ["sel.free"] = "Serbest", ["sel.sizes"] = "Boyutlar", ["sel.full"] = "Tam ekran", ["sel.cancel"] = "Vazgeç · Esc",
        ["sel.record"] = "Kaydet", ["sel.retry"] = "Yeniden çiz", ["sel.enter"] = "Enter",
        ["toast.saved"] = "Kaydedildi", ["toast.play"] = "Oynat", ["toast.folder"] = "Klasör",
        ["err.title"] = "Kayıt başlamadı", ["err.window"] = "O pencere kapanmış.",
        ["tray.record"] = "Kaydet", ["tray.stop"] = "Kaydı durdur", ["tray.region"] = "Bölge kaydet…", ["tray.pause"] = "Duraklat", ["tray.resume"] = "Devam",
        ["tray.open"] = "SweaxIris'i aç", ["tray.folder"] = "Kayıt klasörünü aç", ["tray.quit"] = "Çık",
        ["stats.dropped"] = "{0} kare atlandı", ["stats.smooth"] = "Tek kare kaçmadı",
        ["ago.now"] = "az önce", ["ago.min"] = "{0} dk önce", ["ago.hour"] = "{0} sa önce", ["ago.yesterday"] = "dün",
        ["about"] = "SweaxIris {0} · Sweax Studios · GPLv3 · FFmpeg {1}",
    };
}

/// <summary>XAML markup: <c>Text="{core:L record}"</c>.</summary>
[System.Windows.Markup.MarkupExtensionReturnType(typeof(string))]
public sealed class L : System.Windows.Markup.MarkupExtension
{
    public L() { }
    public L(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
