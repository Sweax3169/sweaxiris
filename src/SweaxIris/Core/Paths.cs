using System.IO;

namespace SweaxIris.Core;

/// <summary>Where SweaxIris keeps its settings and log, and where recordings go by default.</summary>
public static class Paths
{
    public static string Data { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SweaxIris");
    static readonly object _logLock = new();

    public static void Init(string? overrideDir = null)
    {
        if (overrideDir != null) Data = overrideDir;
        Directory.CreateDirectory(Data);
    }

    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string LogFile => Path.Combine(Data, "sweaxiris.log");

    /// <summary>Videos\SweaxIris, or Documents if the Videos folder is missing.</summary>
    public static string DefaultOutput
    {
        get
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(videos, "SweaxIris");
        }
    }

    public static void Log(string line)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Data);
                var f = new FileInfo(LogFile);
                if (f.Exists && f.Length > 2_000_000) f.MoveTo(LogFile + ".old", true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
