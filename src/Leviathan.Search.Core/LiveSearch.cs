using System.Threading.Channels;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Search;

/// <summary>Limits and switches for a live search (spec §13 "Live search", §3.2).</summary>
public sealed record LiveSearchOptions
{
    /// <summary>Folders listed at the same time across all roots (default 8).</summary>
    public int Parallelism { get; init; } = 8;

    /// <summary>Folders listed at the same time on one share or server (default 2), so a search cannot swamp a NAS.</summary>
    public int ParallelismPerShare { get; init; } = 2;

    /// <summary>The most matches handed over in one batch (default 256).</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>Whether hidden items are searched.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Whether system items are searched.</summary>
    public bool IncludeSystem { get; init; }
}

/// <summary>Where a search has got to.</summary>
/// <param name="FoldersSearched">Folders whose listing has finished.</param>
/// <param name="ItemsExamined">Items looked at, matching or not.</param>
/// <param name="Matches">Items that matched so far.</param>
/// <param name="ErrorCount">Folders that could not be read.</param>
public sealed record SearchProgress(int FoldersSearched, long ItemsExamined, int Matches, int ErrorCount);

/// <summary>A folder the search could not read, and why. The search carries on without it.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Message">What went wrong, in words for the user.</param>
public sealed record SearchFolderError(LocationPath Folder, string Message);

/// <summary>One step of a streaming search: new matches, where the search is, and on the last one the folders that failed.</summary>
/// <param name="Matches">Items that matched since the previous batch.</param>
/// <param name="Progress">The counts so far.</param>
/// <param name="IsFinal">True on the last batch; a search that was cancelled never sends one.</param>
/// <param name="Errors">The folders that could not be read; filled on the final batch only.</param>
/// <param name="Sources">Which sources answered which folders, when the batch comes from a <see cref="SearchCoordinator"/>; null from a single source.</param>
public sealed record SearchBatch(IReadOnlyList<FileEntry> Matches, SearchProgress Progress, bool IsFinal, IReadOnlyList<SearchFolderError> Errors, IReadOnlyList<SearchSourceChoice>? Sources = null);

/// <summary>
/// Searches a folder tree by listing it (spec §13 "Live search"): for anywhere no index covers. Folders are listed in parallel with a bound for
/// the whole search and a smaller one per share, results stream in batches as they are found, and cancelling stops the listing at once. Links are
/// not followed. A folder that cannot be read is reported and skipped; a cancelled search ends with <see cref="OperationCanceledException"/>,
/// never with a final batch, so it cannot be mistaken for "nothing found".
/// </summary>
public sealed class LiveSearch(LocationProviderRegistry providers, LiveSearchOptions? options = null)
{
    private const int MaxReportedErrors = 200;

    private readonly LiveSearchOptions _options = options ?? new LiveSearchOptions();

    /// <summary>Searches under <paramref name="roots"/> and streams the matches.</summary>
    public async IAsyncEnumerable<SearchBatch> RunAsync(
        IReadOnlyList<LocationPath> roots,
        QueryMatcher matcher,
        ISearchFacts facts,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(facts);

        var run = new Run(this, matcher, facts, roots, cancellationToken);
        try
        {
            run.Start();
            while (await run.Results.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var matches = new List<FileEntry>();
                while (matches.Count < _options.BatchSize && run.Results.Reader.TryRead(out var found))
                {
                    matches.Add(found);
                }

                if (matches.Count > 0)
                {
                    yield return new SearchBatch(matches, run.Snapshot(matches.Count), false, []);
                }
            }

            // The channel closes with the failure of the search, if there was one (cancellation included).
            await run.Pump.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SearchBatch([], run.Snapshot(0), true, run.Errors);
        }
        finally
        {
            await run.StopAsync().ConfigureAwait(false);
        }
    }

    private sealed class Run
    {
        private readonly LiveSearch _owner;
        private readonly QueryMatcher _matcher;
        private readonly ISearchFacts _facts;
        private readonly IReadOnlyList<LocationPath> _roots;
        private readonly CancellationTokenSource _cts;
        private readonly Channel<LocationPath> _folders = Channel.CreateUnbounded<LocationPath>();
        private readonly Dictionary<string, SemaphoreSlim> _shareGates = [];
        private readonly List<SearchFolderError> _errors = [];
        private readonly Lock _lock = new();
        private int _pending;
        private int _foldersSearched;
        private long _examined;
        private int _matches;
        private int _errorCount;

        public Run(LiveSearch owner, QueryMatcher matcher, ISearchFacts facts, IReadOnlyList<LocationPath> roots, CancellationToken cancellationToken)
        {
            _owner = owner;
            _matcher = matcher;
            _facts = facts;
            _roots = roots;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Pump = Task.CompletedTask;
        }

        public Channel<FileEntry> Results { get; } = Channel.CreateUnbounded<FileEntry>();

        public Task Pump { get; private set; }

        public IReadOnlyList<SearchFolderError> Errors
        {
            get
            {
                lock (_lock)
                {
                    return [.. _errors];
                }
            }
        }

        public SearchProgress Snapshot(int _) =>
            new(Volatile.Read(ref _foldersSearched), Interlocked.Read(ref _examined), Volatile.Read(ref _matches), Volatile.Read(ref _errorCount));

        public void Start()
        {
            if (_roots.Count == 0)
            {
                Results.Writer.TryComplete();
                return;
            }

            _pending = _roots.Count;
            foreach (var root in _roots)
            {
                _folders.Writer.TryWrite(root);
            }

            var workers = Math.Max(1, _owner._options.Parallelism);
            Pump = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(WorkAsync, CancellationToken.None))).ConfigureAwait(false);
                    Results.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    Results.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);
        }

        public async Task StopAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping a search is not an error.
            }

            _cts.Dispose();
            foreach (var gate in _shareGates.Values)
            {
                gate.Dispose();
            }
        }

        private async Task WorkAsync()
        {
            var token = _cts.Token;
            try
            {
                while (await _folders.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_folders.Reader.TryRead(out var folder))
                    {
                        try
                        {
                            await SearchFolderAsync(folder, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            // Children were counted before this folder is, so zero means the whole tree is done.
                            if (Interlocked.Decrement(ref _pending) == 0)
                            {
                                _folders.Writer.TryComplete();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _folders.Writer.TryComplete();
                throw;
            }
        }

        private SemaphoreSlim? GateFor(LocationPath folder)
        {
            if (!folder.IsNetwork)
            {
                return null;
            }

            var key = (folder.Server ?? folder.Root) + "/" + folder.Share;
            lock (_lock)
            {
                if (!_shareGates.TryGetValue(key, out var gate))
                {
                    var limit = Math.Max(1, _owner._options.ParallelismPerShare);
                    gate = new SemaphoreSlim(limit, limit);
                    _shareGates[key] = gate;
                }

                return gate;
            }
        }

        private async Task SearchFolderAsync(LocationPath folder, CancellationToken token)
        {
            var gate = GateFor(folder);
            if (gate is not null)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
            }

            try
            {
                var listing = new ListingOptions { IncludeHidden = _owner._options.IncludeHidden, IncludeSystem = _owner._options.IncludeSystem };
                await foreach (var batch in _owner.ResolveProvider(folder).ListAsync(folder, listing, token).ConfigureAwait(false))
                {
                    foreach (var entry in batch.Items)
                    {
                        token.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref _examined);
                        if (entry.Kind == FileEntryKind.Folder)
                        {
                            Interlocked.Increment(ref _pending);
                            _folders.Writer.TryWrite(entry.Location);
                        }

                        if (await _matcher.MatchesAsync(entry, _facts, token).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref _matches);
                            await Results.Writer.WriteAsync(entry, token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is LocationException or IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _errorCount);
                lock (_lock)
                {
                    if (_errors.Count < MaxReportedErrors)
                    {
                        _errors.Add(new SearchFolderError(folder, ex.Message));
                    }
                }
            }
            finally
            {
                gate?.Release();
                Interlocked.Increment(ref _foldersSearched);
            }
        }
    }

    private ILocationProvider ResolveProvider(LocationPath folder) => providers.Resolve(folder);
}
