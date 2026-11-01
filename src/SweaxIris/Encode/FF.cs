using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SweaxIris.Core;

namespace SweaxIris.Encode;

/// <summary>Loads the bundled FFmpeg libraries and turns their error codes into exceptions.</summary>
public static unsafe class FF
{
    static bool _ready;
    static av_log_set_callback_callback? _log;

    public static string Dir { get; private set; } = "";

    public static void Init()
    {
        if (_ready) return;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "vendor", "ffmpeg")),
        ];
        Dir = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "avcodec-61.dll")))
            ?? throw new FileNotFoundException("The FFmpeg libraries (avcodec-61.dll) are missing next to SweaxIris.exe.");
        ffmpeg.RootPath = Dir;
        // FFmpeg's own messages go to the log, warnings and worse only
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        _log = (p0, level, format, vl) =>
        {
            if (level > ffmpeg.av_log_get_level()) return;
            const int size = 1024;
            var buffer = stackalloc byte[size];
            int prefix = 1;
            ffmpeg.av_log_format_line(p0, level, format, vl, buffer, size, &prefix);
            var line = Marshal.PtrToStringUTF8((IntPtr)buffer)?.TrimEnd();
            if (!string.IsNullOrEmpty(line)) Paths.Log("ffmpeg: " + line);
        };
        ffmpeg.av_log_set_callback(_log);
        _ready = true;
        Paths.Log($"ffmpeg {ffmpeg.av_version_info()} from {Dir}");
    }

    public static string Error(int code)
    {
        var buffer = stackalloc byte[256];
        ffmpeg.av_strerror(code, buffer, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? code.ToString();
    }

    public static int Check(int code, string what)
    {
        if (code < 0) throw new FFException($"{what}: {Error(code)}", code);
        return code;
    }

    public static void Set(AVDictionary** dict, string key, string value) => ffmpeg.av_dict_set(dict, key, value, 0);
    public static void Set(AVDictionary** dict, string key, long value) => ffmpeg.av_dict_set(dict, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);

    public static readonly int EAGAIN = ffmpeg.AVERROR(ffmpeg.EAGAIN);
    public static readonly int EOF = ffmpeg.AVERROR_EOF;
}

public sealed class FFException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}
