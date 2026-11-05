namespace Leviathan.Core.Paths;

/// <summary>
/// Asks several watchers to watch the same folder; each one takes only the folders it is meant for (local drives by change
/// notification, network shares by polling) and gives back a subscription that does nothing for the others (spec §3.1, §3.3).
/// </summary>
public sealed class CompositeFolderWatcher : IFolderWatcher
{
    private readonly IReadOnlyList<IFolderWatcher> _watchers;

    /// <summary>Creates the composite over <paramref name="watchers"/>.</summary>
    public CompositeFolderWatcher(params IFolderWatcher[] watchers)
    {
        ArgumentNullException.ThrowIfNull(watchers);
        _watchers = [.. watchers];
    }

    /// <inheritdoc />
    public IDisposable Watch(LocationPath folder, Action changed)
    {
        var subscriptions = _watchers.Select(w => w.Watch(folder, changed)).ToList();
        return new Subscription(subscriptions);
    }

    private sealed class Subscription(List<IDisposable> inner) : IDisposable
    {
        public void Dispose()
        {
            foreach (var subscription in inner)
            {
                subscription.Dispose();
            }
        }
    }
}
