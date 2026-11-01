using System.Runtime.InteropServices;

namespace SweaxIris.Audio;

// Just enough of the Core Audio COM interfaces to enumerate devices and capture from them.

public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[StructLayout(LayoutKind.Sequential)]
public struct PROPERTYKEY { public Guid fmtid; public uint pid; }

[StructLayout(LayoutKind.Sequential)]
public struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WAVEFORMATEX
{
    public ushort wFormatTag, nChannels;
    public uint nSamplesPerSec, nAvgBytesPerSec;
    public ushort nBlockAlign, wBitsPerSample, cbSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;

    public static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>48 kHz stereo 32-bit float, the format everything is turned into.</summary>
    public static WAVEFORMATEXTENSIBLE StereoFloat(int rate = 48000) => new()
    {
        Format = new WAVEFORMATEX
        {
            wFormatTag = 0xFFFE, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 32, nBlockAlign = 8,
            nAvgBytesPerSec = (uint)rate * 8, cbSize = 22,
        },
        wValidBitsPerSample = 32, dwChannelMask = 3, SubFormat = Float,
    };
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClock
{
    [PreserveSig] int GetFrequency(out ulong frequency);
    [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
    [PreserveSig] int GetCharacteristics(out uint characteristics);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }

public static class Wasapi
{
    public const int SHAREMODE_SHARED = 0;
    public const uint STREAMFLAGS_LOOPBACK = 0x00020000, STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint STREAMFLAGS_AUTOCONVERTPCM = 0x80000000, STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint BUFFERFLAGS_DATA_DISCONTINUITY = 0x1, BUFFERFLAGS_SILENT = 0x2, BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint CLSCTX_ALL = 0x17;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");
    public static PROPERTYKEY PKEY_Device_FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    public static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    public static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0) return "";
        try
        {
            if (store.GetValue(ref PKEY_Device_FriendlyName, out var v) != 0) return "";
            try { return v.vt == 31 ? Marshal.PtrToStringUni(v.p) ?? "" : ""; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int hr, out object iface);
                if (hr < 0) Done.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("activate", hr));
                else Done.TrySetResult((IAudioClient)iface);
            }
            catch (Exception ex) { Done.TrySetException(ex); }
        }
    }

    /// <summary>
    /// An audio client that hears only what one process (and its children) plays. Windows 10 2004 and newer.
    /// </summary>
    public static IAudioClient ActivateProcessLoopback(uint pid, bool includeTree = true)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK, { TargetProcessId, ProcessLoopbackMode } }
        IntPtr parameters = Marshal.AllocHGlobal(12);
        IntPtr prop = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(parameters, 0, 1);
            Marshal.WriteInt32(parameters, 4, (int)pid);
            Marshal.WriteInt32(parameters, 8, includeTree ? 0 : 1);
            // PROPVARIANT holding a BLOB
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(prop, i, 0);
            Marshal.WriteInt16(prop, 0, 65);
            Marshal.WriteInt32(prop, 8, 12);
            Marshal.WriteIntPtr(prop, 16, parameters);
            var handler = new ActivationHandler();
            Guid iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, prop, handler, out var op);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (!handler.Done.Task.Wait(5000)) throw new TimeoutException("Windows did not open the app audio stream.");
            GC.KeepAlive(op);
            return handler.Done.Task.Result;
        }
        finally
        {
            Marshal.FreeHGlobal(prop);
            Marshal.FreeHGlobal(parameters);
        }
    }
}
