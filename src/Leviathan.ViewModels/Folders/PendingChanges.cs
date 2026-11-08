using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>A change to the pending set.</summary>
/// <param name="Folders">The folders whose rows are affected.</param>
/// <param name="Settled">True when a job ended (the folders should be read again); false when one began.</param>
public sealed record PendingChange(IReadOnlyCollection<LocationPath> Folders, bool Settled);

/// <summary>
/// What a running delete, move or copy is about to do to the folders on screen, so the rows can follow the request instead of the
/// finished job (spec §3.2, §12.5, the "UI updates: Immediate" setting). A delete or move hides its sources; a move or copy puts a
/// <em>pending</em> copy of each item into the destination, drawn dimmed. When the job ends the change is settled: whatever really
/// happened is read back from the disk, so a failed or cancelled job brings the sources back and takes the pending rows away.
/// Thread-safe: listings are compared off the UI thread.
/// </summary>
public sealed class PendingChanges
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Change> _changes = [];

    // Jobs that ended before their request was recorded (a fast job can finish first): recorded late, they would never settle.
    private readonly Queue<Guid> _settledOrder = new();
    private readonly HashSet<Guid> _settled = [];
    private const int SettledMemory = 256;

    /// <summary>Raised after the set changed, with the folders whose rows are affected. May come from any thread.</summary>
    public event EventHandler<PendingChange>? Changed;

    /// <summary>True when nothing is pending.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _changes.Count == 0;
            }
        }
    }

    /// <summary>
    /// Records a job. <paramref name="hide"/> are items that vanish from the folder they are in (a delete, or the source of a move);
    /// <paramref name="ghosts"/> are the pending copies that appear in <paramref name="destination"/> (a move or copy).
    /// </summary>
    /// <param name="job">The worker's job id; the same id settles it.</param>
    /// <param name="hide">Items to hide where they are.</param>
    /// <param name="destination">The folder the items are going into, or null for a delete.</param>
    /// <param name="ghosts">What to show in the destination, already marked pending.</param>
    public void Begin(Guid job, IReadOnlyCollection<LocationPath> hide, LocationPath? destination, IReadOnlyList<FileEntry> ghosts)
    {
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(ghosts);
        var change = new Change([.. hide], destination, [.. ghosts.Select(g => g with { IsPending = true })]);
        lock (_gate)
        {
            if (_settled.Contains(job))
            {
                return;
            }

            _changes[job] = change;
        }

        Changed?.Invoke(this, new PendingChange(Folders(change), Settled: false));
    }

    /// <summary>The job ended (done, failed or cancelled): forgets what it was going to do. The caller reads the folders again.</summary>
    public void Settle(Guid job)
    {
        Change? change;
        lock (_gate)
        {
            if (_settled.Add(job))
            {
                _settledOrder.Enqueue(job);
                if (_settledOrder.Count > SettledMemory)
                {
                    _settled.Remove(_settledOrder.Dequeue());
                }
            }

            if (!_changes.Remove(job, out change))
            {
                return;
            }
        }

        Changed?.Invoke(this, new PendingChange(Folders(change), Settled: true));
    }

    /// <summary>True when the job is recorded or already ended (so a second window need not record it again).</summary>
    public bool Knows(Guid job)
    {
        lock (_gate)
        {
            return _changes.ContainsKey(job) || _settled.Contains(job);
        }
    }

    /// <summary>True when anything is hidden in, or pending into, <paramref name="folder"/>.</summary>
    public bool Affects(LocationPath folder)
    {
        lock (_gate)
        {
            return _changes.Values.Any(c => c.Destination == folder || c.Hidden.Any(h => h.Parent == folder));
        }
    }

    /// <summary>The items hidden in <paramref name="folder"/>.</summary>
    public IReadOnlySet<LocationPath> HiddenIn(LocationPath folder)
    {
        lock (_gate)
        {
            return _changes.Values.SelectMany(c => c.Hidden).Where(h => h.Parent == folder).ToHashSet();
        }
    }

    /// <summary>The pending items going into <paramref name="folder"/>.</summary>
    public IReadOnlyList<FileEntry> GhostsIn(LocationPath folder)
    {
        lock (_gate)
        {
            return [.. _changes.Values.Where(c => c.Destination == folder).SelectMany(c => c.Ghosts)];
        }
    }

    /// <summary>
    /// What <paramref name="folder"/> should show given the real listing <paramref name="listed"/>: hidden items left out, and a pending
    /// item added unless the real one has already arrived (then the real one is shown and the pending one is dropped).
    /// </summary>
    public IReadOnlyList<FileEntry> Apply(LocationPath folder, IReadOnlyList<FileEntry> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        var hidden = HiddenIn(folder);
        var ghosts = GhostsIn(folder);
        if (hidden.Count == 0 && ghosts.Count == 0)
        {
            return listed;
        }

        var result = new List<FileEntry>(listed.Count + ghosts.Count);
        var present = new HashSet<LocationPath>();
        foreach (var entry in listed)
        {
            if (!hidden.Contains(entry.Location))
            {
                result.Add(entry);
                present.Add(entry.Location);
            }
        }

        foreach (var ghost in ghosts)
        {
            if (present.Add(ghost.Location))
            {
                result.Add(ghost);
            }
        }

        return result;
    }

    private static HashSet<LocationPath> Folders(Change change)
    {
        var folders = new HashSet<LocationPath>();
        foreach (var hidden in change.Hidden)
        {
            if (hidden.Parent is { } parent)
            {
                folders.Add(parent);
            }
        }

        if (change.Destination is { } destination)
        {
            folders.Add(destination);
        }

        return folders;
    }

    private sealed record Change(IReadOnlyList<LocationPath> Hidden, LocationPath? Destination, IReadOnlyList<FileEntry> Ghosts);
}
