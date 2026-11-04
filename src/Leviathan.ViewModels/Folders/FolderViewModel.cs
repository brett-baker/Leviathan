using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Folders;

/// <summary>Runs work on the UI thread. The WinUI app wraps its <c>DispatcherQueue</c>; tests run work inline.</summary>
public interface IUiContext
{
    /// <summary>Queues <paramref name="action"/> on the UI thread, in order. Must not block the caller.</summary>
    void Post(Action action);
}

/// <summary>Where a folder listing stands (spec §8.6).</summary>
public enum FolderLoadState
{
    /// <summary>Nothing requested yet.</summary>
    Idle,

    /// <summary>Listing in progress; rows arrive in batches.</summary>
    Loading,

    /// <summary>Listing finished and the folder has items.</summary>
    Loaded,

    /// <summary>Listing finished and the folder is empty.</summary>
    Empty,

    /// <summary>The listing was abandoned (cancelled). Rows shown so far are partial. Never reported as <see cref="Empty"/>.</summary>
    Cancelled,

    /// <summary>The folder does not exist.</summary>
    NotFound,

    /// <summary>Access was denied.</summary>
    AccessDenied,

    /// <summary>The server or share is not responding, timed out or needs credentials (spec §3.3).</summary>
    Offline,

    /// <summary>Any other failure.</summary>
    Error,
}

/// <summary>An observable list that can append many items with a single change notification.</summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the contents with <paramref name="items"/> and raises a single <see cref="NotifyCollectionChangedAction.Reset"/> event.</summary>
    public void Reset(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Removes <paramref name="items"/> (those that are in the list) and raises one <see cref="NotifyCollectionChangedAction.Remove"/> event.</summary>
    public void RemoveRange(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<T>(items);
        var removed = new List<T>(items.Count);
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(Items[i]))
            {
                removed.Add(Items[i]);
                Items.RemoveAt(i);
            }
        }

        if (removed.Count == 0)
        {
            return;
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, -1));
    }

    /// <summary>Appends <paramref name="items"/> and raises one <see cref="NotifyCollectionChangedAction.Add"/> event.</summary>
    public void AddRange(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        var start = Items.Count;
        var added = new List<T>(items);
        foreach (var item in added)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }
}

/// <summary>
/// Lists one folder for a tab. Batches stream in from the provider and reach the UI as one change per batch (spec §3.2),
/// navigating away cancels the old listing and its late batches are dropped, and a cancelled or failed listing is never
/// shown as an empty folder (spec §8.6, CLAUDE.md).
/// </summary>
public sealed partial class FolderViewModel : ObservableObject, IDisposable
{
    private readonly LocationProviderRegistry _providers;
    private readonly IUiContext _ui;
    private readonly TimeProvider _time;
    private readonly IFolderWatcher? _watcher;
    private readonly Lock _watchGate = new();
    private CancellationTokenSource? _current;
    private int _generation;
    private IDisposable? _watch;
    private LocationPath? _wantedWatch;
    private bool _disposed;

    // UI-thread state of the live refresh (see RequestRefresh): a check of the folder is under way, and the disk changed meanwhile.
    private bool _busy;
    private bool _dirty;

    [ObservableProperty]
    private LocationPath? _location;

    [ObservableProperty]
    private FolderLoadState _state = FolderLoadState.Idle;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isRetryable;

    [ObservableProperty]
    private FolderKind _kind = FolderKind.General;

    /// <summary>
    /// What the status bar should say about the listing beyond its state: rows shown from the cache that are being checked, what
    /// the check changed, or why it could not be done. Null when the rows are exactly what the folder holds.
    /// </summary>
    [ObservableProperty]
    private ListingNotice? _notice;

    /// <summary>Creates the view model.</summary>
    /// <param name="providers">Where listings come from.</param>
    /// <param name="ui">The UI thread.</param>
    /// <param name="time">Clock.</param>
    /// <param name="cache">Remembered listings, or null to always list afresh.</param>
    /// <param name="watcher">Reports changes to the folder on screen so the rows follow the disk, or null to leave them as listed.</param>
    public FolderViewModel(LocationProviderRegistry providers, IUiContext ui, TimeProvider? time = null, ListingCache? cache = null, IFolderWatcher? watcher = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(ui);
        _providers = providers;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        Cache = cache;
        _watcher = watcher;
    }

    /// <summary>The remembered listings this folder view shows first, or null.</summary>
    public ListingCache? Cache { get; }

    /// <summary>Items listed so far, in arrival order. Sorting, grouping and filtering sit on top of this list.</summary>
    public RangeObservableCollection<FileEntry> Entries { get; } = [];

    /// <summary>Hidden and system item visibility; takes effect on the next load.</summary>
    public ListingOptions Options { get; set; } = ListingOptions.Default;

    /// <summary>Time from the start of the last load to its first rows reaching the UI queue (the §3.1 budget).</summary>
    public TimeSpan? FirstRowsElapsed { get; private set; }

    /// <summary>Time from the start of the last load to its end.</summary>
    public TimeSpan? TotalElapsed { get; private set; }

    /// <summary>The quickest the UI is updated while a listing is under way, however fast batches arrive.</summary>
    public static readonly TimeSpan MinFlushInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The most rows put into the list by one UI callback. Sorting and merging them costs about a millisecond per hundred rows, so a
    /// callback stays near 20 ms and the window can answer clicks and keys between callbacks, however big the folder is (spec §3.2:
    /// nothing on the UI thread takes over 50 ms).
    /// </summary>
    public const int MaxRowsPerUpdate = 2000;

    /// <summary>The slowest the UI is updated while a listing is under way (a listing that takes a minute still shows progress).</summary>
    public static readonly TimeSpan MaxFlushInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long to hold arriving rows back before the next update of the list: 250 rows per millisecond of interval, so a folder of
    /// a few thousand rows updates about 25 times a second and one of 50,000 rows about five times, never less often than
    /// <see cref="MaxFlushInterval"/>. Every update re-sorts and redraws the rows, which costs more the more rows there are.
    /// </summary>
    public static TimeSpan FlushIntervalFor(int rowsSoFar) =>
        TimeSpan.FromMilliseconds(Math.Clamp(rowsSoFar / 250.0, MinFlushInterval.TotalMilliseconds, MaxFlushInterval.TotalMilliseconds));

    /// <summary>
    /// A cached listing younger than this is shown without being checked again: going back and forward through the same few
    /// folders does not re-list each of them every time.
    /// </summary>
    public static readonly TimeSpan CacheTrustedFor = TimeSpan.FromSeconds(2);

    /// <summary>The last background check of a cached listing, so a test (or a caller) can wait for it to end. Never faults.</summary>
    public Task Revalidation { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Lists <paramref name="location"/>, cancelling any listing in progress. When the cache knows the folder its rows are shown
    /// at once (the task completes then) and the folder is listed again in the background, the difference applied when it ends
    /// (<see cref="Revalidation"/>); without a cached listing, or with <paramref name="forceFresh"/>, rows stream in and the task
    /// completes when the listing ends.
    /// </summary>
    public async Task LoadAsync(LocationPath location, CancellationToken cancellationToken, bool forceFresh = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        var generation = Interlocked.Increment(ref _generation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _current, source);
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
            previous.Dispose();
        }

        StartWatching(location);
        var options = Options;
        var key = new ListingKey(location, options.IncludeHidden, options.IncludeSystem);
        var started = _time.GetTimestamp();
        FirstRowsElapsed = null;
        TotalElapsed = null;
        var pump = new UpdatePump(this, generation, source.Token);

        CachedListing? cached = null;
        if (Cache is not null && !forceFresh)
        {
            try
            {
                cached = await Cache.TryGetAsync(key, source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                PostIfCurrent(generation, () => State = FolderLoadState.Cancelled);
                return;
            }
        }

        if (cached is not null)
        {
            await ShowCachedAsync(location, options, key, cached, pump, generation, source, started).ConfigureAwait(false);
            return;
        }

        var all = new List<FileEntry>();
        PostIfCurrent(generation, () =>
        {
            Location = location;
            Entries.Clear();
            Message = null;
            IsRetryable = false;
            Notice = null;
            _busy = false;
            Kind = FolderKind.General;
            State = FolderLoadState.Loading;
        });

        try
        {
            var provider = _providers.Resolve(location);
            await foreach (var batch in provider.ListAsync(location, options, source.Token).ConfigureAwait(false))
            {
                if (batch.Items.Count > 0)
                {
                    FirstRowsElapsed ??= _time.GetElapsedTime(started);
                    all.AddRange(batch.Items);
                    pump.Add(batch.Items);
                }
            }

            TotalElapsed = _time.GetElapsedTime(started);

            // Whatever is still held back goes to the UI first, a chunk at a time; the state changes when the last chunk is in.
            var kind = FolderKindDetector.Detect(all);
            var finished = all.Count == 0 ? FolderLoadState.Empty : FolderLoadState.Loaded;
            Cache?.Put(key, all.ToArray(), _time.GetUtcNow(), kind);
            PostIfCurrent(generation, () => pump.Drain(() =>
            {
                Kind = kind;
                State = finished;
            }));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // A newer load already reset the state; only the load that was cancelled without a replacement reports it.
            PostIfCurrent(generation, () => State = FolderLoadState.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rows that arrived before the failure stay on screen with the message.
            var failure = ex as LocationException ?? LocationErrors.Wrap(ex, location);
            PostIfCurrent(generation, () => pump.Drain(() => ShowFailure(failure)));
        }
    }

    /// <summary>Shows a cached listing at once and starts checking it against the real folder in the background.</summary>
    private async Task ShowCachedAsync(LocationPath location, ListingOptions options, ListingKey key, CachedListing cached, UpdatePump pump, int generation, CancellationTokenSource source, long started)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = source.Token.Register(() => shown.TrySetCanceled(source.Token));
        var age = _time.GetUtcNow() - cached.ListedAt;
        var trusted = age < CacheTrustedFor;

        PostIfCurrent(generation, () =>
        {
            Location = location;
            Entries.Clear();
            Message = null;
            IsRetryable = false;
            Kind = cached.Kind;
            State = FolderLoadState.Loading;
            _busy = !trusted;
            Notice = trusted ? null : ListingNotice.Checking(cached.Source, age);
        });
        FirstRowsElapsed = _time.GetElapsedTime(started);
        pump.Add(cached.Entries);
        var count = cached.Entries.Count;
        PostIfCurrent(generation, () => pump.Drain(() =>
        {
            State = count == 0 ? FolderLoadState.Empty : FolderLoadState.Loaded;
            shown.TrySetResult();
        }));

        try
        {
            await shown.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TotalElapsed = _time.GetElapsedTime(started);
        if (!trusted)
        {
            Revalidation = RevalidateAsync(location, options, key, cached, pump, generation, source);
        }
    }

    /// <summary>
    /// Lists the folder again behind a cached listing that is on screen and applies what changed: items that are gone are taken
    /// out, new and changed ones put in, nothing else is touched, so the scroll position and the selection stay where they are. If
    /// the folder cannot be reached the cached rows stay and the notice says so.
    /// </summary>
    private async Task RevalidateAsync(LocationPath location, ListingOptions options, ListingKey key, CachedListing cached, UpdatePump pump, int generation, CancellationTokenSource source)
    {
        try
        {
            var fresh = new List<FileEntry>(cached.Entries.Count);
            await foreach (var batch in _providers.Resolve(location).ListAsync(location, options, source.Token).ConfigureAwait(false))
            {
                fresh.AddRange(batch.Items);
            }

            var kind = FolderKindDetector.Detect(fresh);
            var diff = ListingDiff.Compute(cached.Entries, fresh);
            Cache?.Put(key, fresh.ToArray(), _time.GetUtcNow(), kind, changed: !diff.IsEmpty);
            PostIfCurrent(generation, () =>
            {
                Entries.RemoveRange(diff.Remove);
                pump.Add(diff.Add);
                pump.Drain(() =>
                {
                    Kind = kind;
                    State = fresh.Count == 0 ? FolderLoadState.Empty : FolderLoadState.Loaded;
                    Notice = diff.IsEmpty ? null : ListingNotice.Updated(diff.Describe());
                    if (Notice is { } updated)
                    {
                        _ = ClearNoticeLaterAsync(generation, updated, source.Token);
                    }

                    Settled();
                });
            });
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            PostIfCurrent(generation, () => Notice = null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failure = ex as LocationException ?? LocationErrors.Wrap(ex, location);
            var age = _time.GetUtcNow() - cached.ListedAt;
            PostIfCurrent(generation, () =>
            {
                Notice = ListingNotice.RefreshFailed(failure.Message, age);
                Settled();
            });
        }
    }

    partial void OnStateChanged(FolderLoadState value)
    {
        if (_dirty && !_busy && value is FolderLoadState.Loaded or FolderLoadState.Empty)
        {
            RequestRefresh();
        }
    }

    private void StartWatching(LocationPath location)
    {
        if (_watcher is null)
        {
            return;
        }

        lock (_watchGate)
        {
            if (_disposed || _wantedWatch == location)
            {
                return;
            }

            _wantedWatch = location;
        }

        // Starting a watch opens the folder, which can be slow on a bad drive: never on the UI thread.
        _ = Task.Run(() =>
        {
            IDisposable? stale;
            lock (_watchGate)
            {
                if (_disposed || _wantedWatch != location)
                {
                    return;
                }

                stale = _watch;
                _watch = null;
            }

            stale?.Dispose();
            var subscription = _watcher.Watch(location, () => OnWatchedChange(location));
            lock (_watchGate)
            {
                if (!_disposed && _wantedWatch == location)
                {
                    _watch = subscription;
                    return;
                }
            }

            subscription.Dispose();
        });
    }

    // On a watcher thread: the cached listing is out of date now; the view refreshes itself on the UI thread.
    private void OnWatchedChange(LocationPath location)
    {
        Cache?.Invalidate(location);
        _ui.Post(() =>
        {
            if (!_disposed && Location == location)
            {
                RequestRefresh();
            }
        });
    }

    // On the UI thread. The folder changed on disk: list it again behind the rows and apply only the difference (the same way a
    // cached listing is checked), so scrolling, selection and renaming stay put. One check at a time; a change that arrives
    // while the folder is loading or being checked is picked up when that ends.
    private void RequestRefresh()
    {
        if (Location is not { } location || State is not (FolderLoadState.Loading or FolderLoadState.Loaded or FolderLoadState.Empty))
        {
            return;
        }

        if (State is FolderLoadState.Loading || _busy)
        {
            _dirty = true;
            return;
        }

        _dirty = false;
        _busy = true;
        _ = RefreshInPlaceAsync(location, Options, Entries.ToArray(), Volatile.Read(ref _generation), _current?.Token ?? CancellationToken.None);
    }

    private void Settled()
    {
        _busy = false;
        if (_dirty)
        {
            RequestRefresh();
        }
    }

    private async Task RefreshInPlaceAsync(LocationPath location, ListingOptions options, FileEntry[] shown, int generation, CancellationToken token)
    {
        try
        {
            var (fresh, diff) = await Task.Run(async () =>
            {
                var list = new List<FileEntry>(shown.Length);
                await foreach (var batch in _providers.Resolve(location).ListAsync(location, options, token).ConfigureAwait(false))
                {
                    list.AddRange(batch.Items);
                }

                return (list, ListingDiff.Compute(shown, list));
            }, token).ConfigureAwait(false);

            var kind = FolderKindDetector.Detect(fresh);
            Cache?.Put(new ListingKey(location, options.IncludeHidden, options.IncludeSystem), fresh.ToArray(), _time.GetUtcNow(), kind, changed: !diff.IsEmpty);
            if (diff.IsEmpty)
            {
                PostIfCurrent(generation, Settled);
                return;
            }

            var pump = new UpdatePump(this, generation, token);
            PostIfCurrent(generation, () =>
            {
                Entries.RemoveRange(diff.Remove);
                pump.Add(diff.Add);
                pump.Drain(() =>
                {
                    Kind = kind;
                    State = fresh.Count == 0 ? FolderLoadState.Empty : FolderLoadState.Loaded;
                    Settled();
                });
            });
        }
        catch (OperationCanceledException)
        {
            // Another load replaced this one and reset the refresh state itself.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The rows stay as they are; the next change or navigation looks again.
            PostIfCurrent(generation, Settled);
        }
    }

    private async Task ClearNoticeLaterAsync(int generation, ListingNotice notice, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ListingNotice.UpdatedShownFor, _time, cancellationToken).ConfigureAwait(false);
            PostIfCurrent(generation, () =>
            {
                if (ReferenceEquals(Notice, notice))
                {
                    Notice = null;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Another listing replaced this one; it clears the notice itself.
        }
    }

    /// <summary>
    /// Hands arriving rows to the UI in a few updates a second, in chunks (see <see cref="MaxRowsPerUpdate"/>): the first rows at
    /// once, later ones gathered until the interval for the size so far has passed. One per listing.
    /// </summary>
    private sealed class UpdatePump(FolderViewModel owner, int generation, CancellationToken token)
    {
        private readonly Lock _gate = new();
        private readonly List<FileEntry> _pending = [];
        private long? _lastFlush;
        private bool _queued;
        private int _total;

        /// <summary>Takes rows from any thread.</summary>
        public void Add(IReadOnlyList<FileEntry> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            TimeSpan delay;
            lock (_gate)
            {
                _pending.AddRange(items);
                _total += items.Count;
                if (_queued)
                {
                    return;
                }

                _queued = true;
                delay = _lastFlush is { } last ? FlushIntervalFor(_total) - owner._time.GetElapsedTime(last) : TimeSpan.Zero;
            }

            if (delay <= TimeSpan.Zero)
            {
                owner.PostIfCurrent(generation, () => Flush(null));
            }
            else
            {
                _ = FlushLaterAsync(delay);
            }
        }

        /// <summary>On the UI thread: puts every row still pending into the list, chunk by chunk, then runs <paramref name="after"/>.</summary>
        public void Drain(Action? after) => Flush(after);

        private async Task FlushLaterAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, owner._time, token).ConfigureAwait(false);
                owner.PostIfCurrent(generation, () => Flush(null));
            }
            catch (OperationCanceledException)
            {
                // The listing was cancelled or replaced; nothing is shown from it any more.
            }
        }

        // On the UI thread. Puts up to MaxRowsPerUpdate pending rows into the list; when more are waiting it queues itself again,
        // so the dispatcher can handle input in between. 'after' runs once everything pending is in.
        private void Flush(Action? after)
        {
            List<FileEntry> take;
            bool more;
            lock (_gate)
            {
                var count = Math.Min(_pending.Count, MaxRowsPerUpdate);
                take = _pending.GetRange(0, count);
                _pending.RemoveRange(0, count);
                more = _pending.Count > 0;
                _queued = more;
                if (count > 0)
                {
                    _lastFlush = owner._time.GetTimestamp();
                }
            }

            if (take.Count > 0)
            {
                owner.Entries.AddRange(take);
            }

            if (more)
            {
                owner.PostIfCurrent(generation, () => Flush(after));
            }
            else
            {
                after?.Invoke();
            }
        }
    }

    /// <summary>Lists the current folder again (F5). With <paramref name="forceFresh"/> the cache is not shown first.</summary>
    public Task ReloadAsync(CancellationToken cancellationToken, bool forceFresh = false) =>
        Location is null ? Task.CompletedTask : LoadAsync(Location, cancellationToken, forceFresh);

    /// <summary>
    /// Takes items out of the list that a running delete has already removed, without listing the folder again, so the rows vanish
    /// while the job runs and the scroll position, selection and focus stay where they are (spec §3.2, §12.5). UI thread only.
    /// </summary>
    public void RemoveGone(IReadOnlyCollection<LocationPath> gone)
    {
        ArgumentNullException.ThrowIfNull(gone);
        if (gone.Count == 0 || Entries.Count == 0)
        {
            return;
        }

        var wanted = gone as ISet<LocationPath> ?? new HashSet<LocationPath>(gone);
        var rows = new List<FileEntry>(gone.Count);
        foreach (var entry in Entries)
        {
            if (wanted.Contains(entry.Location))
            {
                rows.Add(entry);
            }
        }

        Entries.RemoveRange(rows);
    }

    /// <summary>
    /// Looks at the folder again behind the rows and applies only the difference, the way a change seen by the watcher does: for
    /// after a copy, move or delete ended, and for shares, which are not watched (spec §3.3). Unlike <see cref="ReloadAsync"/> the
    /// rows are not replaced, so the view does not jump back to the top. UI thread only.
    /// </summary>
    public void RefreshInPlace()
    {
        if (Location is { } location)
        {
            Cache?.Invalidate(location);
            RequestRefresh();
        }
    }

    /// <summary>Cancels the listing in progress, if any. The state becomes <see cref="FolderLoadState.Cancelled"/>.</summary>
    public void Cancel() => _current?.Cancel();

    /// <inheritdoc />
    public void Dispose()
    {
        IDisposable? watch;
        lock (_watchGate)
        {
            _disposed = true;
            watch = _watch;
            _watch = null;
        }

        watch?.Dispose();
        Interlocked.Increment(ref _generation);
        var source = Interlocked.Exchange(ref _current, null);
        source?.Cancel();
        source?.Dispose();
    }

    private void ShowFailure(LocationException failure)
    {
        Message = failure.Message;
        IsRetryable = failure.IsTransient || failure.Error == LocationError.AuthenticationRequired;
        State = failure.Error switch
        {
            LocationError.NotFound => FolderLoadState.NotFound,
            LocationError.AccessDenied => FolderLoadState.AccessDenied,
            LocationError.Unreachable or LocationError.Timeout or LocationError.AuthenticationRequired => FolderLoadState.Offline,
            _ => FolderLoadState.Error,
        };
    }

    private void PostIfCurrent(int generation, Action action) =>
        _ui.Post(() =>
        {
            if (Volatile.Read(ref _generation) == generation)
            {
                action();
            }
        });
}
