using Leviathan.Contracts;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Hub;

/// <summary>One background job as the Background tab lists it.</summary>
/// <param name="Id">The job.</param>
/// <param name="Title">What it does, e.g. "Count C:\media".</param>
/// <param name="State">Queued, running, paused and so on.</param>
/// <param name="Priority">Low, normal or high.</param>
/// <param name="Detail">One line: what it is on, where it waits, or that it is paused.</param>
/// <param name="CanPause">True while it runs or waits and can be paused.</param>
/// <param name="CanResume">True while it is paused.</param>
public sealed record BackgroundTaskEntry(Guid Id, string Title, JobState State, JobPriority Priority, string Detail, bool CanPause, bool CanResume)
{
    /// <summary>True when the row has a Pause or a Resume button.</summary>
    public bool CanPauseOrResume => CanPause || CanResume;
}

/// <summary>One of the app's own processes as the Background tab shows it.</summary>
/// <param name="Name">Worker, Shell host.</param>
/// <param name="State">Running, idle, restarting, stopped, disabled.</param>
/// <param name="ProcessId">The process id while it runs.</param>
/// <param name="Restarts">How many times it was started again after a crash.</param>
/// <param name="Detail">A line of explanation (why it is disabled, what it is for).</param>
/// <param name="CanReenable">True when the user can switch it back on.</param>
public sealed record ProcessEntry(string Name, string State, int? ProcessId, int Restarts, string Detail, bool CanReenable = false);

/// <summary>What the app does in the background for network folders (spec §16.1, §3.3).</summary>
/// <param name="Watched">Network folders being watched for changes by polling.</param>
/// <param name="CachedFolders">Folder listings held in memory (kept on disk too, so a share can be shown while it is away).</param>
/// <param name="CachedItems">Items in those listings.</param>
/// <param name="ItemCapacity">The most items the cache holds.</param>
public sealed record NetworkStats(int Watched, int CachedFolders, int CachedItems, int ItemCapacity);

/// <summary>Everything the Background tab shows at one moment.</summary>
/// <param name="Tasks">Background jobs, active first.</param>
/// <param name="FolderSizes">The folder-size line: running, queued and the rate.</param>
/// <param name="Thumbnails">The thumbnail line.</param>
/// <param name="Processes">Worker and shell host.</param>
/// <param name="AnyActive">True when any job is running, queued or paused.</param>
/// <param name="AllPaused">True when every active job is paused.</param>
/// <param name="Network">The network line: folders watched for changes and the listings kept for offline use.</param>
public sealed record BackgroundView(IReadOnlyList<BackgroundTaskEntry> Tasks, string FolderSizes, string Thumbnails, IReadOnlyList<ProcessEntry> Processes, bool AnyActive, bool AllPaused, string Network = "Not in use");

/// <summary>
/// What the Background tab of the status hub shows (spec §16.1): the worker's background lane (folder sizes, later metadata
/// probing and indexing) with progress and the rate they finish at, the thumbnail cache, and the state and restart count of the
/// worker and the shell host. Jobs arrive as snapshots from any thread; the other numbers are read when the view is built.
/// Pausing and priorities are the worker's (<see cref="Leviathan.Contracts.IWorkerService"/>), asked for by the window.
/// </summary>
public sealed class BackgroundModel
{
    /// <summary>The most job rows shown (a big folder queues thousands).</summary>
    public const int RowLimit = 40;

    /// <summary>Finished jobs kept in the list.</summary>
    public const int FinishedKept = 5;

    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (JobSnapshot Job, long Order)> _jobs = [];
    private readonly Queue<DateTimeOffset> _finishedAt = new();
    private readonly Func<ThumbnailStats?> _thumbnails;
    private readonly IReadOnlyList<Func<ProcessEntry>> _processes;
    private readonly TimeProvider _time;
    private long _order;

    /// <summary>Creates the model.</summary>
    /// <param name="thumbnails">Reads the thumbnail cache, or null when there is none.</param>
    /// <param name="processes">Read the state of each process; called when the view is built.</param>
    /// <param name="time">Clock.</param>
    public BackgroundModel(Func<ThumbnailStats?>? thumbnails = null, IReadOnlyList<Func<ProcessEntry>>? processes = null, TimeProvider? time = null)
    {
        _thumbnails = thumbnails ?? (() => null);
        _processes = processes ?? [];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Reads the network numbers; set once the folder cache and the watcher exist (they are made after this model).</summary>
    public Func<NetworkStats?>? Network { get; set; }

    /// <summary>Raised after a job changed, on whatever thread delivered the snapshot.</summary>
    public event EventHandler? Changed;

    /// <summary>Takes the worker's latest word on a job; jobs of the operations lane are not listed here.</summary>
    public void Apply(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Lane != JobLane.Background)
        {
            return;
        }

        lock (_gate)
        {
            var wasFinished = _jobs.TryGetValue(job.Id, out var before) && IsFinished(before.Job.State);
            _jobs[job.Id] = (job, ++_order);
            if (job.State == JobState.Completed && !wasFinished)
            {
                _finishedAt.Enqueue(_time.GetUtcNow());
            }

            Trim();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The ids of the jobs that are running or waiting (what "Pause all" acts on).</summary>
    public IReadOnlyList<Guid> RunningOrQueuedIds()
    {
        lock (_gate)
        {
            return [.. _jobs.Values.Where(j => j.Job.State is JobState.Queued or JobState.Running or JobState.Waiting).Select(j => j.Job.Id)];
        }
    }

    /// <summary>The ids of the paused jobs (what "Resume all" acts on).</summary>
    public IReadOnlyList<Guid> PausedIds()
    {
        lock (_gate)
        {
            return [.. _jobs.Values.Where(j => j.Job.State == JobState.Paused).Select(j => j.Job.Id)];
        }
    }

    /// <summary>The tab as it is now.</summary>
    public BackgroundView View()
    {
        List<JobSnapshot> jobs;
        int finishedRecently;
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - RateWindow;
            while (_finishedAt.Count > 0 && _finishedAt.Peek() < cutoff)
            {
                _finishedAt.Dequeue();
            }

            finishedRecently = _finishedAt.Count;
            jobs =
            [
                .. _jobs.Values
                    .OrderBy(j => Rank(j.Job.State))
                    .ThenByDescending(j => j.Order)
                    .Select(j => j.Job),
            ];
        }

        var running = jobs.Count(j => j.State == JobState.Running);
        var queued = jobs.Count(j => j.State is JobState.Queued or JobState.Waiting);
        var paused = jobs.Count(j => j.State == JobState.Paused);
        var active = running + queued + paused;
        var rows = jobs.Take(RowLimit).Select(ToEntry).ToList();
        return new BackgroundView(
            rows,
            FolderSizeLine(running, queued, paused, finishedRecently),
            ThumbnailLine(_thumbnails()),
            [.. _processes.Select(read => read())],
            active > 0,
            active > 0 && paused == active,
            NetworkLine(Network?.Invoke()));
    }

    private static string NetworkLine(NetworkStats? stats)
    {
        if (stats is null)
        {
            return "Not in use";
        }

        var watching = stats.Watched == 0
            ? "No network folder watched"
            : $"Watching {stats.Watched} network folder{(stats.Watched == 1 ? string.Empty : "s")} for changes";
        var kept = stats.CachedFolders == 0
            ? "no listings kept for offline use"
            : $"{stats.CachedFolders:N0} folder listing{(stats.CachedFolders == 1 ? string.Empty : "s")} kept for offline use ({stats.CachedItems:N0} of {stats.ItemCapacity:N0} items)";
        return $"{watching} · {kept}";
    }

    private static string FolderSizeLine(int running, int queued, int paused, int finishedRecently)
    {
        if (running + queued + paused == 0)
        {
            return finishedRecently > 0 ? $"Idle · {finishedRecently} folder{(finishedRecently == 1 ? string.Empty : "s")} counted in the last minute" : "Idle";
        }

        var parts = new List<string>();
        if (running > 0)
        {
            parts.Add($"{running} running");
        }

        if (queued > 0)
        {
            parts.Add($"{queued} queued");
        }

        if (paused > 0)
        {
            parts.Add($"{paused} paused");
        }

        parts.Add($"{finishedRecently} finished in the last minute");
        return string.Join(" · ", parts);
    }

    private static string ThumbnailLine(ThumbnailStats? stats)
    {
        if (stats is null)
        {
            return "Not in use";
        }

        var line = $"{stats.Cached:N0} thumbnails held ({DisplayFormat.Size(stats.Bytes)} of {DisplayFormat.Size(stats.Capacity)})";
        if (stats.Generating > 0)
        {
            line += $" · {stats.Generating} being made";
        }

        if (stats.Failed > 0)
        {
            line += $" · {stats.Failed} files could not be drawn";
        }

        return line;
    }

    private static int Rank(JobState state) => state switch
    {
        JobState.Running => 0,
        JobState.Waiting => 1,
        JobState.Paused => 2,
        JobState.Queued => 3,
        _ => 4,
    };

    private static bool IsFinished(JobState state) => state is JobState.Completed or JobState.Failed or JobState.Cancelled;

    private void Trim()
    {
        var finished = _jobs.Where(j => IsFinished(j.Value.Job.State)).OrderBy(j => j.Value.Order).ToList();
        for (var i = 0; i < finished.Count - FinishedKept; i++)
        {
            _jobs.Remove(finished[i].Key);
        }
    }

    private static BackgroundTaskEntry ToEntry(JobSnapshot job) => new(
        job.Id,
        job.Title,
        job.State,
        job.Priority,
        job.State switch
        {
            JobState.Queued => job.QueuePosition >= 0 ? $"Queued, number {job.QueuePosition + 1}" : "Queued",
            JobState.Waiting => job.WaitingReason ?? "Waiting",
            JobState.Paused => "Paused",
            JobState.Failed => job.Error ?? "Failed",
            JobState.Cancelled => "Cancelled",
            JobState.Completed => job.ItemsDone > 0 ? $"{job.ItemsDone:N0} items, {DisplayFormat.Size(job.BytesDone)}" : "Done",
            _ => job.CurrentItem ?? (job.ItemsDone > 0 ? $"{job.ItemsDone:N0} items so far" : "Working"),
        },
        job.State is JobState.Queued or JobState.Running or JobState.Waiting,
        job.State == JobState.Paused);
}
