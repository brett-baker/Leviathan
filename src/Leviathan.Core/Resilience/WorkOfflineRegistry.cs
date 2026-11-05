using System.Collections.Concurrent;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Resilience;

/// <summary>
/// The servers the user chose to "Work offline" with (spec §3.3, §8.6): while a server is in the set, listings of its folders do not
/// touch the network at all and are served from the offline cache, so a share that is known to be down (or a laptop on a train)
/// costs no timeouts and no retries. Servers are keyed by identity, like their health, so a NAS reached by two names is one entry.
/// The choice lasts for the run; the next start tries the network again. Thread-safe.
/// </summary>
public sealed class WorkOfflineRegistry
{
    private readonly ConcurrentDictionary<string, byte> _servers = new(StringComparer.Ordinal);
    private readonly ServerIdentityMap? _identities;

    /// <summary>Creates the registry; with <paramref name="identities"/>, names of one server count as one.</summary>
    public WorkOfflineRegistry(ServerIdentityMap? identities = null)
    {
        _identities = identities;
    }

    /// <summary>Raised when a server goes offline or online by choice, with its key and the new state. May come from any thread.</summary>
    public event EventHandler<(string Server, bool Offline)>? Changed;

    /// <summary>The servers that are worked with offline now.</summary>
    public IReadOnlyCollection<string> Servers => [.. _servers.Keys];

    /// <summary>True when the user chose to work offline with the server behind <paramref name="location"/>. Always false for local locations.</summary>
    public bool IsOffline(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return ShareHealthRegistry.ServerOf(location) is { } server && _servers.ContainsKey(KeyOf(server));
    }

    /// <summary>Works offline with (or goes back online with) the server behind <paramref name="location"/>. Does nothing for local locations. Returns whether anything changed.</summary>
    public bool Set(LocationPath location, bool offline)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (ShareHealthRegistry.ServerOf(location) is not { } server)
        {
            return false;
        }

        var key = KeyOf(server);
        var changed = offline ? _servers.TryAdd(key, 0) : _servers.TryRemove(key, out _);
        if (changed)
        {
            Changed?.Invoke(this, (key, offline));
        }

        return changed;
    }

    private string KeyOf(string server)
    {
        var name = ServerIdentityMap.Normalize(server);
        return _identities?.KeyFor(name) ?? name;
    }
}
