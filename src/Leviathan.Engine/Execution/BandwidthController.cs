using System.Collections.Concurrent;
using Leviathan.Contracts;

namespace Leviathan.Engine.Execution;

/// <summary>
/// Paces the bytes of one budget: each chunk reserves the time it would take at the current rate, and the caller waits until its
/// turn. Idle time earns no credit, so a limit never lets a burst through. Shared by every job that draws on the same budget (all
/// transfers, or everything going to one NAS), so the budget holds however many jobs run. Thread-safe.
/// </summary>
public sealed class ByteRateLimiter
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private long _freeAt;

    /// <summary>Creates a limiter on <paramref name="time"/>.</summary>
    public ByteRateLimiter(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>
    /// Waits until <paramref name="bytes"/> may have been sent at <paramref name="bytesPerSecond"/> (0 = no wait). The rate is passed in
    /// each time, so a changed limit or a schedule window takes effect on the next chunk.
    /// </summary>
    public async ValueTask WaitAsync(long bytes, long bytesPerSecond, CancellationToken cancellationToken)
    {
        if (bytesPerSecond <= 0 || bytes <= 0)
        {
            return;
        }

        TimeSpan delay;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            var start = Math.Max(_freeAt, now);
            _freeAt = start + (long)(bytes / (double)bytesPerSecond * _time.TimestampFrequency);
            delay = _time.GetElapsedTime(now, start);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The bandwidth limits of the worker (spec §12.3): a global rule, a rule per destination device, and a limit per job, each with an
/// optional schedule. A copy's throttle draws from all that apply, and the slowest wins. Settings can change while copies run.
/// </summary>
public sealed class BandwidthController
{
    private readonly TimeProvider _time;
    private readonly ByteRateLimiter _global;
    private readonly ConcurrentDictionary<string, ByteRateLimiter> _devices = new(StringComparer.OrdinalIgnoreCase);
    private volatile BandwidthSettings _settings = new();

    /// <summary>Creates a controller with no limits.</summary>
    public BandwidthController(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _global = new ByteRateLimiter(_time);
    }

    /// <summary>The current settings.</summary>
    public BandwidthSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _settings = value;
        }
    }

    /// <summary>The limit in bytes per second that applies to a job right now; 0 = unlimited.</summary>
    public long EffectiveLimit(string? deviceKey, long jobLimit)
    {
        var now = _time.GetLocalNow().DateTime;
        var settings = _settings;
        var device = deviceKey is not null && settings.PerDevice.TryGetValue(deviceKey, out var rule) ? rule.LimitAt(now) : 0;
        return new[] { jobLimit, device, settings.Global.LimitAt(now) }.Where(l => l > 0).DefaultIfEmpty(0).Min();
    }

    /// <summary>
    /// A throttle for one job: call it after each chunk with the chunk's size. It waits on the job's own limit, then the device's, then
    /// the global one, each shared with whoever else draws on that budget.
    /// </summary>
    public Func<int, CancellationToken, ValueTask> CreateThrottle(string? deviceKey, long jobLimit)
    {
        var own = new ByteRateLimiter(_time);
        var device = deviceKey is null ? null : _devices.GetOrAdd(deviceKey, _ => new ByteRateLimiter(_time));
        return async (bytes, ct) =>
        {
            var now = _time.GetLocalNow().DateTime;
            var settings = _settings;
            await own.WaitAsync(bytes, jobLimit, ct).ConfigureAwait(false);
            if (device is not null && deviceKey is not null && settings.PerDevice.TryGetValue(deviceKey, out var rule))
            {
                await device.WaitAsync(bytes, rule.LimitAt(now), ct).ConfigureAwait(false);
            }

            await _global.WaitAsync(bytes, settings.Global.LimitAt(now), ct).ConfigureAwait(false);
        };
    }
}
