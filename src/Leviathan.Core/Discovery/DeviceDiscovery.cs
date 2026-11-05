namespace Leviathan.Core.Discovery;

/// <summary>A device found on the local network that offers file sharing (spec §7.1 "Nearby").</summary>
/// <param name="Host">What to connect to: an IPv4 address when known (it works everywhere), else the announced host name.</param>
/// <param name="Name">The device's own name for itself (a NAS's friendly name), or null.</param>
public sealed record DiscoveredDevice(string Host, string? Name = null);

/// <summary>
/// One way of finding devices on the network (spec §7.1: WS-Discovery and mDNS <c>_smb._tcp</c>). A discovery asks, listens for a few
/// seconds, reports each device as it answers, and ends; it never blocks the caller's thread and stops when cancelled.
/// </summary>
public interface IDeviceDiscovery
{
    /// <summary>Short name for logs, such as "mdns".</summary>
    string Name { get; }

    /// <summary>
    /// Looks for devices, calling <paramref name="found"/> for each answer (possibly several times for one device, from any thread) until
    /// the listening window ends or <paramref name="cancellationToken"/> fires. Network trouble ends the search quietly; it is not an error
    /// to find nothing.
    /// </summary>
    Task DiscoverAsync(Action<DiscoveredDevice> found, CancellationToken cancellationToken);
}
