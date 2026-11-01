namespace Leviathan.Core.Resilience;

/// <summary>Health of a server or share as shown in the tree, address bar and status bar (spec §3.3).</summary>
public enum ShareHealth
{
    /// <summary>No information yet.</summary>
    Unknown,

    /// <summary>Responding normally.</summary>
    Online,

    /// <summary>Responding, but slower than the threshold.</summary>
    Slow,

    /// <summary>Not responding. Calls fail fast until the next probe succeeds.</summary>
    Unreachable,

    /// <summary>Credentials needed.</summary>
    AuthenticationRequired,
}

/// <summary>
/// Tracks one server's health from real I/O results: an exponentially weighted latency average decides Online vs Slow,
/// consecutive failures mark it Unreachable, and while unreachable new calls fail fast and a back-off schedule decides
/// when the next probe may try again. Thread-safe.
/// </summary>
public sealed class ShareHealthTracker
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly BackoffPolicy _backoff;
    private double _latencyMs = double.NaN;
    private int _consecutiveFailures;
    private int _probeAttempts;
    private long _nextProbeTimestamp;

    /// <summary>Creates a tracker.</summary>
    public ShareHealthTracker(string server, TimeProvider? time = null, BackoffPolicy? backoff = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        Server = server;
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? BackoffPolicy.Network;
    }

    /// <summary>The server this tracker describes.</summary>
    public string Server { get; }

    /// <summary>Latency above which the share is reported Slow.</summary>
    public TimeSpan SlowThreshold { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Consecutive failures that mark the share Unreachable.</summary>
    public int FailuresBeforeUnreachable { get; init; } = 2;

    /// <summary>Current health.</summary>
    public ShareHealth Health { get; private set; } = ShareHealth.Unknown;

    /// <summary>Smoothed latency, or null before the first success.</summary>
    public TimeSpan? Latency
    {
        get
        {
            lock (_lock)
            {
                return double.IsNaN(_latencyMs) ? null : TimeSpan.FromMilliseconds(_latencyMs);
            }
        }
    }

    /// <summary>Raised when <see cref="Health"/> changes.</summary>
    public event EventHandler<ShareHealth>? HealthChanged;

    /// <summary>Records a successful operation and its duration.</summary>
    public void RecordSuccess(TimeSpan duration)
    {
        ShareHealth next;
        lock (_lock)
        {
            var ms = duration.TotalMilliseconds;
            _latencyMs = double.IsNaN(_latencyMs) ? ms : (_latencyMs * 0.7) + (ms * 0.3);
            _consecutiveFailures = 0;
            _probeAttempts = 0;
            next = _latencyMs > SlowThreshold.TotalMilliseconds ? ShareHealth.Slow : ShareHealth.Online;
        }

        SetHealth(next);
    }

    /// <summary>Records a failed operation.</summary>
    /// <param name="authenticationRequired">True when the failure was a credentials problem.</param>
    public void RecordFailure(bool authenticationRequired = false)
    {
        ShareHealth? next = null;
        lock (_lock)
        {
            if (authenticationRequired)
            {
                next = ShareHealth.AuthenticationRequired;
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= FailuresBeforeUnreachable)
                {
                    next = ShareHealth.Unreachable;
                    _nextProbeTimestamp = _time.GetTimestamp() + ToTimestampTicks(_backoff.GetDelay(_probeAttempts));
                    _probeAttempts++;
                }
            }
        }

        if (next is { } health)
        {
            SetHealth(health);
        }
    }

    /// <summary>
    /// True when a call should be attempted now. While Unreachable, only one probe is allowed per back-off interval;
    /// everything else should fail fast with a clear message instead of hanging (spec §3.3 "Fast failure").
    /// </summary>
    public bool ShouldAttempt()
    {
        lock (_lock)
        {
            return Health != ShareHealth.Unreachable || _time.GetTimestamp() >= _nextProbeTimestamp;
        }
    }

    /// <summary>Time until the next probe is allowed, or zero.</summary>
    public TimeSpan TimeUntilNextProbe()
    {
        lock (_lock)
        {
            if (Health != ShareHealth.Unreachable)
            {
                return TimeSpan.Zero;
            }

            var remaining = _nextProbeTimestamp - _time.GetTimestamp();
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / (double)_time.TimestampFrequency);
        }
    }

    private long ToTimestampTicks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private void SetHealth(ShareHealth health)
    {
        if (Health == health)
        {
            return;
        }

        Health = health;
        HealthChanged?.Invoke(this, health);
    }
}
