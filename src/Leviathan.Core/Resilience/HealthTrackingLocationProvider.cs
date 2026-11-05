using System.Runtime.CompilerServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Core.Resilience;

/// <summary>
/// Wraps a provider so every network listing feeds the server's health from real I/O and an unreachable server fails fast
/// (spec §3.3): a call on a server that is Unreachable and not yet due for a probe throws a transient <see cref="LocationException"/>
/// at once instead of waiting for a timeout, so a dead share costs a tab a banner and nothing else. Latency to the first batch
/// decides Online vs Slow; network drops and timeouts count as failures; "not found" and "access denied" prove the server answered.
/// Locations without a network server pass straight through.
/// </summary>
public sealed class HealthTrackingLocationProvider : ILocationProvider
{
    private readonly ILocationProvider _inner;
    private readonly ShareHealthRegistry _registry;
    private readonly TimeProvider _time;

    /// <summary>Creates the wrapper.</summary>
    public HealthTrackingLocationProvider(ILocationProvider inner, ShareHealthRegistry registry, TimeProvider? time = null, WorkOfflineRegistry? offline = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(registry);
        _inner = inner;
        _registry = registry;
        _time = time ?? TimeProvider.System;
        _offline = offline;
    }

    private readonly WorkOfflineRegistry? _offline;

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => _inner.CanHandle(location);

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var server = ShareHealthRegistry.ServerOf(location);
        if (server is null)
        {
            await foreach (var batch in _inner.ListAsync(location, options, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        // Working offline by choice: the network is not touched and the server's health is left alone (nothing was tried).
        if (_offline?.IsOffline(location) == true)
        {
            throw new LocationException(LocationError.Unreachable, location, $"Working offline with {server}.") { WorkingOffline = true };
        }

        var tracker = _registry.For(server);
        if (!tracker.ShouldAttempt())
        {
            var wait = tracker.TimeUntilNextProbe();
            throw new LocationException(LocationError.Unreachable, location, $"{server} is not responding. Trying again in {Math.Ceiling(wait.TotalSeconds):0} s.");
        }

        var started = _time.GetTimestamp();
        var recorded = false;
        var enumerator = _inner.ListAsync(location, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var wrapped = LocationErrors.Wrap(ex, location);
                    Record(tracker, wrapped, started);
                    if (ReferenceEquals(wrapped, ex))
                    {
                        throw;
                    }

                    throw wrapped;
                }

                if (!more)
                {
                    break;
                }

                if (!recorded)
                {
                    recorded = true;
                    tracker.RecordSuccess(_time.GetElapsedTime(started));
                }

                yield return enumerator.Current;
            }

            if (!recorded)
            {
                tracker.RecordSuccess(_time.GetElapsedTime(started));
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Record(ShareHealthTracker tracker, LocationException error, long started)
    {
        switch (error.Error)
        {
            case LocationError.AuthenticationRequired:
                tracker.RecordFailure(authenticationRequired: true);
                break;
            case LocationError.Unreachable or LocationError.Timeout:
                tracker.RecordFailure();
                break;
            default:
                // The server answered, with a no: it is alive.
                tracker.RecordSuccess(_time.GetElapsedTime(started));
                break;
        }
    }
}
