using System.Runtime.InteropServices;

namespace SweaxIris.Native;

/// <summary>The parts of Win32 a recorder needs: precise timers, capture exclusion, window geometry.</summary>
public static partial class Win32
{
    // ── timing ───────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll")] public static extern bool QueryPerformanceCounter(out long count);
    [DllImport("kernel32.dll")] public static extern bool QueryPerformanceFrequency(out long freq);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetEvent(IntPtr handle);
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] public static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    // ── windows ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint GW_OWNER = 4;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    public const int GWL_STYLE = -16;
    public const long WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000;
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>A window's visible frame (without the invisible resize borders) in physical pixels.</summary>
    public static RECT FrameOf(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;

    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Top-level windows a person would call "a window": visible, titled, not cloaked, not tool windows.</summary>
    public static List<IntPtr> RealWindows()
    {
        var list = new List<IntPtr>();
        IntPtr shell = GetShellWindow();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (h == shell || !IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (GetWindowTextLength(h) == 0) return true;
            var r = FrameOf(h);
            if (r.Width < 40 || r.Height < 30) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MONITORINFO MonitorInfo(IntPtr hmon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hmon, ref mi);
        return mi;
    }

    /// <summary>Every monitor with its handle and rectangle in physical pixels, primary first.</summary>
    public static List<(IntPtr Handle, RECT Bounds, bool Primary)> MonitorList()
    {
        var list = new List<(IntPtr, RECT, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT r, IntPtr _) =>
        {
            var mi = MonitorInfo(h);
            list.Add((h, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Item3).ThenBy(m => m.Item2.Left).ToList();
    }
}

public static partial class Win32
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>\.\DISPLAY1 and so on.</summary>
    public static string MonitorDevice(IntPtr hmon)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoEx(hmon, ref mi) ? mi.szDevice : "";
    }

    // ── monitor names ("DELL U2723QE") from the display configuration ───────
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public uint refreshNum, refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct DISPLAYCONFIG_MODE_INFO { public uint infoType; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint modes, [Out] DISPLAYCONFIG_MODE_INFO[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    /// <summary>GDI device name → the monitor's own name and refresh rate.</summary>
    public static Dictionary<string, (string Name, double Hz)> MonitorNames()
    {
        var map = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;
            for (int i = 0; i < np; i++)
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 1, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = paths[i].sourceInfo.adapterId, id = paths[i].sourceInfo.id } };
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME { header = new DISPLAYCONFIG_DEVICE_INFO_HEADER { type = 2, size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = paths[i].targetInfo.adapterId, id = paths[i].targetInfo.id } };
                if (DisplayConfigGetDeviceInfo(ref src) != 0 || DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                var t = paths[i].targetInfo;
                double hz = t.refreshDen > 0 ? (double)t.refreshNum / t.refreshDen : 0;
                map[src.viewGdiDeviceName] = (tgt.monitorFriendlyDeviceName, hz);
            }
        }
        catch { }
        return map;
    }
}
