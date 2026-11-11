using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Search;

/// <summary>The folders the user chose to index, kept between runs (spec §13: "folders you opt in").</summary>
public interface IIndexedFolders
{
    /// <summary>Reads the chosen folders; a missing or damaged file gives none.</summary>
    IReadOnlyList<LocationPath> Load();

    /// <summary>Saves the chosen folders. Must be atomic.</summary>
    void Save(IReadOnlyList<LocationPath> folders);
}

/// <summary>When and how gently the index is kept up to date.</summary>
public sealed record IndexSchedule
{
    /// <summary>How long after a complete listing a folder is listed again (default 6 hours).</summary>
    public TimeSpan RecrawlEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How often the manager looks for folders that are due (default 1 minute).</summary>
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often a paused crawl looks to see whether it may go on (default 2 seconds).</summary>
    public TimeSpan ResumeCheckEvery { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Where one indexed folder stands, for the Background tab of the status hub (spec §16.1).</summary>
/// <param name="Root">The folder.</param>
/// <param name="LastCrawled">When it was last listed completely, or null.</param>
/// <param name="IsCrawling">True while it is being listed.</param>
/// <param name="IsWaiting">True while a running crawl is held back (paused by the user, or a job is copying to the same server).</param>
/// <param name="Progress">Counts of the crawl under way or the last one.</param>
/// <param name="NextCrawl">When it is next due, or null while it is crawling.</param>
/// <param name="Message">The last problem, or why it is waiting; null when all is well.</param>
public sealed record IndexRootStatus(LocationPath Root, DateTimeOffset? LastCrawled, bool IsCrawling, bool IsWaiting, CrawlProgress? Progress, DateTimeOffset? NextCrawl, string? Message);

/// <summary>
/// Keeps the folders the user chose indexed (spec §13, build plan C): loads the saved index at start, lists each chosen folder in the background when it is due
/// (never listed, or older than the schedule), saves the index after each listing, and stays out of the way: one folder at a time, held back while the user pauses
/// it or while a job is copying to the same share (spec §12). Folders the user opens are folded in as they are listed, which is the cheap change detection:
/// browsing a folder refreshes its entries in the index.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly NameIndex _index;
    private readonly LocationProviderRegistry _providers;
    private readonly INameIndexStore _store;
    private readonly IIndexedFolders _folders;
    private readonly TimeProvider _time;
    private readonly IndexSchedule _schedule;
    private readonly Func<LocationPath, bool> _isBusy;
    private readonly IndexCrawlOptions _crawlOptions;
    private readonly Lock _lock = new();
    private readonly List<LocationPath> _roots = [];
    private readonly Dictionary<LocationPath, (bool Crawling, CrawlProgress? Progress, string? Message)> _live = [];
    private readonly Dictionary<LocationPath, (int Count, string Reason)> _waiting = [];
    private volatile bool _paused;
    private CancellationTokenSource? _current;

    /// <summary>Creates the manager. <paramref name="isBusy"/> says whether a job is copying to the share that holds a folder.</summary>
    public IndexManager(NameIndex index, LocationProviderRegistry providers, INameIndexStore store, IIndexedFolders folders, TimeProvider? time = null, IndexSchedule? schedule = null, Func<LocationPath, bool>? isBusy = null, IndexCrawlOptions? crawlOptions = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        _time = time ?? TimeProvider.System;
        _schedule = schedule ?? new IndexSchedule();
        _isBusy = isBusy ?? (_ => false);
        _crawlOptions = crawlOptions ?? new IndexCrawlOptions();
    }

    /// <summary>Raised (on a pool thread) when the folders or their status change.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the user has paused indexing.</summary>
    public bool IsPaused => _paused;

    /// <summary>The folders chosen for indexing.</summary>
    public IReadOnlyList<LocationPath> Roots
    {
        get
        {
            lock (_lock)
            {
                return [.. _roots];
            }
        }
    }

    /// <summary>Loads the chosen folders and the saved index. Call once at start, off the UI thread.</summary>
    public void Initialize()
    {
        var chosen = _folders.Load();
        lock (_lock)
        {
            _roots.Clear();
            _roots.AddRange(chosen);
        }

        // With nothing chosen the saved copy is not even opened, so a person who never uses the index pays nothing for it at start.
        if (chosen.Count > 0 && _store.Load() is { } snapshot && _index.Count == 0 && _index.Roots.Count == 0)
        {
            try
            {
                _index.Import(snapshot);
            }
            catch (InvalidOperationException)
            {
                // A damaged copy is built again by the next crawl.
            }
        }

        // A folder the user no longer chose should not linger in the index.
        foreach (var root in _index.Roots.ToList())
        {
            if (!chosen.Contains(_index.PathOf(root)))
            {
                _index.Remove(root);
            }
        }

        RaiseChanged();
    }

    /// <summary>Starts indexing a folder (it is listed the next time the manager looks, or at once with <see cref="CrawlNowAsync"/>).</summary>
    public void Add(LocationPath root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_lock)
        {
            if (_roots.Contains(root))
            {
                return;
            }

            _roots.Add(root);
            _folders.Save([.. _roots]);
        }

        RaiseChanged();
    }

    /// <summary>Stops indexing a folder and drops its entries.</summary>
    public void Remove(LocationPath root)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_lock)
        {
            if (!_roots.Remove(root))
            {
                return;
            }

            _folders.Save([.. _roots]);
            _live.Remove(root);
        }

        var id = _index.Find(root);
        if (id >= 0 && _index.PathOf(id) == root && _index.Roots.Contains(id))
        {
            _index.Remove(id);
            _store.Save(_index.Export());
        }

        RaiseChanged();
    }

    /// <summary>True when <paramref name="folder"/> is one of the chosen folders or lies inside one.</summary>
    public bool IsIndexed(LocationPath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        lock (_lock)
        {
            return _roots.Exists(r => r.Contains(folder));
        }
    }

    /// <summary>Holds all indexing back (a running crawl waits where it is) or lets it go on.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        RaiseChanged();
    }

    /// <summary>The status of every chosen folder.</summary>
    public IReadOnlyList<IndexRootStatus> Status()
    {
        lock (_lock)
        {
            return [.. _roots.Select(root =>
            {
                var last = _index.CoveredSince(root);
                var live = _live.GetValueOrDefault(root);
                var waiting = _waiting.GetValueOrDefault(root);
                return new IndexRootStatus(root, last, live.Crawling, waiting.Count > 0, live.Progress, live.Crawling ? null : (last is { } at ? at + _schedule.RecrawlEvery : _time.GetUtcNow()), waiting.Count > 0 ? waiting.Reason : live.Message);
            })];
        }
    }

    /// <summary>Lists one chosen folder now, whether or not it is due, and saves the index.</summary>
    public async Task CrawlNowAsync(LocationPath root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _current = linked;
        }

        SetLive(root, crawling: true, progress: null, message: null);
        try
        {
            var options = _crawlOptions with { BeforeFolder = (folder, token) => WaitWhileHeldBackAsync(root, folder, token) };
            var crawler = new IndexCrawler(_providers, _index, options, _time);
            var progress = new InlineProgress(p => SetLive(root, crawling: true, progress: p, message: null));
            var result = await crawler.CrawlAsync(root, progress, linked.Token).ConfigureAwait(false);
            SetLive(root, crawling: false, progress: result, message: result.Errors > 0 ? $"{result.Errors} folders could not be read" : null);
            _store.Save(_index.Export());
        }
        catch (OperationCanceledException)
        {
            SetLive(root, crawling: false, progress: null, message: "Stopped before it finished");
            throw;
        }
        catch (Exception ex) when (ex is LocationException or IOException)
        {
            SetLive(root, crawling: false, progress: null, message: ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
            }
        }
    }

    /// <summary>Looks every <see cref="IndexSchedule.CheckEvery"/> for folders that are due and lists them, one at a time, until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var root in Roots)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!_paused && !_isBusy(root) && IsDue(root))
                {
                    try
                    {
                        await CrawlNowAsync(root, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped from outside (a folder was removed): carry on with the others.
                    }
                }
            }

            try
            {
                await Task.Delay(_schedule.CheckEvery, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Folds a listing the user just looked at into the index, if the folder is one the index holds (spec §13 "change detection"): a folder you open is
    /// never staler than the moment you opened it. Returns true when the index changed.
    /// </summary>
    public bool OnFolderListed(LocationPath folder, IReadOnlyList<FileEntry> entries, bool includedHiddenAndSystem = true)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(entries);
        if (!IsIndexed(folder))
        {
            return false;
        }

        var id = _index.Find(folder);
        if (id < 0)
        {
            return false;
        }

        _index.ReplaceChildren(id, [.. entries.Select(e => new IndexedChild(e.Name, e.Kind == FileEntryKind.Folder, e.Kind == FileEntryKind.Folder ? 0 : e.Size, e.Modified.UtcTicks, e.Attributes))], includedHiddenAndSystem);
        return true;
    }

    /// <summary>Stops the crawl that is running, if any.</summary>
    public void StopCurrent()
    {
        lock (_lock)
        {
            _current?.Cancel();
        }
    }

    /// <inheritdoc />
    public void Dispose() => StopCurrent();

    private bool IsDue(LocationPath root) =>
        _index.CoveredSince(root) is not { } last || _time.GetUtcNow() - last >= _schedule.RecrawlEvery;

    private async Task WaitWhileHeldBackAsync(LocationPath root, LocationPath folder, CancellationToken token)
    {
        if (!_paused && !_isBusy(folder))
        {
            return;
        }

        // Several folders can be held back at once, so the root counts them: it is waiting while any is.
        ChangeWaiting(root, +1, _paused ? "Paused" : "Waiting while a job copies to the same place");
        try
        {
            while (_paused || _isBusy(folder))
            {
                await Task.Delay(_schedule.ResumeCheckEvery, _time, token).ConfigureAwait(false);
            }
        }
        finally
        {
            ChangeWaiting(root, -1, string.Empty);
        }
    }

    private void ChangeWaiting(LocationPath root, int by, string reason)
    {
        lock (_lock)
        {
            var now = _waiting.GetValueOrDefault(root);
            var count = Math.Max(0, now.Count + by);
            if (count == 0)
            {
                _waiting.Remove(root);
            }
            else
            {
                _waiting[root] = (count, by > 0 ? reason : now.Reason);
            }
        }

        RaiseChanged();
    }

    private void SetLive(LocationPath root, bool crawling, CrawlProgress? progress, string? message)
    {
        lock (_lock)
        {
            _live[root] = (crawling, progress, message);
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class InlineProgress(Action<CrawlProgress> report) : IProgress<CrawlProgress>
    {
        public void Report(CrawlProgress value) => report(value);
    }
}
