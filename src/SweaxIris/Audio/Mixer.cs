using SweaxIris.Core;
using SweaxIris.Encode;

namespace SweaxIris.Audio;

/// <summary>
/// One source's lane on the recording timeline. Packets are placed where their timestamps say they
/// belong; small clock drift between the sound card and the system clock is absorbed by stretching
/// the audio a hair (at most 0.2 %), and real gaps are left silent.
/// </summary>
public sealed unsafe class MixerInput
{
    const int RingFrames = 1 << 17; // ~2.7 s
    const int Mask = RingFrames - 1;
    readonly float[] _ring = new float[RingFrames * 2];
    readonly object _lock = new();
    long _next = long.MinValue;
    double _avgDev, _phase;
    // the last three frames of the previous packet, for the interpolator
    readonly float[] _hist = new float[6];
    long _readPos;

    public string Name { get; }
    /// <summary>Volume, 0..2 (1 = as heard).</summary>
    public volatile float Volume = 1;
    public volatile bool Muted;
    float _gain = -1;

    /// <summary>Level after volume, for the meters (0..1).</summary>
    public float Peak => _peak;
    volatile float _peak;
    public long Resyncs { get; private set; }

    public MixerInput(string name) => Name = name;

    /// <summary>Adds a packet whose first frame belongs at timeline sample <paramref name="pos"/>.</summary>
    public void Push(float* data, int frames, long pos)
    {
        lock (_lock)
        {
            if (_next == long.MinValue) _next = pos;
            long dev = pos - _next;
            // a gap of silence (loopback sends nothing while nothing plays) or a big jump: start over at the timestamp
            if (dev > 720 || dev < -1440)
            {
                _next = pos;
                _avgDev = 0;
                _phase = 0;
                for (int k = 0; k < 3; k++) { _hist[2 * k] = data[0]; _hist[2 * k + 1] = data[1]; }
                Resyncs++;
            }
            else _avgDev = _avgDev * 0.97 + dev * 0.03;
            double ratio = 1 + Math.Clamp(_avgDev * 4e-6, -0.002, 0.002);

            // 4-point Hermite interpolation; t walks the input, -3..-1 are the previous packet's last frames
            double step = 1 / ratio, t = _phase;
            long w = _next;
            while ((int)Math.Floor(t) + 2 <= frames - 1)
            {
                int i0 = (int)Math.Floor(t);
                float f = (float)(t - i0);
                for (int ch = 0; ch < 2; ch++)
                {
                    float ym1 = S(data, i0 - 1, ch), y0 = S(data, i0, ch), y1 = S(data, i0 + 1, ch), y2 = S(data, i0 + 2, ch);
                    float c1 = 0.5f * (y1 - ym1), c2 = ym1 - 2.5f * y0 + 2 * y1 - 0.5f * y2, c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
                    _out[ch] = ((c3 * f + c2) * f + c1) * f + y0;
                }
                Put(w++, _out[0], _out[1]);
                t += step;
            }
            _phase = t - frames;
            _next = w;
            // keep the last three frames (of history + this packet) for the next one
            float h0l = S(data, frames - 3, 0), h0r = S(data, frames - 3, 1);
            float h1l = S(data, frames - 2, 0), h1r = S(data, frames - 2, 1);
            float h2l = S(data, frames - 1, 0), h2r = S(data, frames - 1, 1);
            _hist[0] = h0l; _hist[1] = h0r; _hist[2] = h1l; _hist[3] = h1r; _hist[4] = h2l; _hist[5] = h2r;
        }
    }

    readonly float[] _out = new float[2];

    float S(float* data, int i, int ch) => i < 0 ? _hist[(3 + i) * 2 + ch] : data[2 * i + ch];

    void Put(long pos, float l, float r)
    {
        if (pos < _readPos || pos >= _readPos + RingFrames) return; // too late or too far ahead
        int i = (int)(pos & Mask) * 2;
        _ring[i] = l;
        _ring[i + 1] = r;
    }

    /// <summary>Takes <paramref name="frames"/> frames starting at <paramref name="pos"/> into <paramref name="dst"/> with volume applied.</summary>
    public void Take(long pos, int frames, float[] dst)
    {
        float target = Muted ? 0 : Volume;
        if (_gain < 0) _gain = target;
        float g0 = _gain, peak = 0;
        lock (_lock)
        {
            for (int k = 0; k < frames; k++)
            {
                long p = pos + k;
                int i = (int)(p & Mask) * 2;
                // ramp the volume over the block so mute and slider moves never click
                float g = g0 + (target - g0) * (k + 1) / frames;
                float l = _ring[i] * g, r = _ring[i + 1] * g;
                _ring[i] = 0; _ring[i + 1] = 0;
                dst[2 * k] = l; dst[2 * k + 1] = r;
                float a = Math.Max(Math.Abs(l), Math.Abs(r));
                if (a > peak) peak = a;
            }
            _readPos = pos + frames;
        }
        _gain = target;
        _peak = Math.Max(peak, _peak * 0.8f);
    }
}

/// <summary>One audio track in the file and which inputs are summed into it.</summary>
public sealed class MixerTrack(string title, AudioEncoder encoder, int stream, MixerInput[] inputs)
{
    public string Title { get; } = title;
    public AudioEncoder Encoder { get; } = encoder;
    public int Stream { get; } = stream;
    public MixerInput[] Inputs { get; } = inputs;
}

/// <summary>
/// Pulls every input along the recording clock in AAC-sized blocks, sums them per track and encodes.
/// It stays a little behind real time so late packets still land in place.
/// </summary>
public sealed unsafe class Mixer : IDisposable
{
    public const int Rate = AudioEncoder.Rate;
    const long Latency = Rate / 8; // 125 ms

    readonly Timeline _timeline;
    readonly MixerTrack[] _tracks;
    readonly MixerInput[] _inputs;
    readonly Muxer _muxer;
    readonly Thread _thread;
    readonly float[][] _scratch;
    readonly float[] _sum;
    volatile bool _stop;
    long _endSample = -1;
    long _pos;

    public long SamplesWritten => _pos;
    public Exception? Failure { get; private set; }

    public Mixer(Timeline timeline, MixerTrack[] tracks, Muxer muxer)
    {
        _timeline = timeline;
        _tracks = tracks;
        _muxer = muxer;
        _inputs = tracks.SelectMany(t => t.Inputs).Distinct().ToArray();
        int block = tracks.Length > 0 ? tracks[0].Encoder.FrameSize : 1024;
        _scratch = _inputs.Select(_ => new float[block * 2]).ToArray();
        _sum = new float[block * 2];
        _thread = new Thread(Run) { IsBackground = true, Name = "SweaxIris.Mixer", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    /// <summary>Recording time (100 ns) to timeline samples.</summary>
    public static long ToSamples(long active100ns) => active100ns * Rate / 10_000_000;

    void Run()
    {
        try
        {
            int block = _tracks[0].Encoder.FrameSize;
            while (true)
            {
                Thread.Sleep(5);
                long end = Interlocked.Read(ref _endSample);
                long target = end >= 0 ? end : ToSamples(_timeline.Elapsed) - Latency;
                while (_pos + block <= target) MixBlock(block);
                if (end >= 0 && _stop)
                {
                    int rest = (int)(end - _pos);
                    if (rest > 0) MixBlock(rest);
                    foreach (var t in _tracks) t.Encoder.Flush(p => _muxer.Write(p, t.Stream));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
            Paths.Log("mixer: " + ex);
        }
    }

    void MixBlock(int frames)
    {
        for (int i = 0; i < _inputs.Length; i++) _inputs[i].Take(_pos, frames, _scratch[i]);
        foreach (var track in _tracks)
        {
            Array.Clear(_sum, 0, frames * 2);
            foreach (var input in track.Inputs)
            {
                var src = _scratch[Array.IndexOf(_inputs, input)];
                for (int k = 0; k < frames * 2; k++) _sum[k] += src[k];
            }
            // soft limit so a loud game plus a loud voice bends instead of clipping
            for (int k = 0; k < frames * 2; k++)
            {
                float x = _sum[k];
                if (x > 0.95f || x < -0.95f) _sum[k] = MathF.Sign(x) * (0.95f + 0.05f * MathF.Tanh((MathF.Abs(x) - 0.95f) / 0.05f));
            }
            fixed (float* s = _sum) track.Encoder.Encode(s, frames, _pos, p => _muxer.Write(p, track.Stream));
        }
        _pos += frames;
    }

    /// <summary>Mixes up to <paramref name="endActive100ns"/>, flushes the encoders and waits for it to finish.</summary>
    public void Finish(long endActive100ns)
    {
        // give packets still in flight from the sound cards a moment to arrive
        Thread.Sleep(160);
        Interlocked.Exchange(ref _endSample, ToSamples(endActive100ns));
        _stop = true;
        _thread.Join(10_000);
    }

    public void Dispose()
    {
        _stop = true;
        if (_endSample < 0) Interlocked.Exchange(ref _endSample, _pos);
        if (_thread.IsAlive) _thread.Join(2000);
    }
}
