using Leviathan.Core.Resilience;

namespace Leviathan.Core.Paths;

/// <summary>
/// Identifies the physical device a location lives on, for per-device concurrency limits (spec §12.3).
/// A UNC path is keyed by its server (one NAS = one device), a drive path by its drive letter.
/// </summary>
public static class DeviceKeys
{
    /// <summary>The device key for a location, e.g. <c>\\nas</c>, <c>C:</c> or <c>sftp://host</c>.</summary>
    public static string For(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind switch
        {
            LocationKind.Drive => location.Root[..2].ToUpperInvariant(),
            LocationKind.Unc => @"\\" + location.Server!.ToLowerInvariant(),
            LocationKind.Shell => location.Root.ToLowerInvariant(),
            _ => $"{location.Kind.ToString().ToLowerInvariant()}://{location.Server}",
        };
    }

    /// <summary>
    /// Like <see cref="For(LocationPath)"/>, but a UNC server is keyed by its identity, so <c>\\192.168.1.5</c>, <c>\\diskstation</c>
    /// and the DDNS name of one NAS are one device (spec §3.5 item 5).
    /// </summary>
    public static string For(LocationPath location, ServerIdentityMap identities)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(identities);
        return location.Kind == LocationKind.Unc ? @"\\" + identities.KeyFor(location.Server!) : For(location);
    }

    /// <summary><see cref="ForJob(IEnumerable{LocationPath})"/> with server identities applied.</summary>
    public static string? ForJob(IEnumerable<LocationPath> locations, ServerIdentityMap identities)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var list = locations.ToList();
        var network = list.FirstOrDefault(l => l.IsNetwork);
        return network is not null ? For(network, identities) : list.Count > 0 ? For(list[0], identities) : null;
    }

    /// <summary>The device key that limits a job touching <paramref name="locations"/>: the first network device, else the first device.</summary>
    public static string? ForJob(IEnumerable<LocationPath> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var list = locations.ToList();
        var network = list.FirstOrDefault(l => l.IsNetwork);
        return network is not null ? For(network) : list.Count > 0 ? For(list[0]) : null;
    }
}
