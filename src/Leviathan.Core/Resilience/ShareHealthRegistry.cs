using System.Collections.Concurrent;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Resilience;

/// <summary>A server's health at a moment, for the tree, the tab banner and the status bar.</summary>
/// <param name="Server">The server's identity key (see <see cref="ServerIdentityMap"/>).</param>
/// <param name="Health">Current health.</param>
/// <param name="Latency">Smoothed latency, or null before the first success.</param>
/// <param name="RetryIn">While unreachable, the time until the next probe is allowed.</param>
public sealed record ShareStatus(string Server, ShareHealth Health, TimeSpan? Latency, TimeSpan RetryIn);

/// <summary>
/// One <see cref="ShareHealthTracker"/> per server, shared by everything that talks to it (spec §3.3). Servers are keyed by identity,
/// so a NAS reached by IP, NetBIOS name and DDNS name has one health, not three. Thread-safe.
/// </summary>
public sealed class ShareHealthRegistry
{
    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly ConcurrentDictionary<string, ShareHealthTracker> _trackers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _identified = new(StringComparer.Ordinal);
    private readonly ServerIdentityMap _identities;
    private readonly TimeProvider _time;

    /// <summary>Creates the registry.</summary>
    public ShareHealthRegistry(ServerIdentityMap identities, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        _identities = identities;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when any server's health changes, with the server key and the new health. May come from any thread.</summary>
    public event EventHandler<(string Server, ShareHealth Health)>? HealthChanged;

    /// <summary>The server a location lives on, or null for local drives and other locations without a network server.</summary>
    public static string? ServerOf(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind == LocationKind.Unc ? location.Server : null;
    }

    /// <summary>The tracker for <paramref name="server"/>. The first call for a name also starts looking up which other names are the same server.</summary>
    public ShareHealthTracker For(string server)
    {
        var name = ServerIdentityMap.Normalize(server);
        if (_identified.TryAdd(name, 0))
        {
            _ = _identities.IdentifyAsync(name, IdentifyTimeout, CancellationToken.None);
        }

        var key = _identities.KeyFor(name);
        return _trackers.GetOrAdd(key, k =>
        {
            var tracker = new ShareHealthTracker(k, _time);
            tracker.HealthChanged += (_, health) => HealthChanged?.Invoke(this, (k, health));
            return tracker;
        });
    }

    /// <summary>The current health of the server behind <paramref name="location"/>; Unknown for locations without a network server or not yet visited.</summary>
    public ShareHealth HealthOf(LocationPath location)
    {
        var server = ServerOf(location);
        return server is not null && _trackers.TryGetValue(_identities.KeyFor(server), out var tracker) ? tracker.Health : ShareHealth.Unknown;
    }

    /// <summary>Every server seen so far, with its health.</summary>
    public IReadOnlyList<ShareStatus> Snapshot() =>
        [.. _trackers.Values.Select(t => new ShareStatus(t.Server, t.Health, t.Latency, t.TimeUntilNextProbe())).OrderBy(s => s.Server, StringComparer.Ordinal)];
}
