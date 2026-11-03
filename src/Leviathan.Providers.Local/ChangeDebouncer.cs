namespace Leviathan.Providers.Local;

/// <summary>
/// Turns a stream of file-system events into a few calls (spec §3.2): the call comes <c>delay</c> after the first event, so a
/// burst is one call, and never sooner than <c>minGap</c> after the previous call, so a file that is being written to (an event
/// per block) cannot make a listing refresh over and over.
/// </summary>
public sealed class ChangeDebouncer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _minGap;
    private readonly Action _fire;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long? _lastFired;
    private bool _disposed;

    /// <summary>Creates the debouncer.</summary>
    /// <param name="time">Clock and timers.</param>
    /// <param name="delay">How long after the first event of a burst the call is made.</param>
    /// <param name="minGap">The least time between two calls.</param>
    /// <param name="fire">Called on a timer thread.</param>
    public ChangeDebouncer(TimeProvider time, TimeSpan delay, TimeSpan minGap, Action fire)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(fire);
        _time = time;
        _delay = delay;
        _minGap = minGap;
        _fire = fire;
    }

    /// <summary>Records an event; safe from any thread.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            var wait = _delay;
            if (_lastFired is { } last)
            {
                var sinceLast = _time.GetElapsedTime(last);
                if (sinceLast + _delay < _minGap)
                {
                    wait = _minGap - sinceLast;
                }
            }

            _timer = _time.CreateTimer(_ => Fire(), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            _lastFired = _time.GetTimestamp();
        }

        _fire();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
