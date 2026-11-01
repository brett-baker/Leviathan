using Leviathan.Core.Paths;

namespace Leviathan.Core.Providers;

/// <summary>Picks the provider for a location. Providers are asked in registration order.</summary>
public sealed class LocationProviderRegistry
{
    private readonly IReadOnlyList<ILocationProvider> _providers;

    /// <summary>Creates the registry.</summary>
    public LocationProviderRegistry(IEnumerable<ILocationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [.. providers];
    }

    /// <summary>All registered providers.</summary>
    public IReadOnlyList<ILocationProvider> Providers => _providers;

    /// <summary>Returns the provider for <paramref name="location"/> or throws when none can handle it.</summary>
    public ILocationProvider Resolve(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return _providers.FirstOrDefault(p => p.CanHandle(location))
            ?? throw new LocationException(LocationError.Unknown, location, $"No provider can open {location.Kind} locations yet.");
    }
}
