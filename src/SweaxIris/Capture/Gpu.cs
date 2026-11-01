using System.Runtime.InteropServices;
using SweaxIris.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace SweaxIris.Capture;

/// <summary>One graphics card as the recorder sees it.</summary>
public sealed record AdapterInfo(int Index, string Name, uint VendorId, long Luid, ulong VideoMemory, List<IntPtr> Monitors)
{
    public const uint Nvidia = 0x10DE, Amd = 0x1002, Intel = 0x8086;
    public string Vendor => VendorId switch { Nvidia => "NVIDIA", Amd => "AMD", Intel => "Intel", _ => "GPU" };
}

/// <summary>
/// A Direct3D 11 device with video support, shared by capture, colour conversion and the hardware encoder.
/// The immediate context is made thread-safe, and <see cref="Enter"/> holds it across several calls.
/// </summary>
public sealed class Gpu : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice Video { get; }
    public ID3D11VideoContext VideoContext { get; }
    public AdapterInfo Adapter { get; }
    readonly ID3D11Multithread _mt;
    IDirect3DDevice? _winrt;

    Gpu(ID3D11Device device, ID3D11DeviceContext context, AdapterInfo adapter)
    {
        Device = device;
        Context = context;
        Adapter = adapter;
        _mt = context.QueryInterface<ID3D11Multithread>();
        _mt.SetMultithreadProtected(true);
        Video = device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = context.QueryInterface<ID3D11VideoContext>();
    }

    /// <summary>The device as Windows.Graphics.Capture wants it.</summary>
    public IDirect3DDevice WinRt => _winrt ??= WinRtInterop.CreateDevice(Device);

    public readonly struct Held : IDisposable
    {
        readonly ID3D11Multithread _mt;
        public Held(ID3D11Multithread mt) { _mt = mt; mt.Enter(); }
        public void Dispose() => _mt.Leave();
    }

    /// <summary>Keeps other threads off the context until disposed.</summary>
    public Held Enter() => new(_mt);

    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? a).Success; i++)
        {
            using (a)
            {
                var d = a!.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0) continue;
                var mons = new List<IntPtr>();
                for (uint o = 0; a.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                    using (output) mons.Add(output!.Description.Monitor);
                list.Add(new AdapterInfo((int)i, d.Description.Trim(), d.VendorId, d.Luid, (ulong)d.DedicatedVideoMemory, mons));
            }
        }
        return list;
    }

    /// <summary>The card a monitor is plugged into (capturing on it avoids copies between cards).</summary>
    public static AdapterInfo? AdapterForMonitor(IntPtr hmon) => Adapters().FirstOrDefault(a => a.Monitors.Contains(hmon));

    /// <summary>Creates a device on the card that matches <paramref name="pick"/>, or the first card.</summary>
    public static Gpu Create(Func<AdapterInfo, bool>? pick = null)
    {
        var all = Adapters();
        var chosen = (pick != null ? all.FirstOrDefault(pick) : null) ?? all.FirstOrDefault()
            ?? throw new InvalidOperationException("No graphics card found.");
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)chosen.Index, out IDXGIAdapter1? adapter).CheckError();
        using (adapter)
        {
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, levels, out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
                throw new InvalidOperationException($"Could not open {chosen.Name} for video (0x{hr.Code:X8}).");
            Paths.Log($"gpu: {chosen.Name} ({chosen.Vendor}), feature level {device.FeatureLevel}");
            return new Gpu(device, context, chosen);
        }
    }

    public void Dispose()
    {
        (_winrt as IDisposable)?.Dispose();
        VideoContext.Dispose();
        Video.Dispose();
        _mt.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>The little bit of COM glue between Direct3D 11 and Windows.Graphics.Capture.</summary>
public static unsafe class WinRtInterop
{
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public static IDirect3DDevice CreateDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr p));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    public static Windows.Graphics.Capture.GraphicsCaptureItem ForMonitor(IntPtr hmon) => CreateItem(hmon, monitor: true);
    public static Windows.Graphics.Capture.GraphicsCaptureItem ForWindow(IntPtr hwnd) => CreateItem(hwnd, monitor: false);

    static Windows.Graphics.Capture.GraphicsCaptureItem CreateItem(IntPtr handle, bool monitor)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out IntPtr hs));
        IntPtr factory = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, IID_IGraphicsCaptureItemInterop, out factory));
            // IGraphicsCaptureItemInterop: 3 = CreateForWindow, 4 = CreateForMonitor
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)factory + (monitor ? 4 : 3)));
            Guid iid = IID_IGraphicsCaptureItem;
            IntPtr item;
            Marshal.ThrowExceptionForHR(fn(factory, handle, &iid, &item));
            try { return Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(hs);
        }
    }

    /// <summary>The Direct3D texture behind a captured frame (caller releases it).</summary>
    public static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        IntPtr unk = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
        Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in accessIid, out IntPtr access));
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + 3));
            Guid texIid = IID_ID3D11Texture2D;
            IntPtr tex;
            Marshal.ThrowExceptionForHR(fn(access, &texIid, &tex));
            return new ID3D11Texture2D(tex);
        }
        finally { Marshal.Release(access); }
    }
}
