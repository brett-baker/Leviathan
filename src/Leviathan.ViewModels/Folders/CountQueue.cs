using Leviathan.Contracts;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>What <see cref="CountQueue.Add"/> decided for a folder that wants counting.</summary>
public enum CountAdd
{
    /// <summary>Submit a counting job for it.</summary>
    Submit,

    /// <summary>A job for exactly this folder is already waiting or running.</summary>
    AlreadyQueued,

    /// <summary>A job for a folder above it is waiting or running and will store this one on the way (spec §12.7: bottom-up).</summary>
    Covered,
}

/// <summary>A change of priority for a job that is already in the worker's queue.</summary>
/// <param name="JobId">The job.</param>
/// <param name="Priority">The priority it should have now.</param>
public readonly record struct PriorityChange(Guid JobId, JobPriority Priority);

/// <summary>
/// Which folders are being counted and in what order (spec §12.7): what you are looking at now goes first, then what is selected, then
/// the rest. It never queues a folder twice, and it does not queue a folder whose ancestor is already being counted, because that walk
/// stores every subfolder on its way (the duplicate job seen in Phase 1). Work that scrolls away is not thrown away: it drops to low
/// priority and keeps its place behind what is on screen. Pure bookkeeping, thread-safe; the app turns its answers into worker calls.
/// </summary>
public sealed class CountQueue
{
    private readonly Lock _lock = new();
    private readonly Dictionary<LocationPath, Guid?> _queued = [];
    private readonly Dictionary<LocationPath, LocationPath> _covered = [];
    private HashSet<LocationPath> _urgent = [];

    /// <summary>The priority a job for <paramref name="folder"/> should be submitted with.</summary>
    public JobPriority PriorityFor(LocationPath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        lock (_lock)
        {
            return _urgent.Contains(folder) ? JobPriority.High : JobPriority.Low;
        }
    }

    /// <summary>Decides what to do with a folder that wants counting; on <see cref="CountAdd.Submit"/> the folder is now recorded as queued.</summary>
    public CountAdd Add(LocationPath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        lock (_lock)
        {
            if (_queued.ContainsKey(folder))
            {
                return CountAdd.AlreadyQueued;
            }

            if (_covered.ContainsKey(folder))
            {
                return CountAdd.Covered;
            }

            foreach (var queued in _queued.Keys)
            {
                if (queued != folder && queued.Contains(folder))
                {
                    _covered[folder] = queued;
                    return CountAdd.Covered;
                }
            }

            _queued[folder] = null;
            return CountAdd.Submit;
        }
    }

    /// <summary>The worker accepted the job for <paramref name="folder"/>.</summary>
    public void Started(LocationPath folder, Guid jobId)
    {
        lock (_lock)
        {
            if (_queued.ContainsKey(folder))
            {
                _queued[folder] = jobId;
            }
        }
    }

    /// <summary>The job could not be submitted: forget the folder (and what it was covering) so it can be asked for again.</summary>
    public IReadOnlyList<LocationPath> Failed(LocationPath folder)
    {
        lock (_lock)
        {
            return Release(folder);
        }
    }

    /// <summary>
    /// The job <paramref name="jobId"/> ended. Returns its folder and the folders it was covering, which the caller reads from the
    /// store (or asks for again if the walk did not store them); null when this is not one of ours.
    /// </summary>
    public (LocationPath Folder, IReadOnlyList<LocationPath> Covered)? Finished(Guid jobId)
    {
        lock (_lock)
        {
            var folder = _queued.FirstOrDefault(p => p.Value == jobId).Key;
            return folder is null ? null : (folder, Release(folder));
        }
    }

    /// <summary>
    /// Says which folders are urgent (on screen, or selected). Returns the jobs already in the queue whose priority must change; jobs
    /// not yet accepted pick the new priority up from <see cref="PriorityFor"/> when they are submitted.
    /// </summary>
    public IReadOnlyList<PriorityChange> SetUrgent(IEnumerable<LocationPath> urgent)
    {
        ArgumentNullException.ThrowIfNull(urgent);
        lock (_lock)
        {
            var next = new HashSet<LocationPath>(urgent);
            var changes = new List<PriorityChange>();
            foreach (var (folder, job) in _queued)
            {
                if (job is { } id && _urgent.Contains(folder) != next.Contains(folder))
                {
                    changes.Add(new PriorityChange(id, next.Contains(folder) ? JobPriority.High : JobPriority.Low));
                }
            }

            // A folder covered by an ancestor's walk is urgent too: that walk should run now, not after the rest.
            foreach (var (covered, ancestor) in _covered)
            {
                if (next.Contains(covered) && !next.Contains(ancestor) && _queued.TryGetValue(ancestor, out var job) && job is { } id
                    && !changes.Any(c => c.JobId == id))
                {
                    changes.Add(new PriorityChange(id, JobPriority.High));
                }
            }

            _urgent = next;
            return changes;
        }
    }

    /// <summary>The folder the job <paramref name="jobId"/> is counting, or null when it is not one of ours.</summary>
    public LocationPath? FolderOf(Guid jobId)
    {
        lock (_lock)
        {
            var folder = _queued.FirstOrDefault(p => p.Value == jobId).Key;
            return folder;
        }
    }

    /// <summary>True when a job for <paramref name="folder"/> is waiting or running.</summary>
    public bool IsQueued(LocationPath folder)
    {
        lock (_lock)
        {
            return _queued.ContainsKey(folder);
        }
    }

    private List<LocationPath> Release(LocationPath folder)
    {
        _queued.Remove(folder);
        var released = _covered.Where(p => p.Value == folder).Select(p => p.Key).ToList();
        foreach (var covered in released)
        {
            _covered.Remove(covered);
        }

        return released;
    }
}
