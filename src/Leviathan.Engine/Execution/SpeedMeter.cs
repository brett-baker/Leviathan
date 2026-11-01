namespace Leviathan.Engine.Execution;

/// <summary>
/// Measures transfer speed from cumulative byte counts. Keeps one sample per second for the last two minutes, which
/// feeds the speed sparkline (spec §12.6), and a smoothed current speed for the ETA. Thread-safe.
/// </summary>
public sealed class SpeedMeter
{
    /// <summary>Samples kept: one per second for two minutes.</summary>
    public const int HistoryLength = 120;

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly double[] _history = new double[HistoryLength];
    private int _historyCount;
    private int _historyStart;
    private long _lastSampleTimestamp;
    private long _lastSampleBytes;
    private long _latestBytes;
    private double _smoothed;
    private bool _started;

    /// <summary>Creates a meter.</summary>
    public SpeedMeter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Smoothed bytes per second.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _smoothed;
            }
        }
    }

    /// <summary>Records the cumulative number of bytes transferred so far.</summary>
    public void Record(long totalBytes)
    {
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            if (!_started)
            {
                _started = true;
                _lastSampleTimestamp = now;
                _lastSampleBytes = totalBytes;
                _latestBytes = totalBytes;
                return;
            }

            _latestBytes = totalBytes;
            var elapsed = _time.GetElapsedTime(_lastSampleTimestamp, now);
            while (elapsed >= TimeSpan.FromSeconds(1))
            {
                // Progress is reported several times a second, so a stalled share naturally produces zero samples.
                // If reports were sparse, spread the bytes evenly over the whole seconds that passed.
                var seconds = (int)Math.Floor(elapsed.TotalSeconds);
                var perSecond = (_latestBytes - _lastSampleBytes) / elapsed.TotalSeconds;
                for (var i = 0; i < seconds; i++)
                {
                    AddSample(perSecond);
                }

                _smoothed = _smoothed == 0 ? perSecond : (_smoothed * 0.6) + (perSecond * 0.4);
                _lastSampleTimestamp = now;
                _lastSampleBytes = _latestBytes;
                elapsed = TimeSpan.Zero;
            }
        }
    }

    /// <summary>Samples, oldest first.</summary>
    public IReadOnlyList<double> History()
    {
        lock (_lock)
        {
            var result = new double[_historyCount];
            for (var i = 0; i < _historyCount; i++)
            {
                result[i] = _history[(_historyStart + i) % HistoryLength];
            }

            return result;
        }
    }

    /// <summary>Estimated time to transfer <paramref name="remainingBytes"/> at the current speed, or null when unknown.</summary>
    public TimeSpan? EstimateRemaining(long remainingBytes)
    {
        var speed = BytesPerSecond;
        if (remainingBytes < 0 || speed <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Min(remainingBytes / speed, TimeSpan.FromDays(30).TotalSeconds));
    }

    private void AddSample(double value)
    {
        if (_historyCount < HistoryLength)
        {
            _history[(_historyStart + _historyCount) % HistoryLength] = value;
            _historyCount++;
        }
        else
        {
            _history[_historyStart] = value;
            _historyStart = (_historyStart + 1) % HistoryLength;
        }
    }
}
