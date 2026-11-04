using Leviathan.Contracts;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Hub;

/// <summary>One job as the Operations tab shows it.</summary>
/// <param name="Id">The job.</param>
/// <param name="Title">What it does, e.g. "Copy 3 items to \\nas\media".</param>
/// <param name="State">Queued, running, finished and so on.</param>
/// <param name="Percent">0 to 100, or null while the size is not known yet.</param>
/// <param name="Detail">One line under the title: progress, why it waits, or why it failed.</param>
/// <param name="IsActive">True while the job is not finished.</param>
/// <param name="IsFailed">True for a job that failed.</param>
/// <param name="ColorIndex">The job's colour in the strip under the address bar (0 to 7), or -1.</param>
/// <param name="NeedsAttention">True while the job has failed items nobody has looked at yet: the red tip of its strip line (spec §12.6).</param>
/// <param name="Tooltip">The strip line's tooltip: name, percent, bytes, speed and time left.</param>
/// <param name="QueuePosition">Place in the queue (0 = next), or -1 when the job is not queued.</param>
/// <param name="Priority">How soon the job runs among the queued ones.</param>
/// <param name="Prompt">The question the job waits on (Skip / Retry / Skip all like this), or null.</param>
/// <param name="RetryAt">When a job that waits for a share tries again, or null.</param>
public sealed record OperationEntry(
    Guid Id, string Title, JobState State, double? Percent, string Detail, bool IsActive, bool IsFailed, int ColorIndex, bool NeedsAttention = false, string Tooltip = "",
    int QueuePosition = -1, JobPriority Priority = JobPriority.Normal, JobPrompt? Prompt = null, DateTimeOffset? RetryAt = null)
{
    /// <summary>The job is stopped on a question only the user can answer (spec §12.2).</summary>
    public bool HasPrompt => Prompt is not null;

    /// <summary>The question's text: what went wrong with which item.</summary>
    public string PromptText => Prompt is { } p ? $"{p.Message}{Environment.NewLine}{p.Item}" : string.Empty;

    /// <summary>The job ended with items that failed (that is what Failed means), so "Retry failed" has something to do.</summary>
    public bool CanRetryFailed => State == JobState.Failed;

    /// <summary>The group of the list the job sits in.</summary>
    public OperationGroupKind Group => State switch
    {
        JobState.Queued => OperationGroupKind.Queued,
        JobState.Running or JobState.Paused or JobState.Waiting => OperationGroupKind.Running,
        _ => OperationGroupKind.Finished,
    };

    /// <summary>The worker can pause it (spec §12.6).</summary>
    public bool CanPause => State is JobState.Queued or JobState.Waiting or JobState.Running;

    /// <summary>It is paused and can go on.</summary>
    public bool CanResume => State == JobState.Paused;

    /// <summary>It can be started now, above the limits.</summary>
    public bool CanRunNow => State is JobState.Queued or JobState.Paused or JobState.Waiting;

    /// <summary>It has not finished, so it can be cancelled.</summary>
    public bool CanCancel => IsActive;

    /// <summary>It is not running, so it can be taken off the list (a queued or paused job is dropped without running).</summary>
    public bool CanRemove => State != JobState.Running;

    /// <summary>It is queued and not first, so it can go earlier.</summary>
    public bool CanMoveUp => State == JobState.Queued && QueuePosition > 0;

    /// <summary>It is queued, so it can go later (the worker keeps it at the end when it is last already).</summary>
    public bool CanMoveDown => State == JobState.Queued && QueuePosition >= 0;

    /// <summary>It is finished and some of it failed or was cancelled: the report says which items.</summary>
    public bool HasReport => State is JobState.Failed or JobState.Cancelled or JobState.Completed;
}

/// <summary>The groups of the Operations list, in the order they are shown.</summary>
public enum OperationGroupKind
{
    /// <summary>Running, waiting for a share and paused jobs: they hold a place.</summary>
    Running,

    /// <summary>Jobs waiting for a slot.</summary>
    Queued,

    /// <summary>Completed, failed and cancelled jobs, newest first.</summary>
    Finished,
}

/// <summary>One group of the Operations list with its heading.</summary>
/// <param name="Kind">Which group.</param>
/// <param name="Title">The heading, e.g. "Queued (3)".</param>
/// <param name="Entries">The jobs, in the order shown.</param>
public sealed record OperationGroup(OperationGroupKind Kind, string Title, IReadOnlyList<OperationEntry> Entries);

/// <summary>
/// The jobs of the user's operations (copies, moves, deletes, ...) as the Operations tab of the status hub lists them (spec §12.6,
/// §16.1): active jobs first, then the finished ones, newest first, the last <see cref="FinishedKept"/> of them. Background jobs
/// (folder sizes, probing) are not operations and are left to the Background tab. The worker's snapshots arrive on any thread;
/// <see cref="Changed"/> is raised after every change and readers take <see cref="Entries"/>, a snapshot, so the model needs no
/// UI thread of its own.
/// </summary>
public sealed class OperationsModel(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How many finished jobs the list keeps.</summary>
    public const int FinishedKept = 50;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (JobSnapshot Job, long Order)> _jobs = [];
    private readonly HashSet<Guid> _seenFailures = [];
    private long _order;

    /// <summary>Raised after the list changed, on whatever thread delivered the snapshot.</summary>
    public event EventHandler? Changed;

    /// <summary>Takes the worker's latest word on a job.</summary>
    public void Apply(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Lane != JobLane.Operations)
        {
            return;
        }

        lock (_gate)
        {
            // A job keeps its place while it runs (the strip's lines and the panel's rows must not swap on every progress report);
            // it moves to the top of the finished ones only when it ends.
            var order = _jobs.TryGetValue(job.Id, out var before) && !(IsActive(before.Job.State) && !IsActive(job.State)) ? before.Order : ++_order;
            _jobs[job.Id] = (job, order);
            Trim();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The worker forgot a job.</summary>
    public void Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _jobs.Remove(id);
            _seenFailures.Remove(id);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Forgets the finished jobs ("Clear completed").</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            foreach (var id in _jobs.Where(j => !IsActive(j.Value.Job.State)).Select(j => j.Key).ToList())
            {
                _jobs.Remove(id);
                _seenFailures.Remove(id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The list as it is now: active jobs first (running before queued), then finished ones, newest first.</summary>
    public IReadOnlyList<OperationEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                return
                [
                    .. _jobs.Values
                        .OrderBy(j => IsActive(j.Job.State) ? 0 : 1)
                        .ThenBy(j => j.Job.State == JobState.Queued ? 1 : 0)
                        .ThenBy(j => IsActive(j.Job.State) ? j.Order : -j.Order)
                        .Select(j => ToEntry(j.Job, _seenFailures.Contains(j.Job.Id), now)),
                ];
            }
        }
    }

    /// <summary>The list in three groups (Running, Queued, Completed and failed); empty groups are left out (spec §12.6).</summary>
    public IReadOnlyList<OperationGroup> Groups
    {
        get
        {
            var entries = Entries;
            var groups = new List<OperationGroup>(3);
            foreach (var (kind, name) in new[] { (OperationGroupKind.Running, "Running"), (OperationGroupKind.Queued, "Queued"), (OperationGroupKind.Finished, "Completed and failed") })
            {
                var inGroup = entries.Where(e => e.Group == kind).ToList();
                if (inGroup.Count > 0)
                {
                    groups.Add(new OperationGroup(kind, $"{name} ({inGroup.Count})", inGroup));
                }
            }

            return groups;
        }
    }

    /// <summary>
    /// How long everything unfinished will take at the current speed, e.g. "Everything finishes in about 12 min", or null while it cannot be
    /// said (a job's size is still being counted, nothing moves, or fewer than two jobs are unfinished).
    /// </summary>
    public string? QueueEta
    {
        get
        {
            lock (_gate)
            {
                var open = _jobs.Values.Select(j => j.Job).Where(j => IsActive(j.State)).ToList();
                if (open.Count < 2 || open.Any(j => j.BytesTotal <= 0))
                {
                    return null;
                }

                var speed = open.Where(j => j.State == JobState.Running).Sum(j => j.BytesPerSecond);
                if (speed <= 0)
                {
                    return null;
                }

                var left = open.Sum(j => Math.Max(0, j.BytesTotal - j.BytesDone));
                var time = TimeSpan.FromSeconds(left / speed);
                return time.TotalSeconds switch
                {
                    < 60 => "Everything finishes in under a minute",
                    < 3600 => $"Everything finishes in about {Math.Ceiling(time.TotalMinutes):0} min",
                    _ => $"Everything finishes in about {time.TotalHours:0.#} h",
                };
            }
        }
    }

    /// <summary>The questions jobs wait on now (their ids), so the window can bring the Operations tab up once for each new one (spec §12.2).</summary>
    public IReadOnlyList<Guid> OpenPrompts
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Values.Where(j => IsActive(j.Job.State) && j.Job.Prompt is not null).Select(j => j.Job.Prompt!.Id)];
            }
        }
    }

    /// <summary>Jobs running now (waiting for a share and paused ones count: they hold a place).</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values.Count(j => IsActive(j.Job.State) && j.Job.State != JobState.Queued);
            }
        }
    }

    /// <summary>Jobs waiting for a slot.</summary>
    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values.Count(j => j.Job.State == JobState.Queued);
            }
        }
    }

    /// <summary>Failed jobs the user has not looked at yet: the badge on the hub's chevron (spec §16.1).</summary>
    public int UnseenFailures
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values.Count(j => j.Job.State == JobState.Failed && !_seenFailures.Contains(j.Job.Id));
            }
        }
    }

    /// <summary>
    /// What the newest failure nobody has looked at says, cut to <paramref name="maxLength"/> characters: the tooltip of the failure
    /// mark in the status bar (the full text is in the Operations tab). Null when there is none.
    /// </summary>
    public string? LatestUnseenFailure(int maxLength = 100)
    {
        lock (_gate)
        {
            var newest = _jobs.Values
                .Where(j => j.Job.State == JobState.Failed && !_seenFailures.Contains(j.Job.Id))
                .OrderByDescending(j => j.Order)
                .Select(j => j.Job)
                .FirstOrDefault();
            if (newest is null)
            {
                return null;
            }

            var text = $"{newest.Title}: {newest.Error ?? "failed"}".ReplaceLineEndings(" ");
            return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");
        }
    }

    /// <summary>The status bar chip: "2 jobs · 1 queued · 112 MB/s", or empty when nothing is going on.</summary>
    public string Summary
    {
        get
        {
            lock (_gate)
            {
                var running = _jobs.Values.Where(j => IsActive(j.Job.State) && j.Job.State != JobState.Queued).Select(j => j.Job).ToList();
                var queued = _jobs.Values.Count(j => j.Job.State == JobState.Queued);
                if (running.Count == 0 && queued == 0)
                {
                    return string.Empty;
                }

                var parts = new List<string> { running.Count + queued == 1 ? "1 job" : $"{running.Count + queued} jobs" };
                if (running.Count > 0 && queued > 0)
                {
                    parts.Add($"{queued} queued");
                }

                var speed = running.Where(j => j.State == JobState.Running).Sum(j => j.BytesPerSecond);
                if (speed > 0)
                {
                    parts.Add(DisplayFormat.Size((long)speed) + "/s");
                }

                if (running.Count > 0 && running.All(j => j.Remaining is not null))
                {
                    parts.Add(Remaining(running.Max(j => j.Remaining!.Value)));
                }

                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>The user has looked at the list: the badge goes away.</summary>
    public void MarkFailuresSeen()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(j => j.Job.State == JobState.Failed))
            {
                _seenFailures.Add(job.Job.Id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string Remaining(TimeSpan time) => time.TotalSeconds switch
    {
        < 60 => "under a minute left",
        < 3600 => $"{Math.Ceiling(time.TotalMinutes):0} min left",
        _ => $"{time.TotalHours:0.#} h left",
    };

    private static bool IsActive(JobState state) => state is JobState.Queued or JobState.Running or JobState.Paused or JobState.Waiting;

    // Finished jobs beyond the limit go, oldest first.
    private void Trim()
    {
        var finished = _jobs.Where(j => !IsActive(j.Value.Job.State)).OrderBy(j => j.Value.Order).ToList();
        for (var i = 0; i < finished.Count - FinishedKept; i++)
        {
            _jobs.Remove(finished[i].Key);
            _seenFailures.Remove(finished[i].Key);
        }
    }

    private static OperationEntry ToEntry(JobSnapshot job, bool failureSeen, DateTimeOffset now)
    {
        // Copies know their bytes; a delete (or any job that only counts items) is measured over the whole batch by items, so the bar
        // runs from 0 to 100 once for the job instead of sweeping endlessly.
        double? percent = job.State == JobState.Completed ? 100
            : job.BytesTotal > 0 ? Math.Clamp(100.0 * job.BytesDone / job.BytesTotal, 0, 100)
            : job.ItemsTotal > 0 ? Math.Clamp(100.0 * job.ItemsDone / job.ItemsTotal, 0, 100)
            : null;
        var needsAttention = job.State == JobState.Failed ? !failureSeen : IsActive(job.State) && (job.Error is not null || job.Prompt is not null);
        return new OperationEntry(
            job.Id,
            job.Title,
            job.State,
            percent,
            DetailOf(job, now),
            IsActive(job.State),
            job.State == JobState.Failed,
            job.ColorIndex,
            needsAttention,
            TooltipOf(job, percent),
            job.QueuePosition,
            job.Priority,
            job.Prompt,
            job.State == JobState.Waiting ? job.RetryAt : null);
    }

    /// <summary>"Waiting for \\nas, retrying in 8 s" (spec §12.2): the reason the job gave, then the seconds left until it tries again.</summary>
    internal static string WaitingText(string? reason, DateTimeOffset? retryAt, DateTimeOffset now)
    {
        var text = reason ?? "Waiting for the location to answer";
        if (retryAt is not { } at)
        {
            return text;
        }

        var seconds = (int)Math.Ceiling((at - now).TotalSeconds);
        return seconds <= 0 ? $"{text}, retrying now" : seconds < 60 ? $"{text}, retrying in {seconds} s" : $"{text}, retrying in {Math.Ceiling(seconds / 60.0):0} min";
    }

    private static string TooltipOf(JobSnapshot job, double? percent)
    {
        var parts = new List<string>();
        parts.Add(percent is { } p ? $"{p:0}%" : "counting items");
        if (job.BytesTotal > 0)
        {
            parts.Add($"{DisplayFormat.Size(job.BytesDone)} of {DisplayFormat.Size(job.BytesTotal)}");
        }

        switch (job.State)
        {
            case JobState.Paused:
                parts.Add("paused");
                break;
            case JobState.Waiting:
                parts.Add(job.WaitingReason ?? "waiting for the location");
                break;
            case JobState.Failed:
                parts.Add(job.Error ?? "failed");
                break;
            default:
                if (job.BytesPerSecond > 0)
                {
                    parts.Add(DisplayFormat.Size((long)job.BytesPerSecond) + "/s");
                }

                if (job.Remaining is { } left)
                {
                    parts.Add(Remaining(left));
                }

                break;
        }

        return $"{job.Title}{Environment.NewLine}{string.Join(" · ", parts)}";
    }

    private static string DetailOf(JobSnapshot job, DateTimeOffset now)
    {
        if (job.Prompt is { } prompt && job.State is JobState.Running or JobState.Waiting)
        {
            return $"Needs an answer: {prompt.Message}";
        }

        var items = job.ItemsTotal > 0 && job.BytesTotal <= 0 ? $"{job.ItemsDone:N0} of {job.ItemsTotal:N0} items" : job.ItemsDone > 0 ? $"{job.ItemsDone:N0} items · " : string.Empty;
        var itemsOnly = job.ItemsTotal > 0 && job.BytesTotal <= 0 && job.BytesDone == 0;
        var size = itemsOnly ? string.Empty
            : job.BytesTotal > 0 ? $"{DisplayFormat.Size(job.BytesDone)} of {DisplayFormat.Size(job.BytesTotal)}" : DisplayFormat.Size(job.BytesDone);
        return job.State switch
        {
            JobState.Queued => job.WaitingReason ?? "Queued",
            JobState.Waiting => WaitingText(job.WaitingReason, job.RetryAt, now),
            JobState.Paused => "Paused",
            JobState.Failed => job.Error ?? "Failed",
            JobState.Cancelled => "Cancelled",
            JobState.Completed => $"{items}{size}".TrimEnd(' ', '·'),
            _ => $"{items}{size}{(job.BytesPerSecond > 0 ? " · " + DisplayFormat.Size((long)job.BytesPerSecond) + "/s" : string.Empty)}{(job.CurrentItem is null ? string.Empty : " · " + job.CurrentItem)}",
        };
    }
}
