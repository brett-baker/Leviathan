using Leviathan.Core.Resilience;

namespace Leviathan.Engine.Supervision;

/// <summary>What the supervisor should do after a worker exits.</summary>
/// <param name="Restart">True to restart.</param>
/// <param name="Delay">How long to wait before restarting.</param>
/// <param name="Reason">Human-readable explanation, shown when the feature is disabled.</param>
public sealed record RestartDecision(bool Restart, TimeSpan Delay, string Reason);

/// <summary>
/// Restart rules for worker processes (spec §3.4): restart after a crash with back-off; after three crashes within five
/// minutes, stop restarting and disable the feature so the user is told instead of watching a crash loop.
/// </summary>
public sealed class RestartPolicy
{
    private readonly Queue<DateTimeOffset> _crashes = new();
    private readonly TimeProvider _time;
    private readonly BackoffPolicy _backoff;

    /// <summary>Creates the policy.</summary>
    public RestartPolicy(TimeProvider? time = null, BackoffPolicy? backoff = null)
    {
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? new BackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), 0.2);
    }

    /// <summary>Crashes allowed within <see cref="Window"/> before giving up.</summary>
    public int MaxCrashes { get; init; } = 3;

    /// <summary>The sliding window for counting crashes.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>True once the policy has given up.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>Decides what to do after the worker exited with <paramref name="exitCode"/>.</summary>
    /// <param name="exitCode">Process exit code.</param>
    /// <param name="expected">True when the exit was requested (idle shutdown or app closing).</param>
    public RestartDecision OnExit(int exitCode, bool expected)
    {
        if (expected || exitCode == 0)
        {
            return new RestartDecision(false, TimeSpan.Zero, "Exited normally.");
        }

        var now = _time.GetUtcNow();
        _crashes.Enqueue(now);
        while (_crashes.Count > 0 && now - _crashes.Peek() > Window)
        {
            _crashes.Dequeue();
        }

        if (_crashes.Count >= MaxCrashes)
        {
            GaveUp = true;
            return new RestartDecision(false, TimeSpan.Zero, $"Crashed {_crashes.Count} times in {Window.TotalMinutes:0} minutes; disabled until restart.");
        }

        return new RestartDecision(true, _backoff.GetDelay(_crashes.Count - 1), $"Crashed with exit code {exitCode}; restarting.");
    }

    /// <summary>Clears the crash history (e.g. after the user re-enables the feature).</summary>
    public void Reset()
    {
        _crashes.Clear();
        GaveUp = false;
    }
}
