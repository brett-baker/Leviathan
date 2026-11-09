namespace Leviathan.Testing;

/// <summary>
/// A clock that only moves when the test calls <see cref="Advance"/>. Timers (and therefore
/// <c>Task.Delay(span, timeProvider)</c>) fire during <see cref="Advance"/>, in due-time order.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _ticks;

    /// <summary>Creates the clock at <paramref name="start"/> (default 2026-01-01 00:00 UTC).</summary>
    public ManualTimeProvider(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _ticks;
        }
    }

    /// <summary>How many timers are waiting to fire, so a test can wait until code has started its delay.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Moves time forward, firing due timers along the way.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        long target;
        lock (_lock)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_lock)
            {
                due = _timers.Where(t => t.DueTicks <= target).OrderBy(t => t.DueTicks).FirstOrDefault();
                if (due is null)
                {
                    _now += TimeSpan.FromTicks(target - _ticks);
                    _ticks = target;
                    return;
                }

                if (due.DueTicks > _ticks)
                {
                    _now += TimeSpan.FromTicks(due.DueTicks - _ticks);
                    _ticks = due.DueTicks;
                }

                if (due.PeriodTicks > 0)
                {
                    due.DueTicks += due.PeriodTicks;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            due.Fire();
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.DueTicks = _ticks + Math.Max(0, dueTime.Ticks);
            timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            _timers.Add(timer);
        }
    }

    private void Unschedule(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueTicks { get; set; }

        public long PeriodTicks { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Unschedule(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
