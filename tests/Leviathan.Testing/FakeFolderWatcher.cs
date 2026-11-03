using Leviathan.Core.Paths;

namespace Leviathan.Testing;

/// <summary>A watcher the test drives: <see cref="Raise"/> reports a change to everyone watching that folder.</summary>
public sealed class FakeFolderWatcher : IFolderWatcher
{
    private readonly Lock _gate = new();
    private readonly List<(LocationPath Folder, Action Changed)> _watches = [];

    /// <summary>How many subscriptions are active.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _watches.Count;
            }
        }
    }

    /// <summary>The folders being watched now.</summary>
    public IReadOnlyList<LocationPath> Watched
    {
        get
        {
            lock (_gate)
            {
                return [.. _watches.Select(w => w.Folder)];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Watch(LocationPath folder, Action changed)
    {
        var entry = (folder, changed);
        lock (_gate)
        {
            _watches.Add(entry);
        }

        return new Release(this, entry);
    }

    /// <summary>Reports a change in <paramref name="folder"/>.</summary>
    public void Raise(LocationPath folder)
    {
        Action[] targets;
        lock (_gate)
        {
            targets = [.. _watches.Where(w => w.Folder == folder).Select(w => w.Changed)];
        }

        foreach (var target in targets)
        {
            target();
        }
    }

    private sealed class Release(FakeFolderWatcher owner, (LocationPath Folder, Action Changed) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._watches.Remove(entry);
            }
        }
    }
}
