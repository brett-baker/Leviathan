namespace Leviathan.Core.Resilience;

/// <summary>
/// Exponential back-off with jitter: 1 s, 2 s, 4 s … capped (spec §3.3). Jitter spreads retries so several tabs or jobs
/// waiting on the same NAS don't hammer it in lock-step.
/// </summary>
public sealed class BackoffPolicy
{
    /// <summary>Policy used for network retries: 1 s doubling to 60 s, ±20% jitter.</summary>
    public static BackoffPolicy Network { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0.2);

    /// <summary>Creates a policy.</summary>
    /// <param name="initial">Delay before the first retry.</param>
    /// <param name="maximum">Upper bound for any delay.</param>
    /// <param name="jitter">Fraction (0–1) of random variation applied to each delay.</param>
    public BackoffPolicy(TimeSpan initial, TimeSpan maximum, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, initial);
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1.0);
        Initial = initial;
        Maximum = maximum;
        Jitter = jitter;
    }

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan Initial { get; }

    /// <summary>Upper bound for any delay.</summary>
    public TimeSpan Maximum { get; }

    /// <summary>Jitter fraction.</summary>
    public double Jitter { get; }

    /// <summary>
    /// Delay before retry number <paramref name="attempt"/> (0-based). <paramref name="random"/> is a value in [0, 1)
    /// so tests can make jitter deterministic.
    /// </summary>
    public TimeSpan GetDelay(int attempt, double random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        var exponent = Math.Min(attempt, 30);
        var baseTicks = Math.Min(Initial.Ticks * Math.Pow(2, exponent), Maximum.Ticks);
        var factor = 1 + (Jitter * ((random * 2) - 1));
        var ticks = (long)Math.Clamp(baseTicks * factor, Initial.Ticks * (1 - Jitter), Maximum.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Delay before retry <paramref name="attempt"/> using a shared random source.</summary>
    public TimeSpan GetDelay(int attempt) => GetDelay(attempt, Random.Shared.NextDouble());
}
