using Leviathan.Contracts;

namespace Leviathan.ViewModels.Hub;

/// <summary>
/// Decides when the automatic purge of the Soft-delete bin runs (spec §12.5): a short while after start-up, then once a day, with the
/// limits read each time so a change in Settings applies at the next run. The purge itself is a background job in the worker, with a
/// report; this only asks for it. A limit of zero is off, and when both are off nothing is asked. If the worker is away the run is
/// skipped and the next day's catches up, since an old item stays in the bin a day longer at worst.
/// </summary>
public sealed class BinPurgeSchedule(TimeProvider? time = null)
{
    /// <summary>How long after start-up the first run waits, so it never competes with opening the window.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    /// <summary>How long between runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="limits">The current age and size limits.</param>
    /// <param name="submit">Asks the worker for a purge by this rule.</param>
    /// <param name="cancellationToken">Stops the schedule (the window is closing).</param>
    public async Task RunAsync(Func<PurgePolicy> limits, Func<PurgePolicy, CancellationToken, Task> submit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(submit);
        try
        {
            await Task.Delay(FirstDelay, _time, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var policy = limits();
                if (policy.MaxAgeDays > 0 || policy.MaxBytes > 0)
                {
                    try
                    {
                        await submit(policy, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                    {
                        // The worker is not there right now; tomorrow's run picks it up.
                    }
                }

                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
    }
}
