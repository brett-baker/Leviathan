using System.Runtime.CompilerServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Testing;

/// <summary>A fault to inject into a listing.</summary>
public abstract record Fault
{
    /// <summary>Throw ERROR_NETNAME_DELETED (the share dropped) after <paramref name="AfterBatches"/> batches.</summary>
    public sealed record NetworkDrop(int AfterBatches = 0) : Fault;

    /// <summary>Throw access denied.</summary>
    public sealed record AccessDenied : Fault;

    /// <summary>Delay every batch by <paramref name="Delay"/>.</summary>
    public sealed record Latency(TimeSpan Delay) : Fault;

    /// <summary>Never produce a batch: block until cancelled (a hung share).</summary>
    public sealed record Hang : Fault;
}

/// <summary>
/// Wraps a provider and injects faults for chosen locations (spec §19.2): network drops mid-listing, access denied,
/// latency, and hangs that only cancellation ends. The same decorator drives unit tests and chaos runs.
/// </summary>
public sealed class FaultInjectingLocationProvider(ILocationProvider inner) : ILocationProvider
{
    private readonly Dictionary<LocationPath, Fault> _faults = [];

    /// <inheritdoc />
    public string Name => inner.Name + "+faults";

    /// <summary>Injects <paramref name="fault"/> whenever <paramref name="path"/> is listed.</summary>
    public FaultInjectingLocationProvider Inject(string path, Fault fault)
    {
        _faults[LocationPath.Parse(path)] = fault;
        return this;
    }

    /// <summary>Removes the fault for <paramref name="path"/> (the share "comes back").</summary>
    public void Clear(string path) => _faults.Remove(LocationPath.Parse(path));

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => inner.CanHandle(location);

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _faults.TryGetValue(location, out var fault);
        switch (fault)
        {
            case Fault.AccessDenied:
                throw new LocationException(LocationError.AccessDenied, location, $"Access to {location} was denied.");
            case Fault.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
        }

        var produced = 0;
        await foreach (var batch in inner.ListAsync(location, options, cancellationToken))
        {
            if (fault is Fault.NetworkDrop drop && produced >= drop.AfterBatches)
            {
                throw NetworkDropped(location);
            }

            if (fault is Fault.Latency latency)
            {
                await Task.Delay(latency.Delay, cancellationToken);
            }

            produced++;
            yield return batch;
        }

        if (fault is Fault.NetworkDrop { AfterBatches: var after } && produced <= after)
        {
            throw NetworkDropped(location);
        }
    }

    private static LocationException NetworkDropped(LocationPath location) =>
        new(LocationError.Unreachable, location, $"{location.Server ?? location.Root} is not responding.", new IOException("The specified network name is no longer available.", unchecked((int)0x80070040)));
}
