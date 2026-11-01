using System.Runtime.InteropServices;
using SweaxIris.Core;
using SweaxIris.Native;

namespace SweaxIris.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Lists playback and recording devices and tells the app when the default one changes.</summary>
public static class AudioDevices
{
    public static List<AudioDevice> List(EDataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = Wasapi.Enumerator();
            string? def = null;
            if (en.GetDefaultAudioEndpoint(flow, ERole.Console, out var d) == 0)
            {
                d.GetId(out def);
                Marshal.ReleaseComObject(d);
            }
            if (en.EnumAudioEndpoints(flow, Wasapi.DEVICE_STATE_ACTIVE, out var col) != 0) return list;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                dev.GetId(out string id);
                list.Add(new AudioDevice(id, Wasapi.FriendlyName(dev), id == def));
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(col);
            Marshal.ReleaseComObject(en);
        }
        catch (Exception ex) { Paths.Log("audio devices: " + ex.Message); }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    public static string? DefaultName(EDataFlow flow) => List(flow).FirstOrDefault(d => d.IsDefault)?.Name;
}

/// <summary>
/// One capture stream: what the speakers play (loopback), a microphone, or what a single app plays.
/// Everything arrives as 48 kHz stereo float with the time it was heard, on a thread of its own.
/// When the device disappears or the default changes, the stream quietly reopens.
/// </summary>
public sealed unsafe class CaptureStream : IDisposable
{
    public enum Kind { System, Microphone, App }

    /// <summary>Samples (interleaved stereo), frame count, time of the first frame (100 ns, QPC clock), silent flag.</summary>
    public delegate void PacketHandler(float* data, int frames, long time, bool silent);

    public Kind Source { get; }
    public string? DeviceId { get; }
    public uint ProcessId { get; }
    public event PacketHandler? Packet;
    public string? Error { get; private set; }
    public string DeviceName { get; private set; } = "";

    /// <summary>Peak level of recent audio (0..1, before volume), decays on its own.</summary>
    public float Peak => _peak;
    volatile float _peak;
    long _lastPacket;

    readonly Thread _thread;
    volatile bool _stop;
    volatile bool _reopen;
    readonly Notifier _notifier;
    IMMDeviceEnumerator? _enumerator;
    float[] _zeros = new float[4096];

    public CaptureStream(Kind kind, string? deviceId = null, uint processId = 0)
    {
        Source = kind;
        DeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        ProcessId = processId;
        _notifier = new Notifier(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "SweaxIris.Audio." + kind, Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start() => _thread.Start();

    void Run()
    {
        uint task = 0;
        IntPtr mm = Win32.AvSetMmThreadCharacteristicsW("Audio", ref task);
        try
        {
            _enumerator = Wasapi.Enumerator();
            if (Source != Kind.App) _enumerator.RegisterEndpointNotificationCallback(_notifier);
            int failures = 0;
            while (!_stop)
            {
                try
                {
                    CaptureOnce();
                    failures = 0;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    if (failures++ < 3 || failures % 20 == 0) Paths.Log($"audio {Source}: {ex.Message}");
                    // a missing device: try again in a moment (it may be plugged back in)
                    for (int i = 0; i < 20 && !_stop && !_reopen; i++) Thread.Sleep(50);
                }
                _reopen = false;
            }
        }
        finally
        {
            try { if (Source != Kind.App) _enumerator?.UnregisterEndpointNotificationCallback(_notifier); } catch { }
            if (_enumerator != null) Marshal.ReleaseComObject(_enumerator);
            if (mm != IntPtr.Zero) Win32.AvRevertMmThreadCharacteristics(mm);
        }
    }

    IAudioClient Open(out string name)
    {
        if (Source == Kind.App)
        {
            name = "App";
            return Wasapi.ActivateProcessLoopback(ProcessId);
        }
        var flow = Source == Kind.Microphone ? EDataFlow.Capture : EDataFlow.Render;
        IMMDevice? dev = null;
        if (DeviceId != null && _enumerator!.GetDevice(DeviceId, out var picked) == 0)
        {
            picked.GetState(out uint state);
            if (state == Wasapi.DEVICE_STATE_ACTIVE) dev = picked;
            else Marshal.ReleaseComObject(picked);
        }
        // a chosen device that is gone falls back to the default one
        if (dev == null)
        {
            int hr = _enumerator!.GetDefaultAudioEndpoint(flow, ERole.Console, out var def);
            if (hr != 0) throw new InvalidOperationException(Source == Kind.Microphone ? "No microphone found." : "No playback device found.");
            dev = def;
        }
        try
        {
            name = Wasapi.FriendlyName(dev);
            Guid iid = Wasapi.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, Wasapi.CLSCTX_ALL, IntPtr.Zero, out object client));
            return (IAudioClient)client;
        }
        finally { Marshal.ReleaseComObject(dev); }
    }

    void CaptureOnce()
    {
        var client = Open(out string name);
        DeviceName = name;
        IntPtr evt = Win32.CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        IAudioCaptureClient? capture = null;
        IntPtr fmt = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
        try
        {
            Marshal.StructureToPtr(WAVEFORMATEXTENSIBLE.StereoFloat(), fmt, false);
            uint flags = Wasapi.STREAMFLAGS_EVENTCALLBACK | Wasapi.STREAMFLAGS_AUTOCONVERTPCM | Wasapi.STREAMFLAGS_SRC_DEFAULT_QUALITY;
            if (Source != Kind.Microphone) flags |= Wasapi.STREAMFLAGS_LOOPBACK;
            long buffer = Source == Kind.App ? 200_000 : 1_000_000; // 20 ms / 100 ms
            Marshal.ThrowExceptionForHR(client.Initialize(Wasapi.SHAREMODE_SHARED, flags, buffer, 0, fmt, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.SetEventHandle(evt));
            Guid ciid = Wasapi.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out object cc));
            capture = (IAudioCaptureClient)cc;
            Marshal.ThrowExceptionForHR(client.Start());
            Error = null;
            Paths.Log($"audio {Source}: capturing from {name}");

            while (!_stop && !_reopen)
            {
                // loopback streams sometimes never signal: wake up every 10 ms regardless
                Win32.WaitForSingleObject(evt, 10);
                while (true)
                {
                    int hr = capture.GetNextPacketSize(out uint next);
                    if (hr == Wasapi.AUDCLNT_E_DEVICE_INVALIDATED) return;
                    Marshal.ThrowExceptionForHR(hr);
                    if (next == 0) break;
                    hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint bflags, out _, out ulong qpc);
                    if (hr == Wasapi.AUDCLNT_E_DEVICE_INVALIDATED) return;
                    Marshal.ThrowExceptionForHR(hr);
                    long time = qpc != 0 && (bflags & Wasapi.BUFFERFLAGS_TIMESTAMP_ERROR) == 0 ? (long)qpc : Clock.Now - (long)frames * 10_000_000 / 48000;
                    bool silent = (bflags & Wasapi.BUFFERFLAGS_SILENT) != 0;
                    Deliver((float*)data, (int)frames, time, silent);
                    capture.ReleaseBuffer(frames);
                }
                // no packets for a while: let the meter fall
                if (Clock.Now - _lastPacket > 1_500_000) _peak *= 0.6f;
            }
        }
        finally
        {
            try { client.Stop(); } catch { }
            if (capture != null) Marshal.ReleaseComObject(capture);
            Marshal.ReleaseComObject(client);
            Marshal.FreeHGlobal(fmt);
            Win32.CloseHandle(evt);
        }
    }

    void Deliver(float* data, int frames, long time, bool silent)
    {
        _lastPacket = Clock.Now;
        float peak = 0;
        if (!silent)
        {
            for (int i = 0, n = frames * 2; i < n; i++)
            {
                float a = Math.Abs(data[i]);
                if (a > peak) peak = a;
            }
        }
        _peak = Math.Max(peak, _peak * 0.82f);
        var handler = Packet;
        if (handler == null) return;
        if (silent)
        {
            if (_zeros.Length < frames * 2) _zeros = new float[frames * 2];
            fixed (float* z = _zeros) handler(z, frames, time, true);
        }
        else handler(data, frames, time, false);
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(1500);
    }

    /// <summary>Reopens the stream when it follows the default device and that changes, or when its device comes back.</summary>
    sealed class Notifier(CaptureStream owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, uint state) { if (id == owner.DeviceId || owner.Error != null) owner._reopen = true; }
        public void OnDeviceAdded(string id) { if (owner.Error != null) owner._reopen = true; }
        public void OnDeviceRemoved(string id) { if (id == owner.DeviceId) owner._reopen = true; }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
        {
            if (role != ERole.Console || owner.DeviceId != null) return;
            if ((flow == EDataFlow.Capture) == (owner.Source == Kind.Microphone)) owner._reopen = true;
        }
        public void OnPropertyValueChanged(string id, PROPERTYKEY key) { }
    }
}
