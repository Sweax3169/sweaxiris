using SweaxIris.Native;

namespace SweaxIris.Core;

/// <summary>The performance counter in 100 ns units, the same clock Windows stamps audio packets and captured frames with.</summary>
public static class Clock
{
    static readonly long Freq;

    static Clock() => Win32.QueryPerformanceFrequency(out Freq);

    public static long Now
    {
        get
        {
            Win32.QueryPerformanceCounter(out long c);
            return Freq == 10_000_000 ? c : (long)((decimal)c * 10_000_000 / Freq);
        }
    }
}

/// <summary>
/// Maps the wall clock to the recording's own clock, which stands still while paused. Video frames and
/// audio packets are both placed with it, so they stay together across any number of pauses.
/// </summary>
public sealed class Timeline
{
    readonly object _lock = new();
    readonly List<(long From, long To)> _pauses = [];
    long _start = -1, _pausedAt = -1, _end = -1;

    public bool Started => _start >= 0;
    public bool Paused { get { lock (_lock) return _pausedAt >= 0; } }

    public void Begin(long now) { lock (_lock) { _start = now; _pauses.Clear(); _pausedAt = -1; _end = -1; } }
    public void Pause(long now) { lock (_lock) if (_pausedAt < 0 && _end < 0) _pausedAt = now; }

    public void Resume(long now)
    {
        lock (_lock)
        {
            if (_pausedAt < 0) return;
            _pauses.Add((_pausedAt, now));
            _pausedAt = -1;
        }
    }

    /// <summary>Freezes the clock for good; nothing after this moment is recorded.</summary>
    public long End(long now)
    {
        lock (_lock)
        {
            if (_end < 0) _end = _pausedAt >= 0 ? _pausedAt : now;
            return ActiveLocked(_end, allowEnd: true);
        }
    }

    /// <summary>Recording time (100 ns) at wall time <paramref name="t"/>, or -1 if nothing was being recorded then.</summary>
    public long Active(long t)
    {
        lock (_lock) return ActiveLocked(t, allowEnd: false);
    }

    long ActiveLocked(long t, bool allowEnd)
    {
        if (_start < 0 || t < _start) return -1;
        if (_end >= 0 && (t > _end || (t == _end && !allowEnd))) return -1;
        if (_pausedAt >= 0 && t >= _pausedAt) return -1;
        long paused = 0;
        foreach (var (from, to) in _pauses)
        {
            if (t >= to) paused += to - from;
            else if (t >= from) return -1;
        }
        return t - _start - paused;
    }

    /// <summary>How much has been recorded so far (holds still while paused).</summary>
    public long Elapsed
    {
        get
        {
            lock (_lock)
            {
                if (_start < 0) return 0;
                long t = _end >= 0 ? _end : _pausedAt >= 0 ? _pausedAt : Clock.Now;
                long paused = 0;
                foreach (var (from, to) in _pauses) if (t >= to) paused += to - from;
                return Math.Max(0, t - _start - paused);
            }
        }
    }
}
