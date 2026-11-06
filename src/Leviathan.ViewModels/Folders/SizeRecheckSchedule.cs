using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>
/// Counts the folders the user opted in again once a day (spec §12.7): a share's folder sizes can only show their age, because a change
/// deep inside is not noticed, so the ones that matter are recounted on a schedule at low priority. The list is read each time, so a
/// change in Settings applies at the next run. A path that is not a drive letter path (a raw server path cannot be counted yet) or is
/// written twice is skipped. If the worker is away a run is skipped and tomorrow's catches up.
/// </summary>
public sealed class SizeRecheckSchedule(TimeProvider? time = null)
{
    /// <summary>How long after start-up the first run waits, so it never competes with opening the window or with the bin's purge.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    /// <summary>How long between runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The folders to count from what the user typed: one per line, drive letter paths only, each once.</summary>
    public static IReadOnlyList<LocationPath> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<LocationPath>();
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (line.Length >= 2 && line[1] == ':' && LocationPath.TryParse(line, out var path) && !result.Contains(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="paths">The folders to count, read at each run.</param>
    /// <param name="recount">Asks for one folder to be counted again.</param>
    /// <param name="cancellationToken">Stops the schedule (the window is closing).</param>
    public async Task RunAsync(Func<IReadOnlyList<LocationPath>> paths, Action<LocationPath> recount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(recount);
        try
        {
            await Task.Delay(FirstDelay, _time, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                foreach (var path in paths())
                {
                    try
                    {
                        recount(path);
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
