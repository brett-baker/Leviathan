using System.Threading.Channels;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Search;

/// <summary>How hard a crawl works (spec §13: "low priority").</summary>
public sealed record IndexCrawlOptions
{
    /// <summary>Folders listed at the same time (default 2, so a crawl never competes with the work the user is doing).</summary>
    public int Parallelism { get; init; } = 2;

    /// <summary>Folders listed at the same time on one share or server (default 1, as the NAS crawl is meant to be gentle).</summary>
    public int ParallelismPerShare { get; init; } = 1;
}

/// <summary>Where a crawl has got to.</summary>
/// <param name="FoldersDone">Folders listed.</param>
/// <param name="ItemsSeen">Items found in them.</param>
/// <param name="Errors">Folders that could not be listed (their old entries are kept).</param>
public sealed record CrawlProgress(int FoldersDone, long ItemsSeen, int Errors);

/// <summary>
/// Fills a <see cref="NameIndex"/> by listing folders (spec §13: the NAS index, and the local index when no Everything or indexer is available). It
/// lists with a small bound and never follows links. A folder that cannot be listed keeps what the index had for it. Cancelling stops at once and leaves a
/// consistent index: every folder is replaced as a whole, so the index is never left with half a folder. Running it again over a root re-lists the same
/// folders and applies only the differences, keeping entry ids.
/// </summary>
public sealed class IndexCrawler(LocationProviderRegistry providers, NameIndex index, IndexCrawlOptions? options = null)
{
    private readonly IndexCrawlOptions _options = options ?? new IndexCrawlOptions();

    /// <summary>Lists <paramref name="root"/> and everything below it into the index.</summary>
    public async Task<CrawlProgress> CrawlAsync(LocationPath root, IProgress<CrawlProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        var rootId = index.AddRoot(root);
        var queue = Channel.CreateUnbounded<int>();
        var pending = 1;
        var done = 0;
        long seen = 0;
        var errors = 0;
        var gates = new Dictionary<string, SemaphoreSlim>();
        queue.Writer.TryWrite(rootId);

        SemaphoreSlim? GateFor(LocationPath folder)
        {
            if (!folder.IsNetwork)
            {
                return null;
            }

            lock (gates)
            {
                var key = (folder.Server ?? folder.Root) + "/" + folder.Share;
                if (!gates.TryGetValue(key, out var gate))
                {
                    var limit = Math.Max(1, _options.ParallelismPerShare);
                    gates[key] = gate = new SemaphoreSlim(limit, limit);
                }

                return gate;
            }
        }

        async Task CrawlFolderAsync(int id)
        {
            var folder = index.PathOf(id);
            var gate = GateFor(folder);
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var children = new List<IndexedChild>();
                var entries = new List<FileEntry>();
                var options = new ListingOptions { IncludeHidden = true, IncludeSystem = true, BatchSize = 1024 };
                await foreach (var batch in providers.Resolve(folder).ListAsync(folder, options, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var entry in batch.Items)
                    {
                        entries.Add(entry);
                        children.Add(new IndexedChild(entry.Name, entry.Kind == FileEntryKind.Folder, entry.Kind == FileEntryKind.Folder ? 0 : entry.Size, entry.Modified.UtcTicks, entry.Attributes));
                    }
                }

                var ids = index.ReplaceChildren(id, children);
                Interlocked.Add(ref seen, children.Count);
                for (var i = 0; i < ids.Length; i++)
                {
                    if (children[i].IsFolder)
                    {
                        Interlocked.Increment(ref pending);
                        queue.Writer.TryWrite(ids[i]);
                    }
                }
            }
            catch (Exception ex) when (ex is LocationException or IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref errors);
            }
            finally
            {
                gate?.Release();
                Interlocked.Increment(ref done);
                progress?.Report(new CrawlProgress(Volatile.Read(ref done), Interlocked.Read(ref seen), Volatile.Read(ref errors)));
            }
        }

        async Task WorkerAsync()
        {
            try
            {
                while (await queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (queue.Reader.TryRead(out var id))
                    {
                        try
                        {
                            await CrawlFolderAsync(id).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref pending) == 0)
                            {
                                queue.Writer.TryComplete();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                queue.Writer.TryComplete();
                throw;
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.Parallelism)).Select(_ => Task.Run(WorkerAsync, CancellationToken.None))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var gate in gates.Values)
            {
                gate.Dispose();
            }
        }

        return new CrawlProgress(done, seen, errors);
    }
}
