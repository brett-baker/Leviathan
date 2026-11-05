using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Core.Resilience;

/// <summary>
/// Reconnects a disconnected mapped drive the first time it is used (spec §3.3 Reconnect): when listing a drive letter that is a
/// network mapping fails with "not responding" or "sign in" before any item arrived, the drive is reconnected with the credentials
/// Windows holds and the listing is tried once more. An attempt is made at most once per drive per <see cref="MinGap"/> and never
/// takes longer than <see cref="ReconnectTimeout"/>, so a NAS that is really down still fails fast. Everything else passes through.
/// </summary>
public sealed class ReconnectingLocationProvider : ILocationProvider
{
    private readonly ILocationProvider _inner;
    private readonly IShareConnector _connector;
    private readonly Func<LocationPath, bool> _isMappedDrive;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<char, long> _nextAttempt = new();

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">The provider that lists.</param>
    /// <param name="connector">Reconnects drives.</param>
    /// <param name="isMappedDrive">Tells whether a location is on a mapped network drive letter.</param>
    /// <param name="time">Clock for the gap between attempts.</param>
    public ReconnectingLocationProvider(ILocationProvider inner, IShareConnector connector, Func<LocationPath, bool> isMappedDrive, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(isMappedDrive);
        _inner = inner;
        _connector = connector;
        _isMappedDrive = isMappedDrive;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The least time between two reconnect attempts for one drive.</summary>
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The longest a reconnect attempt is waited for before the listing fails as it would have.</summary>
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => _inner.CanHandle(location);

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var mapped = location.Kind == LocationKind.Drive && location.Root.Length >= 2 && _isMappedDrive(location);
        for (var attempt = 0; ; attempt++)
        {
            Exception? failure = null;
            var yielded = false;
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failure = ex;
                        break;
                    }

                    if (!more)
                    {
                        break;
                    }

                    yielded = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is null)
            {
                yield break;
            }

            var wrapped = LocationErrors.Wrap(failure, location);
            if (!mapped || yielded || attempt > 0 || wrapped.Error is not (LocationError.Unreachable or LocationError.AuthenticationRequired)
                || !await ReconnectAsync(location.Root[0], cancellationToken).ConfigureAwait(false))
            {
                if (ReferenceEquals(wrapped, failure))
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }

                throw wrapped;
            }
        }
    }

    // True when the drive was reconnected and the listing is worth trying again.
    private async Task<bool> ReconnectAsync(char letter, CancellationToken cancellationToken)
    {
        letter = char.ToUpperInvariant(letter);
        var now = _time.GetTimestamp();
        var due = _nextAttempt.GetOrAdd(letter, 0);
        if (now < due || !_nextAttempt.TryUpdate(letter, now + (long)(MinGap.TotalSeconds * _time.TimestampFrequency), due))
        {
            return false;
        }

        try
        {
            var result = await _connector.ReconnectDriveAsync(letter, cancellationToken).WaitAsync(ReconnectTimeout, _time, cancellationToken).ConfigureAwait(false);
            return result.Outcome == ConnectOutcome.Connected;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
