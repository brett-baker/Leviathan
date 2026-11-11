using Leviathan.Contracts;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Searches;

/// <summary>
/// Which servers and drives a running foreground job is reading or writing (spec §12: the background lane "pauses automatically while a job is copying to
/// or from the same NAS"). Fed with every job snapshot; the search index asks it before listing a folder so a crawl never competes with a copy for the
/// same share.
/// </summary>
public sealed class BusyDevices
{
    private readonly Dictionary<Guid, string> _active = [];
    private readonly Lock _lock = new();

    /// <summary>Notes a job's new state.</summary>
    public void Apply(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_lock)
        {
            if (job.Lane == JobLane.Operations && job.State == JobState.Running && job.DeviceKey is { Length: > 0 } key)
            {
                _active[job.Id] = key;
            }
            else
            {
                _active.Remove(job.Id);
            }
        }
    }

    /// <summary>True when a foreground job is running against the device that holds <paramref name="location"/>.</summary>
    public bool IsBusy(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var key = DeviceKeys.For(location);
        lock (_lock)
        {
            return _active.Values.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
