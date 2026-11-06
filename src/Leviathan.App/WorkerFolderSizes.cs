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
    private readonly ConcurrentDictionary<Guid, LocationPath> _jobs = new();
    private readonly ConcurrentDictionary<LocationPath, bool> _queued = new();
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

            foreach (var folder in missing.Where(f => _queued.TryAdd(f.Location, true)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SubmitAsync(folder.Location);
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

    /// <summary>
    /// Queues one folder. A folder the worker refuses, or a worker that is not there, only means this folder has no size: it must
    /// never take the app down (this runs on the thread pool, where an unobserved exception ends the process).
    /// </summary>
    private async Task SubmitAsync(LocationPath folder)
    {
        try
        {
            var request = new JobRequest(JobKind.ComputeFolderSize, [folder.Value], null) { Priority = JobPriority.Low };
            var snapshot = await Connection().SubmitAsync(request);
            _jobs[snapshot.Id] = folder;
            _progress[folder] = new FolderProgress(IsRunning: false, Fraction: null);
            Computed?.Invoke(this, [folder]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _queued.TryRemove(folder, out _);
            _progress.TryRemove(folder, out _);
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
        if (snapshot.State is JobState.Running && _jobs.TryGetValue(snapshot.Id, out var running))
        {
            ReportProgress(running, snapshot);
            return;
        }

        if (snapshot.State is not (JobState.Completed or JobState.Failed or JobState.Cancelled) || !_jobs.TryRemove(snapshot.Id, out var folder))
        {
            return;
        }

        _progress.TryRemove(folder, out _);
        _lastProgress.TryRemove(folder, out _);
        _queued.TryRemove(folder, out _);
        _ = Task.Run(
            () =>
            {
                if (_store.Value.Get(folder) is { } stored)
                {
                    _cache[folder] = ToInfo(stored, outdated: false);
                    Computed?.Invoke(this, [folder]);
                }
            },
            _closing.Token);
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
