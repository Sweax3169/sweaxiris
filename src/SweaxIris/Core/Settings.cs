using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SweaxIris.Core;

/// <summary>Everything the user can set, saved as settings.json next to the log.</summary>
public sealed class Settings
{
    // ── general ─────────────────────────────────────────────────────────────
    public bool StartWithWindows { get; set; }
    public bool HideWhileRecording { get; set; } = true;
    public bool ShowController { get; set; } = true;
    public bool HideFromCapture { get; set; } = true;
    public int Countdown { get; set; } = 3;
    public bool Sounds { get; set; } = true;
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "system";
    public string Palette { get; set; } = "coating";

    // ── shortcuts ───────────────────────────────────────────────────────────
    public string KeyRecord { get; set; } = "Ctrl+Alt+R";
    public string KeyPause { get; set; } = "Ctrl+Alt+P";
    public string KeyRegion { get; set; } = "Ctrl+Alt+A";
    public string KeyMic { get; set; } = "Ctrl+Alt+M";
    public string KeyShow { get; set; } = "";
    public bool ShortcutsEnabled { get; set; } = true;

    // ── what to record ──────────────────────────────────────────────────────
    /// <summary>screen, window or region.</summary>
    public string Source { get; set; } = "screen";
    /// <summary>Monitor device name (\\.\DISPLAY1), or empty for the one the mouse is on.</summary>
    public string Monitor { get; set; } = "";
    public int[]? LastRegion { get; set; }

    // ── video ───────────────────────────────────────────────────────────────
    /// <summary>0 = as captured, else the output height (2160, 1440, 1080, 720, 480).</summary>
    public int Resolution { get; set; }
    public int Fps { get; set; } = 60;
    /// <summary>"auto" or an encoder key such as nvenc-h264.</summary>
    public string Encoder { get; set; } = "auto";
    public bool ConstantQuality { get; set; } = true;
    public double Quality { get; set; } = 0.62;
    public int BitrateKbps { get; set; } = 20000;
    public int Speed { get; set; } = 1;
    public int KeyframeSeconds { get; set; } = 2;
    public string Container { get; set; } = "mp4";
    public bool Cursor { get; set; } = true;

    // ── sound ───────────────────────────────────────────────────────────────
    public bool SystemAudio { get; set; } = true;
    public string SystemDevice { get; set; } = "";
    public double SystemVolume { get; set; } = 1;
    public bool AppAudioOnly { get; set; }
    public bool Microphone { get; set; }
    public string MicDevice { get; set; } = "";
    public double MicVolume { get; set; } = 1;
    public bool SeparateTracks { get; set; }
    public int AudioBitrate { get; set; } = 192;
    public int SystemOffsetMs { get; set; }
    public int MicOffsetMs { get; set; }

    // ── files ───────────────────────────────────────────────────────────────
    public string OutputFolder { get; set; } = "";
    public string NamePattern { get; set; } = "SweaxIris {date} {time}";
    public bool OpenFolderAfter { get; set; }

    [JsonIgnore] public string Folder => string.IsNullOrWhiteSpace(OutputFolder) ? Paths.DefaultOutput : OutputFolder;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Json) ?? new Settings();
        }
        catch (Exception ex) { Paths.Log("settings: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            string tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex) { Paths.Log("save settings: " + ex.Message); }
    }

    /// <summary>A file name for a new recording from the pattern ({date}, {time}, {source}).</summary>
    public string NewFilePath(string source)
    {
        var now = DateTime.Now;
        string name = (string.IsNullOrWhiteSpace(NamePattern) ? "SweaxIris {date} {time}" : NamePattern)
            .Replace("{date}", now.ToString("yyyy-MM-dd")).Replace("{time}", now.ToString("HH-mm-ss")).Replace("{source}", source);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        string ext = Container == "mkv" ? ".mkv" : ".mp4";
        string path = Path.Combine(Folder, name + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Folder, $"{name} ({i}){ext}");
        return path;
    }
}
