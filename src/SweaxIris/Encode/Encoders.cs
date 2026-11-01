using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SweaxIris.Capture;
using SweaxIris.Core;
using Vortice.Direct3D11;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using BindFlags = Vortice.Direct3D11.BindFlags;

namespace SweaxIris.Encode;

public enum Codec { H264, Hevc, Av1 }
public enum Backend { Nvenc, Amf, Qsv, X264 }

/// <summary>One encoder FFmpeg can run: a backend (the GPU vendor's engine, or x264 on the CPU) and a codec.</summary>
public sealed record EncoderSpec(Backend Backend, Codec Codec)
{
    public string FFName => (Backend, Codec) switch
    {
        (Backend.Nvenc, Codec.H264) => "h264_nvenc",
        (Backend.Nvenc, Codec.Hevc) => "hevc_nvenc",
        (Backend.Nvenc, Codec.Av1) => "av1_nvenc",
        (Backend.Amf, Codec.H264) => "h264_amf",
        (Backend.Amf, Codec.Hevc) => "hevc_amf",
        (Backend.Amf, Codec.Av1) => "av1_amf",
        (Backend.Qsv, Codec.H264) => "h264_qsv",
        (Backend.Qsv, Codec.Hevc) => "hevc_qsv",
        (Backend.Qsv, Codec.Av1) => "av1_qsv",
        _ => "libx264",
    };

    /// <summary>The card this encoder lives on (0 = any, it runs on the processor).</summary>
    public uint Vendor => Backend switch { Backend.Nvenc => AdapterInfo.Nvidia, Backend.Amf => AdapterInfo.Amd, Backend.Qsv => AdapterInfo.Intel, _ => 0 };

    /// <summary>Takes GPU textures directly (no copy back to memory).</summary>
    public bool ZeroCopy => Backend is Backend.Nvenc or Backend.Amf;

    public bool Hardware => Backend != Backend.X264;

    public string BackendName => Backend switch { Backend.Nvenc => "NVIDIA NVENC", Backend.Amf => "AMD AMF", Backend.Qsv => "Intel Quick Sync", _ => "x264 (CPU)" };
    public string CodecName => Codec switch { Codec.H264 => "H.264", Codec.Hevc => "HEVC", _ => "AV1" };
    public string Key => $"{Backend}-{Codec}".ToLowerInvariant();
    public override string ToString() => $"{BackendName} · {CodecName}";

    public static readonly EncoderSpec[] All =
    [
        new(Backend.Nvenc, Codec.H264), new(Backend.Nvenc, Codec.Hevc), new(Backend.Nvenc, Codec.Av1),
        new(Backend.Amf, Codec.H264), new(Backend.Amf, Codec.Hevc), new(Backend.Amf, Codec.Av1),
        new(Backend.Qsv, Codec.H264), new(Backend.Qsv, Codec.Hevc), new(Backend.Qsv, Codec.Av1),
        new(Backend.X264, Codec.H264),
    ];

    public static EncoderSpec? FromKey(string? key) => All.FirstOrDefault(e => e.Key == key);
}

/// <summary>How the video should be encoded.</summary>
public sealed record VideoParams(int Width, int Height, int Fps, bool ConstantQuality, double Quality, int BitrateKbps, int Speed, int KeyframeSeconds = 2)
{
    /// <summary>The constant-quality value for this codec family: 0 = smallest file, 1 = near lossless.</summary>
    public int Cq(EncoderSpec e)
    {
        double q = Math.Clamp(Quality, 0, 1);
        (double worst, double best) = (e.Backend, e.Codec) switch
        {
            (Backend.X264, _) => (30, 12),
            (Backend.Amf, Codec.Av1) => (170, 60),
            (_, Codec.Av1) => (42, 18),
            (_, Codec.Hevc) => (32, 14),
            _ => (31, 13),
        };
        return (int)Math.Round(worst + (best - worst) * q);
    }
}

/// <summary>
/// A video encoder. Hardware encoders that read Direct3D textures get frames from a pool of GPU surfaces
/// (<see cref="NewGpuFrame"/>); the others get NV12 frames in memory (<see cref="NewMemoryFrame"/>).
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    AVCodecContext* _c;
    AVBufferRef* _device;
    AVBufferRef* _frames;
    AVBSFContext* _bsf;
    ID3D11Texture2D? _poolTexture;
    IntPtr _poolPtr;

    public EncoderSpec Spec { get; }
    public VideoParams Params { get; }
    public AVCodecContext* Context => _c;
    public AVRational TimeBase => _c->time_base;
    public bool UsesGpuFrames => _frames != null;
    public int PoolSize { get; }

    public VideoEncoder(Gpu gpu, EncoderSpec spec, VideoParams p, bool globalHeader, int poolSize = 24)
    {
        FF.Init();
        Spec = spec;
        Params = p;
        PoolSize = poolSize;
        var codec = ffmpeg.avcodec_find_encoder_by_name(spec.FFName);
        if (codec == null) throw new FFException($"{spec.FFName} is not in this FFmpeg build", -1);

        Exception? last = null;
        // some cards refuse B-frames; try with, then without
        foreach (int bframes in spec.Backend == Backend.Nvenc ? new[] { 2, 0 } : new[] { spec.Backend == Backend.X264 ? 2 : 0 })
        {
            try
            {
                Open(gpu, codec, bframes, globalHeader);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Free();
            }
        }
        throw last!;
    }

    void Open(Gpu gpu, AVCodec* codec, int bframes, bool globalHeader)
    {
        var p = Params;
        _c = ffmpeg.avcodec_alloc_context3(codec);
        _c->width = p.Width;
        _c->height = p.Height;
        _c->time_base = new AVRational { num = 1, den = p.Fps };
        _c->framerate = new AVRational { num = p.Fps, den = 1 };
        _c->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        _c->gop_size = Math.Max(1, p.Fps * p.KeyframeSeconds);
        _c->max_b_frames = bframes;
        _c->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        _c->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        _c->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        _c->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        if (globalHeader) _c->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (Spec.ZeroCopy)
        {
            _device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            var dev = (AVHWDeviceContext*)_device->data;
            var d3d = (AVD3D11VADeviceContext*)dev->hwctx;
            // FFmpeg releases the device when it is done with it
            Marshal.AddRef(gpu.Device.NativePointer);
            *(IntPtr*)&d3d->device = gpu.Device.NativePointer;
            FF.Check(ffmpeg.av_hwdevice_ctx_init(_device), "GPU device for the encoder");

            _frames = ffmpeg.av_hwframe_ctx_alloc(_device);
            var fc = (AVHWFramesContext*)_frames->data;
            fc->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            fc->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
            fc->width = p.Width;
            fc->height = p.Height;
            fc->initial_pool_size = PoolSize;
            var fh = (AVD3D11VAFramesContext*)fc->hwctx;
            fh->BindFlags = (uint)(BindFlags.RenderTarget);
            FF.Check(ffmpeg.av_hwframe_ctx_init(_frames), "GPU frame pool");

            _c->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            _c->hw_frames_ctx = ffmpeg.av_buffer_ref(_frames);
            _c->hw_device_ctx = ffmpeg.av_buffer_ref(_device);
        }
        else
        {
            _c->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        }

        AVDictionary* opts = null;
        ApplyRateControl(&opts);
        try
        {
            FF.Check(ffmpeg.avcodec_open2(_c, codec, &opts), $"open {Spec.FFName}");
        }
        finally { ffmpeg.av_dict_free(&opts); }
        // AMF (and some Quick Sync drivers) write no or wrong colour tags; stamp BT.709 TV range into the stream
        if (Spec.Backend is Backend.Amf or Backend.Qsv) OpenColourFix();
    }

    void OpenColourFix()
    {
        string name = Spec.Codec switch { Codec.H264 => "h264_metadata", Codec.Hevc => "hevc_metadata", _ => "av1_metadata" };
        var filter = ffmpeg.av_bsf_get_by_name(name);
        if (filter == null) return;
        AVBSFContext* b;
        FF.Check(ffmpeg.av_bsf_alloc(filter, &b), name);
        FF.Check(ffmpeg.avcodec_parameters_from_context(b->par_in, _c), name);
        b->time_base_in = _c->time_base;
        if (Spec.Codec == Codec.Av1)
        {
            ffmpeg.av_opt_set_int(b->priv_data, "color_primaries", 1, 0);
            ffmpeg.av_opt_set_int(b->priv_data, "transfer_characteristics", 1, 0);
            ffmpeg.av_opt_set_int(b->priv_data, "matrix_coefficients", 1, 0);
            ffmpeg.av_opt_set(b->priv_data, "color_range", "tv", 0);
        }
        else
        {
            ffmpeg.av_opt_set_int(b->priv_data, "colour_primaries", 1, 0);
            ffmpeg.av_opt_set_int(b->priv_data, "transfer_characteristics", 1, 0);
            ffmpeg.av_opt_set_int(b->priv_data, "matrix_coefficients", 1, 0);
            ffmpeg.av_opt_set_int(b->priv_data, "video_full_range_flag", 0, 0);
        }
        int r = ffmpeg.av_bsf_init(b);
        if (r < 0)
        {
            // some drivers hand out headers the filter cannot parse; then the stream keeps its own tags
            Paths.Log($"{name}: {FF.Error(r)}, colour tags left as the encoder wrote them");
            ffmpeg.av_bsf_free(&b);
            return;
        }
        _bsf = b;
    }

    /// <summary>The stream description for the file (after the colour fix, when there is one).</summary>
    public AVCodecParameters* Parameters
    {
        get
        {
            if (_bsf != null) return _bsf->par_out;
            if (_par == null)
            {
                _par = ffmpeg.avcodec_parameters_alloc();
                FF.Check(ffmpeg.avcodec_parameters_from_context(_par, _c), "stream parameters");
            }
            return _par;
        }
    }

    AVCodecParameters* _par;

    void ApplyRateControl(AVDictionary** o)
    {
        var p = Params;
        int cq = p.Cq(Spec);
        long bitrate = (long)p.BitrateKbps * 1000;
        switch (Spec.Backend)
        {
            case Backend.Nvenc:
                FF.Set(o, "preset", p.Speed switch { 0 => "p2", 2 => "p6", _ => "p4" });
                FF.Set(o, "tune", "hq");
                FF.Set(o, "rc", "vbr");
                FF.Set(o, "spatial-aq", "1");
                if (p.ConstantQuality) { FF.Set(o, "cq", cq); _c->bit_rate = 0; }
                else { _c->bit_rate = bitrate; _c->rc_max_rate = bitrate * 3 / 2; _c->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2); }
                break;
            case Backend.Amf:
                FF.Set(o, "usage", "transcoding");
                FF.Set(o, "quality", p.Speed switch { 0 => "speed", 2 => "quality", _ => "balanced" });
                if (p.ConstantQuality)
                {
                    FF.Set(o, "rc", "cqp");
                    FF.Set(o, "qp_i", Math.Max(0, cq - (Spec.Codec == Codec.Av1 ? 8 : 2)));
                    FF.Set(o, "qp_p", cq);
                    if (Spec.Codec == Codec.H264) FF.Set(o, "qp_b", cq + 2);
                }
                else
                {
                    FF.Set(o, "rc", "vbr_peak");
                    _c->bit_rate = bitrate; _c->rc_max_rate = bitrate * 3 / 2; _c->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
                }
                break;
            case Backend.Qsv:
                FF.Set(o, "preset", p.Speed switch { 0 => "veryfast", 2 => "slower", _ => "medium" });
                if (p.ConstantQuality) { _c->global_quality = cq; _c->bit_rate = 0; }
                else { _c->bit_rate = bitrate; _c->rc_max_rate = bitrate * 3 / 2; _c->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2); }
                break;
            default:
                // screen recordings run in real time, so x264 stays on its quick presets
                FF.Set(o, "preset", p.Speed switch { 0 => "ultrafast", 2 => "faster", _ => "veryfast" });
                if (p.ConstantQuality) FF.Set(o, "crf", cq);
                else { _c->bit_rate = bitrate; _c->rc_max_rate = bitrate * 3 / 2; _c->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2); }
                break;
        }
    }

    /// <summary>
    /// A free surface from the GPU pool and where it lives (a slice of one texture array), or null when every
    /// surface is still queued in the encoder.
    /// </summary>
    public AVFrame* NewGpuFrame(out ID3D11Texture2D texture, out uint slice)
    {
        texture = null!;
        slice = 0;
        var f = ffmpeg.av_frame_alloc();
        if (ffmpeg.av_hwframe_get_buffer(_frames, f, 0) < 0)
        {
            ffmpeg.av_frame_free(&f);
            return null;
        }
        var ptr = (IntPtr)f->data[0];
        if (ptr != _poolPtr)
        {
            _poolTexture?.Dispose();
            Marshal.AddRef(ptr);
            _poolTexture = new ID3D11Texture2D(ptr);
            _poolPtr = ptr;
        }
        texture = _poolTexture!;
        slice = (uint)(nint)f->data[1];
        return f;
    }

    /// <summary>An empty NV12 frame in memory.</summary>
    public AVFrame* NewMemoryFrame()
    {
        var f = ffmpeg.av_frame_alloc();
        f->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        f->width = Params.Width;
        f->height = Params.Height;
        FF.Check(ffmpeg.av_frame_get_buffer(f, 0), "frame buffer");
        return f;
    }

    /// <summary>Encodes one frame (null flushes) and hands every finished packet to <paramref name="write"/>.</summary>
    public void Encode(AVFrame* frame, Action<IntPtr> write)
    {
        if (frame != null)
        {
            frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        }
        if (frame == null) _flushing = true;
        int ret = ffmpeg.avcodec_send_frame(_c, frame);
        if (ret == FF.EAGAIN)
        {
            Drain(write);
            ret = ffmpeg.avcodec_send_frame(_c, frame);
        }
        if (ret < 0 && ret != FF.EOF) FF.Check(ret, "encode video");
        Drain(write);
    }

    void Drain(Action<IntPtr> write)
    {
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (true)
            {
                int r = ffmpeg.avcodec_receive_packet(_c, pkt);
                if (r == FF.EAGAIN || r == FF.EOF) break;
                FF.Check(r, "receive video");
                Pass(pkt, write);
            }
            if (_flushing && _bsf != null) Pass(null, write);
        }
        finally { ffmpeg.av_packet_free(&pkt); }
    }

    bool _flushing;

    void Pass(AVPacket* pkt, Action<IntPtr> write)
    {
        if (_bsf == null)
        {
            write((IntPtr)pkt);
            ffmpeg.av_packet_unref(pkt);
            return;
        }
        FF.Check(ffmpeg.av_bsf_send_packet(_bsf, pkt), "colour fix");
        var outp = ffmpeg.av_packet_alloc();
        try
        {
            while (ffmpeg.av_bsf_receive_packet(_bsf, outp) == 0)
            {
                write((IntPtr)outp);
                ffmpeg.av_packet_unref(outp);
            }
        }
        finally { ffmpeg.av_packet_free(&outp); }
    }

    void Free()
    {
        if (_bsf != null) { var b = _bsf; ffmpeg.av_bsf_free(&b); _bsf = null; }
        if (_par != null) { var pr = _par; ffmpeg.avcodec_parameters_free(&pr); _par = null; }
        if (_c != null) { var c = _c; ffmpeg.avcodec_free_context(&c); _c = null; }
        if (_frames != null) { var f = _frames; ffmpeg.av_buffer_unref(&f); _frames = null; }
        if (_device != null) { var d = _device; ffmpeg.av_buffer_unref(&d); _device = null; }
    }

    public void Dispose()
    {
        Free();
        _poolTexture?.Dispose();
        _poolTexture = null;
    }

    /// <summary>
    /// Opens the encoder for a moment on a small picture to see if this computer can use it.
    /// Returns null when it works, otherwise why not.
    /// </summary>
    public static string? Probe(EncoderSpec spec, Func<uint, Gpu?> gpuFor)
    {
        try
        {
            FF.Init();
            if (ffmpeg.avcodec_find_encoder_by_name(spec.FFName) == null) return "not in this FFmpeg build";
            var gpu = gpuFor(spec.Vendor);
            if (gpu == null) return "no matching graphics card";
            var p = new VideoParams(320, 240, 30, true, 0.5, 4000, 1);
            using var enc = new VideoEncoder(gpu, spec, p, globalHeader: true, poolSize: 4);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>AAC at 48 kHz stereo; takes interleaved float samples.</summary>
public sealed unsafe class AudioEncoder : IDisposable
{
    AVCodecContext* _c;
    AVFrame* _frame;

    public AVCodecContext* Context => _c;
    public AVRational TimeBase => _c->time_base;
    public int FrameSize => _c->frame_size;
    public const int Rate = 48000;

    public AudioEncoder(int bitrate, bool globalHeader)
    {
        FF.Init();
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        _c = ffmpeg.avcodec_alloc_context3(codec);
        _c->sample_rate = Rate;
        _c->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        AVChannelLayout layout;
        ffmpeg.av_channel_layout_default(&layout, 2);
        _c->ch_layout = layout;
        _c->bit_rate = bitrate;
        _c->time_base = new AVRational { num = 1, den = Rate };
        if (globalHeader) _c->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        FF.Check(ffmpeg.avcodec_open2(_c, codec, null), "open AAC");

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _frame->nb_samples = _c->frame_size;
        _frame->sample_rate = Rate;
        _frame->ch_layout = layout;
        FF.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "audio frame");
    }

    /// <summary>Encodes <paramref name="frames"/> (at most <see cref="FrameSize"/>) stereo samples that start at sample <paramref name="pts"/>.</summary>
    public void Encode(float* interleaved, int frames, long pts, Action<IntPtr> write)
    {
        FF.Check(ffmpeg.av_frame_make_writable(_frame), "audio frame");
        _frame->nb_samples = frames;
        var l = (float*)_frame->data[0];
        var r = (float*)_frame->data[1];
        for (int i = 0; i < frames; i++)
        {
            l[i] = interleaved[2 * i];
            r[i] = interleaved[2 * i + 1];
        }
        _frame->pts = pts;
        Send(_frame, write);
    }

    public void Flush(Action<IntPtr> write) => Send(null, write);

    void Send(AVFrame* f, Action<IntPtr> write)
    {
        int ret = ffmpeg.avcodec_send_frame(_c, f);
        if (ret < 0 && ret != FF.EOF) FF.Check(ret, "encode audio");
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (true)
            {
                int r = ffmpeg.avcodec_receive_packet(_c, pkt);
                if (r == FF.EAGAIN || r == FF.EOF) break;
                FF.Check(r, "receive audio");
                write((IntPtr)pkt);
                ffmpeg.av_packet_unref(pkt);
            }
        }
        finally { ffmpeg.av_packet_free(&pkt); }
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_c != null) { var c = _c; ffmpeg.avcodec_free_context(&c); _c = null; }
    }
}

/// <summary>Writes the encoded streams into one MP4 or MKV file. Safe to call from several threads.</summary>
public sealed unsafe class Muxer : IDisposable
{
    AVFormatContext* _oc;
    readonly object _lock = new();
    readonly List<AVRational> _encoderTb = [];
    bool _open, _finished;

    public string Path { get; }
    public long BytesWritten => _oc != null && _oc->pb != null ? ffmpeg.avio_tell(_oc->pb) : 0;

    public Muxer(string path, bool mkv)
    {
        FF.Init();
        Path = path;
        AVFormatContext* oc = null;
        FF.Check(ffmpeg.avformat_alloc_output_context2(&oc, null, mkv ? "matroska" : "mp4", path), "output file");
        _oc = oc;
    }

    public bool NeedsGlobalHeader => (_oc->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

    public int AddStream(AVCodecContext* enc, string? title = null, bool isDefault = true)
    {
        var st = ffmpeg.avformat_new_stream(_oc, null);
        FF.Check(ffmpeg.avcodec_parameters_from_context(st->codecpar, enc), "stream parameters");
        st->time_base = enc->time_base;
        if (enc->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            st->avg_frame_rate = enc->framerate;
            st->r_frame_rate = enc->framerate;
            // QuickTime and iPhones only play HEVC tagged hvc1
            if (enc->codec_id == AVCodecID.AV_CODEC_ID_HEVC && _oc->oformat->name != null && Marshal.PtrToStringUTF8((IntPtr)_oc->oformat->name) == "mp4")
                st->codecpar->codec_tag = (uint)('h' | 'v' << 8 | 'c' << 16 | '1' << 24);
        }
        if (title != null)
        {
            ffmpeg.av_dict_set(&st->metadata, "title", title, 0);
            ffmpeg.av_dict_set(&st->metadata, "handler_name", title, 0);
        }
        st->disposition = isDefault ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
        _encoderTb.Add(enc->time_base);
        return st->index;
    }

    /// <summary>Adds the video stream from an encoder (uses its fixed-up parameters).</summary>
    public int AddVideo(VideoEncoder enc)
    {
        var c = enc.Context;
        var st = ffmpeg.avformat_new_stream(_oc, null);
        FF.Check(ffmpeg.avcodec_parameters_copy(st->codecpar, enc.Parameters), "stream parameters");
        st->time_base = c->time_base;
        st->avg_frame_rate = c->framerate;
        st->r_frame_rate = c->framerate;
        if (c->codec_id == AVCodecID.AV_CODEC_ID_HEVC && Marshal.PtrToStringUTF8((IntPtr)_oc->oformat->name) == "mp4")
            st->codecpar->codec_tag = (uint)('h' | 'v' << 8 | 'c' << 16 | '1' << 24);
        st->disposition = ffmpeg.AV_DISPOSITION_DEFAULT;
        _encoderTb.Add(c->time_base);
        return st->index;
    }

    public void Start()
    {
        FF.Check(ffmpeg.avio_open(&_oc->pb, Path, ffmpeg.AVIO_FLAG_WRITE), "create " + Path);
        ffmpeg.av_dict_set(&_oc->metadata, "encoder", "SweaxIris", 0);
        ffmpeg.av_dict_set(&_oc->metadata, "creation_time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), 0);
        FF.Check(ffmpeg.avformat_write_header(_oc, null), "write header");
        _open = true;
    }

    public void Write(IntPtr packet, int stream)
    {
        var pkt = (AVPacket*)packet;
        lock (_lock)
        {
            if (!_open || _finished) return;
            var st = _oc->streams[stream];
            ffmpeg.av_packet_rescale_ts(pkt, _encoderTb[stream], st->time_base);
            pkt->stream_index = stream;
            int r = ffmpeg.av_interleaved_write_frame(_oc, pkt);
            if (r < 0) Paths.Log("write packet: " + FF.Error(r));
        }
    }

    public void Finish()
    {
        lock (_lock)
        {
            if (!_open || _finished) return;
            _finished = true;
            FF.Check(ffmpeg.av_write_trailer(_oc), "finish file");
            ffmpeg.avio_closep(&_oc->pb);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_oc == null) return;
            if (_open && !_finished)
            {
                try { ffmpeg.av_write_trailer(_oc); } catch { }
                ffmpeg.avio_closep(&_oc->pb);
            }
            ffmpeg.avformat_free_context(_oc);
            _oc = null;
        }
    }
}
