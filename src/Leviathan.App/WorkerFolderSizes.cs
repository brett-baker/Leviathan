using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Data;
using Leviathan.Engine.FolderSizes;
using Leviathan.ViewModels.Folders;

namespace Leviathan.App;

/// <summary>
/// Folder totals for the Size column (spec §8.2, §12.7). Counting is a background job of the worker (CLAUDE.md: workers own long
/// work); the result lands in the shared SQLite store, which this class reads on the thread pool into a small in-memory cache,
/// so the UI thread only ever reads a dictionary.
/// </summary>
internal sealed class WorkerFolderSizes : IFolderSizeSource, IDisposable
{
    private readonly ConcurrentDictionary<LocationPath, FolderSizeInfo> _cache = new();
    private readonly CountQueue _queue = new();
    private readonly ConcurrentDictionary<LocationPath, FolderProgress> _progress = new();
    private readonly ConcurrentDictionary<LocationPath, long> _lastProgress = new();
    private readonly Func<WorkerConnection> _worker;
    private readonly Lazy<IFolderSizeStore> _store = new(() => new SqliteFolderSizeStore(new LeviathanDatabase(LeviathanDatabase.DefaultPath)));
    private readonly CancellationTokenSource _closing = new();
    private WorkerConnection? _connection;

    /// <summary>Creates the source; <paramref name="worker"/> is called on first use to get the connection to the worker.</summary>
    public WorkerFolderSizes(Func<WorkerConnection> worker) => _worker = worker;

    /// <summary>False when the user turned folder sizes off (Files and folders): nothing is counted and no size is shown.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>False when sizes are not wanted on network drives (Quick settings): such a folder costs a walk of the whole share.</summary>
    public bool NetworkEnabled { get; set; } = true;

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<LocationPath>>? Computed;

    /// <inheritdoc />
    public bool CanCompute(LocationPath folder) =>
        Enabled && folder.Value.Length >= 2 && folder.Value[1] == ':' && (NetworkEnabled || !Leviathan.Providers.Local.MappedDrives.IsMappedDrive(folder));

    /// <inheritdoc />
    public FolderSizeInfo? TryGet(LocationPath folder) => Enabled && CanCompute(folder) && _cache.TryGetValue(folder, out var info) ? info : null;

    /// <inheritdoc />
    public void Request(IReadOnlyList<FileEntry> folders)
    {
        if (!Enabled)
        {
            return;
        }

        var copy = folders.Where(f => CanCompute(f.Location)).ToList();
        _ = Task.Run(() => RequestAsync(copy, _closing.Token), _closing.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closing.Cancel();
        if (_connection is not null)
        {
            _connection.JobChanged -= OnJobChanged;
        }

        _closing.Dispose();
    }

    private async Task RequestAsync(List<FileEntry> folders, CancellationToken cancellationToken)
    {
        try
        {
            var known = new List<LocationPath>();
            var missing = new List<FileEntry>();
            foreach (var folder in folders)
            {
                if (_store.Value.Get(folder.Location) is { } stored)
                {
                    // The worker stores a folder it was asked about without its own last-write time; then the folder is outdated
                    // when it changed after it was counted.
                    var changed = stored.FolderModified is { } counted ? counted != folder.Modified : stored.ComputedAt < folder.Modified;
                    var outdated = changed || stored.State == FolderSizeState.Stale;
                    _cache[folder.Location] = ToInfo(stored, outdated);
                    known.Add(folder.Location);

                    // A folder counted before the Items column existed has no count of its own folders: count it again once.
                    if (outdated || stored.IsLowerBound || stored.OwnFolders is null)
                    {
                        missing.Add(folder);
                    }
                }
                else
                {
                    missing.Add(folder);
                }
            }

            if (known.Count > 0)
            {
                Computed?.Invoke(this, known);
            }

            foreach (var folder in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await QueueAsync(folder.Location);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // No database: folders just show no size. Nothing the user must act on.
            Log(ex);
        }
    }

    // Asks for one folder to be counted unless it is already queued, or a job for a folder above it will store it on the way.
    private async Task QueueAsync(LocationPath folder)
    {
        switch (_queue.Add(folder))
        {
            case CountAdd.Submit:
                await SubmitAsync(folder);
                break;
            case CountAdd.Covered:
                // Waiting for the walk above it: shown as waiting, and read from the store when that walk ends.
                _progress[folder] = new FolderProgress(IsRunning: false, Fraction: null);
                Computed?.Invoke(this, [folder]);
                break;
        }
    }

    /// <summary>
    /// Queues one folder. A folder the worker refuses, or a worker that is not there, only means this folder has no size: it must
    /// never take the app down (this runs on the thread pool, where an unobserved exception ends the process).
    /// </summary>
    private async Task SubmitAsync(LocationPath folder)
    {
        try
        {
            var request = new JobRequest(JobKind.ComputeFolderSize, [folder.Value], null) { Priority = _queue.PriorityFor(folder) };
            var snapshot = await Connection().SubmitAsync(request);
            _queue.Started(folder, snapshot.Id);
            _progress[folder] = new FolderProgress(IsRunning: false, Fraction: null);
            Computed?.Invoke(this, [folder]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var released in _queue.Failed(folder))
            {
                _progress.TryRemove(released, out _);
            }

            _progress.TryRemove(folder, out _);
            Log(ex);
        }
    }

    /// <summary>Counts <paramref name="folder"/> again whatever is stored: the scheduled check of a share the user opted in (spec §12.7).</summary>
    public void Recount(LocationPath folder)
    {
        if (CanCompute(folder))
        {
            _ = Task.Run(() => QueueAsync(folder), _closing.Token);
        }
    }

    /// <inheritdoc />
    public void Prioritize(IReadOnlyCollection<LocationPath> urgent)
    {
        var changes = _queue.SetUrgent(urgent);
        if (changes.Count > 0)
        {
            _ = Task.Run(() => ApplyPrioritiesAsync(changes), _closing.Token);
        }
    }

    private async Task ApplyPrioritiesAsync(IReadOnlyList<PriorityChange> changes)
    {
        try
        {
            foreach (var change in changes)
            {
                await Connection().SetPriorityAsync(change.JobId, change.Priority);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A job that already finished or a worker that is away: the order is only a preference.
            Log(ex);
        }
    }

    private static void Log(Exception ex) =>
        App.Recorder.Record(Microsoft.Extensions.Logging.LogLevel.Warning, "FolderSizes", $"Folder size unavailable: {ex.Message}");

    private WorkerConnection Connection()
    {
        if (_connection is null)
        {
            _connection = _worker();
            _connection.JobChanged += OnJobChanged;
        }

        return _connection;
    }

    /// <inheritdoc />
    public FolderProgress? ProgressOf(LocationPath folder) => _progress.TryGetValue(folder, out var progress) ? progress : null;

    // Progress reaches the Size cell at most this often per folder; the worker reports far more often than the eye needs.
    private static readonly TimeSpan ProgressGap = TimeSpan.FromMilliseconds(100);

    private void OnJobChanged(object? sender, JobSnapshot snapshot)
    {
        if (snapshot.State is JobState.Running && _queue.FolderOf(snapshot.Id) is { } running)
        {
            ReportProgress(running, snapshot);
            return;
        }

        var ended = snapshot.State is JobState.Completed or JobState.Failed or JobState.Cancelled;
        if (ended && snapshot.Kind is JobKind.Copy or JobKind.Move or JobKind.Delete or JobKind.Restore)
        {
            // The worker applied what the job added and removed to the stored sizes (spec §12.7); read them again for what is shown.
            _ = Task.Run(RefreshCached, _closing.Token);
            return;
        }

        if (!ended || _queue.Finished(snapshot.Id) is not { } done)
        {
            return;
        }

        _progress.TryRemove(done.Folder, out _);
        _lastProgress.TryRemove(done.Folder, out _);
        _ = Task.Run(() => FinishedAsync(done.Folder, done.Covered), _closing.Token);
    }

    // A count ended: its folder, and every folder it was covering, now has its size in the store (the walk stores each subfolder).
    // A covered folder the walk did not store (it failed, or was cancelled) is asked for again.
    private async Task FinishedAsync(LocationPath folder, IReadOnlyList<LocationPath> covered)
    {
        try
        {
            var changed = new List<LocationPath>();
            foreach (var location in covered.Prepend(folder))
            {
                _progress.TryRemove(location, out _);
                if (_store.Value.Get(location) is { } stored)
                {
                    _cache[location] = ToInfo(stored, outdated: false);
                    changed.Add(location);
                }
                else if (!ReferenceEquals(location, folder) && location != folder)
                {
                    await QueueAsync(location);
                }
            }

            if (changed.Count > 0)
            {
                Computed?.Invoke(this, changed);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log(ex);
        }
    }

    // Reads the stored size again of every folder this process has shown, and tells the view about the ones that changed.
    private void RefreshCached()
    {
        try
        {
            var changed = new List<LocationPath>();
            foreach (var (folder, shown) in _cache)
            {
                var stored = _store.Value.Get(folder);
                if (stored is null)
                {
                    if (_cache.TryRemove(folder, out _))
                    {
                        changed.Add(folder);
                    }

                    continue;
                }

                var fresh = ToInfo(stored, outdated: stored.State == FolderSizeState.Stale);
                if (fresh != shown)
                {
                    _cache[folder] = fresh;
                    changed.Add(folder);
                }
            }

            if (changed.Count > 0)
            {
                Computed?.Invoke(this, changed);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log(ex);
        }
    }

    private void ReportProgress(LocationPath folder, JobSnapshot snapshot)
    {
        // The worker reports subfolders finished of all of them; no subfolders (or not known yet) gives a bar with no fraction.
        double? fraction = snapshot.ItemsTotal > 0 ? Math.Clamp((double)snapshot.ItemsDone / snapshot.ItemsTotal, 0, 1) : null;
        var next = new FolderProgress(IsRunning: true, fraction);
        var now = Environment.TickCount64;
        var first = !_progress.TryGetValue(folder, out var before) || !before.IsRunning;
        if (!first && _lastProgress.TryGetValue(folder, out var last) && now - last < ProgressGap.TotalMilliseconds)
        {
            return;
        }

        _progress[folder] = next;
        _lastProgress[folder] = now;
        Computed?.Invoke(this, [folder]);
    }

    private static FolderSizeInfo ToInfo(FolderSize size, bool outdated) =>
        new(
            size.TotalBytes,
            size.IsLowerBound,
            outdated,
            size.TotalFiles,
            size.TotalFolders,
            size.ComputedAt,
            Leviathan.Providers.Local.MappedDrives.IsMappedDrive(size.Location),
            size.OwnBytes,
            size.OwnFiles,
            size.OwnFolders);
}
