using Leviathan.Core.Discovery;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>
/// The devices under Network ▸ Nearby (spec §7.1): runs the discoveries (WS-Discovery, mDNS) in the background, collects what they
/// find, and says when the list changes. Discovery never delays the tree: <see cref="RefreshAsync"/> returns at once to its caller's
/// thread of control, the network work runs on the pool, and a discovery that fails only loses its own answers. A run is repeated no
/// sooner than <see cref="MinGap"/> after the last one began, because a refresh when the group is opened is free but a flood is not.
/// </summary>
public sealed class NearbyService
{
    /// <summary>The least time between two runs started by <see cref="RefreshAsync"/>.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IDeviceDiscovery> _discoveries;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private List<NearbyDevice> _devices = [];
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private Task? _running;

    /// <summary>Creates the service over <paramref name="discoveries"/>.</summary>
    public NearbyService(IEnumerable<IDeviceDiscovery> discoveries, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(discoveries);
        _discoveries = [.. discoveries];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when <see cref="Devices"/> or <see cref="IsSearching"/> changed. May come from any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>What has been found, in no particular order.</summary>
    public IReadOnlyList<NearbyDevice> Devices
    {
        get
        {
            lock (_lock)
            {
                return [.. _devices];
            }
        }
    }

    /// <summary>True while a run is listening.</summary>
    public bool IsSearching
    {
        get
        {
            lock (_lock)
            {
                return _running is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Starts a run unless one is going or one began less than <see cref="MinGap"/> ago (<paramref name="force"/> skips the gap, not the
    /// running check). The returned task ends with the run and never faults; a cancelled run keeps what it found.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken, bool force = false)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
            {
                return _running;
            }

            if (!force && _time.GetUtcNow() - _lastRun < MinGap)
            {
                return Task.CompletedTask;
            }

            _lastRun = _time.GetUtcNow();
            _running = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
            return _running;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, NearbyDevice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.WhenAll(_discoveries.Select(d => DiscoverSafelyAsync(d, found, cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            // Devices that did not answer this time are gone; a cancelled run keeps what it had.
            lock (_lock)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _devices = [.. found.Values];
                }

                _running = null;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DiscoverSafelyAsync(IDeviceDiscovery discovery, System.Collections.Concurrent.ConcurrentDictionary<string, NearbyDevice> found, CancellationToken cancellationToken)
    {
        try
        {
            await discovery.DiscoverAsync(device => Add(found, device), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Window closing.
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ObjectDisposedException or InvalidOperationException or System.Xml.XmlException or ArgumentException)
        {
            // This way of looking failed (no network, a blocked port); the other one, and the tree, carry on.
        }
    }

    private void Add(System.Collections.Concurrent.ConcurrentDictionary<string, NearbyDevice> found, DiscoveredDevice device)
    {
        if (!SavedConnectionList.IsValidHost(device.Host))
        {
            return;
        }

        var host = SavedConnectionList.Normalize(device.Host);
        var changed = false;
        found.AddOrUpdate(
            host,
            _ =>
            {
                changed = true;
                return new NearbyDevice(host, device.Name);
            },
            (_, existing) =>
            {
                // The first name wins unless it was missing: mDNS has a real name, WS-Discovery often only the address.
                if (existing.Name is null && !string.IsNullOrWhiteSpace(device.Name))
                {
                    changed = true;
                    return existing with { Name = device.Name };
                }

                return existing;
            });
        if (changed)
        {
            lock (_lock)
            {
                // Show devices as they turn up, on top of what the last run found.
                _devices = [.. _devices.Where(d => !d.Host.Equals(host, StringComparison.OrdinalIgnoreCase)), found[host]];
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
