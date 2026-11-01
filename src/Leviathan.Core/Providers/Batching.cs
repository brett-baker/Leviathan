using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Providers;

/// <summary>
/// Turns a blocking enumeration into a stream of batches. The blocking work runs on a thread-pool thread, never on the
/// caller's thread, and a partial batch is flushed after <see cref="ListingOptions.MaxBatchDelay"/> so a slow network
/// listing still shows rows early (spec §3.2 rule 4).
/// </summary>
public static class Batching
{
    /// <summary>Streams the entries produced by <paramref name="producer"/> as batches.</summary>
    /// <param name="producer">Blocking enumeration. It receives the token and must stop when it is cancelled.</param>
    /// <param name="location">Location being listed, used to classify failures.</param>
    /// <param name="options">Batch size, delay and filters.</param>
    /// <param name="timeProvider">Clock for batch timing.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    public static async IAsyncEnumerable<ListingBatch> StreamAsync(
        Func<CancellationToken, IEnumerable<FileEntry>> producer,
        LocationPath location,
        ListingOptions options,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(options.BatchSize * 4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        var producerTask = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var entry in producer(token))
                    {
                        if (options.Includes(entry))
                        {
                            await channel.Writer.WriteAsync(entry, token).ConfigureAwait(false);
                        }
                    }

                    channel.Writer.TryComplete();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // A cancelled listing must never look like a complete (possibly empty) folder.
                    channel.Writer.TryComplete(new OperationCanceledException(token));
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(LocationErrors.Wrap(ex, location));
                }
            },
            CancellationToken.None);

        try
        {
            var batch = new List<FileEntry>(options.BatchSize);
            var batchStarted = timeProvider.GetTimestamp();
            var reader = channel.Reader;

            while (true)
            {
                bool more;
                if (batch.Count == 0)
                {
                    more = await reader.WaitToReadAsync(token).ConfigureAwait(false);
                    batchStarted = timeProvider.GetTimestamp();
                }
                else
                {
                    var remaining = options.MaxBatchDelay - timeProvider.GetElapsedTime(batchStarted);
                    if (remaining <= TimeSpan.Zero)
                    {
                        yield return new ListingBatch(batch, false);
                        batch = new List<FileEntry>(options.BatchSize);
                        continue;
                    }

                    var (timedOut, hasMore) = await WaitWithTimeoutAsync(reader, remaining, token).ConfigureAwait(false);
                    if (timedOut)
                    {
                        yield return new ListingBatch(batch, false);
                        batch = new List<FileEntry>(options.BatchSize);
                        continue;
                    }

                    more = hasMore;
                }

                if (!more)
                {
                    break;
                }

                while (batch.Count < options.BatchSize && reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count >= options.BatchSize)
                {
                    yield return new ListingBatch(batch, false);
                    batch = new List<FileEntry>(options.BatchSize);
                }
            }

            // Surfaces a producer failure (already wrapped as LocationException) or cancellation.
            await reader.Completion.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            yield return new ListingBatch(batch, true);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            if (producerTask.IsCompleted)
            {
                await producerTask.ConfigureAwait(false);
            }
            else
            {
                // The producer may be stuck in a blocking call on a dead share. Don't wait for it: the caller has moved on,
                // and the thread returns to the pool when the OS call eventually fails. Observe its outcome so nothing leaks.
                _ = producerTask.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static async Task<(bool TimedOut, bool HasMore)> WaitWithTimeoutAsync(
        ChannelReader<FileEntry> reader, TimeSpan timeout, CancellationToken token)
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
        delay.CancelAfter(timeout);
        try
        {
            return (false, await reader.WaitToReadAsync(delay.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (true, true);
        }
    }
}
