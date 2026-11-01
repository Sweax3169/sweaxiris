using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SweaxIris.Capture;

/// <summary>
/// Scales a BGRA picture into the recording size and turns it into NV12 (BT.709, TV range) on the GPU's
/// video engine, letterboxing when the shapes differ. The same class makes the small BGRA preview.
/// Call <see cref="Run"/> while holding <see cref="Gpu.Enter"/>.
/// </summary>
public sealed class Converter : IDisposable
{
    readonly Gpu _gpu;
    readonly Format _format;
    ID3D11VideoProcessorEnumerator? _enum;
    ID3D11VideoProcessor? _vp;
    ID3D11VideoProcessorOutputView? _outView;
    ID3D11VideoProcessorInputView? _inView;
    ID3D11Texture2D? _inTex;
    int _enumW, _enumH;

    public int Width { get; }
    public int Height { get; }
    /// <summary>The converted picture.</summary>
    public ID3D11Texture2D Output { get; }

    public Converter(Gpu gpu, int width, int height, Format format = Format.NV12)
    {
        _gpu = gpu;
        _format = format;
        Width = width;
        Height = height;
        Output = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | (format == Format.NV12 ? BindFlags.None : BindFlags.ShaderResource),
        });
    }

    void EnsureProcessor(int inW, int inH)
    {
        if (_vp != null && inW == _enumW && inH == _enumH) return;
        _inView?.Dispose(); _inView = null; _inTex = null;
        _outView?.Dispose(); _vp?.Dispose(); _enum?.Dispose();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inW, InputHeight = (uint)inH,
            OutputWidth = (uint)Width, OutputHeight = (uint)Height,
            InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalQuality,
        };
        _gpu.Video.CreateVideoProcessorEnumerator(ref content, out _enum).CheckError();
        _gpu.Video.CreateVideoProcessor(_enum, 0, out _vp).CheckError();
        _gpu.Video.CreateVideoProcessorOutputView(Output, _enum,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D },
            out _outView).CheckError();

        var vc = _gpu.VideoContext;
        vc.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        vc.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        vc.VideoProcessorSetOutputTargetRect(_vp, true, new RawRect(0, 0, Width, Height));
        vc.VideoProcessorSetOutputBackgroundColor(_vp, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        using (var vc1 = vc.QueryInterfaceOrNull<ID3D11VideoContext1>())
        {
            if (vc1 != null)
            {
                vc1.VideoProcessorSetStreamColorSpace1(_vp, 0, ColorSpaceType.RgbFullG22NoneP709);
                vc1.VideoProcessorSetOutputColorSpace1(_vp, _format == Format.NV12 ? ColorSpaceType.YcbcrStudioG22LeftP709 : ColorSpaceType.RgbFullG22NoneP709);
            }
        }
        _enumW = inW;
        _enumH = inH;
    }

    /// <summary>Where a picture of this size lands inside the output, keeping its shape.</summary>
    public RawRect Fit(int inW, int inH)
    {
        double s = Math.Min((double)Width / inW, (double)Height / inH);
        int w = Math.Min(Width, (int)Math.Round(inW * s / 2) * 2), h = Math.Min(Height, (int)Math.Round(inH * s / 2) * 2);
        int x = (Width - w) / 2 & ~1, y = (Height - h) / 2 & ~1;
        return new RawRect(x, y, x + w, y + h);
    }

    public void Run(ID3D11Texture2D input, int inW, int inH)
    {
        EnsureProcessor(inW, inH);
        if (!ReferenceEquals(input, _inTex) || _inView == null)
        {
            _inView?.Dispose();
            _gpu.Video.CreateVideoProcessorInputView(input, _enum!,
                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D },
                out _inView).CheckError();
            _inTex = input;
        }
        var vc = _gpu.VideoContext;
        vc.VideoProcessorSetStreamSourceRect(_vp!, 0, true, new RawRect(0, 0, inW, inH));
        vc.VideoProcessorSetStreamDestRect(_vp!, 0, true, Fit(inW, inH));
        var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = _inView! } };
        vc.VideoProcessorBlt(_vp!, _outView!, 0, 1, streams).CheckError();
    }

    public void Dispose()
    {
        _inView?.Dispose();
        _outView?.Dispose();
        _vp?.Dispose();
        _enum?.Dispose();
        Output.Dispose();
    }
}
