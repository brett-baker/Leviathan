using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leviathan.Engine.Execution;

/// <summary>
/// Runs what the <see cref="JobScheduler"/> allows: starts executors, handles pause and cancel, moves jobs whose share
/// dropped to Waiting and retries them with back-off, writes the journal, and publishes throttled snapshots.
/// This is the façade the worker's IPC service calls.
/// </summary>
public sealed class JobHost : IAsyncDisposable
{
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(7);

    private readonly JobScheduler _scheduler;
    private readonly IReadOnlyList<IJobExecutor> _executors;
    private readonly IJobJournal _journal;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly BackoffPolicy _retryBackoff;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, RunState> _running = new();
    private readonly ConcurrentDictionary<Guid, JobContext> _progress = new();
    private readonly ConcurrentDictionary<Guid, int> _retryAttempts = new();
    private readonly ConcurrentDictionary<Guid, long> _lastPublished = new();
    private Task? _loop;

    /// <summary>Creates the host.</summary>
    public JobHost(
        JobScheduler scheduler,
        IEnumerable<IJobExecutor> executors,
        IJobJournal journal,
        TimeProvider? time = null,
        ILogger<JobHost>? logger = null,
        BackoffPolicy? retryBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(journal);
        _scheduler = scheduler;
        _executors = [.. executors];
        _journal = journal;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<JobHost>.Instance;
        _retryBackoff = retryBackoff ?? BackoffPolicy.Network;
        _scheduler.Changed += OnSchedulerChanged;
    }

    /// <summary>Raised when a job's snapshot changes. Progress updates are throttled to four per second per job.</summary>
    public event EventHandler<JobSnapshot>? JobChanged;

    /// <summary>Raised when a job is removed from the list.</summary>
    public event EventHandler<Guid>? JobRemoved;

    /// <summary>The scheduler (exposed for limits and inspection).</summary>
    public JobScheduler Scheduler => _scheduler;

    /// <summary>True while at least one job is running or queued.</summary>
    public bool IsBusy => _scheduler.Snapshot().Any(j => !j.IsFinished);

    /// <summary>Re-queues unfinished jobs from the journal, then starts the scheduling loop.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The host is already running.");
        }

        var recovered = JournalReplay.Recover(_journal.ReadAll());
        foreach (var job in recovered)
        {
            var request = job.Request with { StartPaused = job.LastState == JobState.Paused };
            _scheduler.Enqueue(request, job.Lane, job.DeviceKey, job.Title, job.JobId);
        }

        // Keep the journal small: only what is still pending.
        _journal.Compact(recovered.Select(j => new JournalRecord
        {
            Type = JournalRecordType.Enqueued,
            JobId = j.JobId,
            At = _time.GetUtcNow(),
            Request = j.Request with { StartPaused = j.LastState == JobState.Paused },
            Lane = j.Lane,
            DeviceKey = j.DeviceKey,
            Title = j.Title,
        }));

        if (recovered.Count > 0)
        {
            _logger.LogInformation("Recovered {Count} unfinished job(s) from the journal", recovered.Count);
        }

        _loop = Task.Run(() => LoopAsync(_stopping.Token));
        _signal.Release();
    }

    /// <summary>Queues a job.</summary>
    public JobSnapshot Submit(JobRequest request, JobLane lane, string? deviceKey, string title)
    {
        var job = _scheduler.Enqueue(request, lane, deviceKey, title);
        _journal.Append(new JournalRecord
        {
            Type = JournalRecordType.Enqueued,
            JobId = job.Id,
            At = _time.GetUtcNow(),
            Request = request,
            Lane = lane,
            DeviceKey = deviceKey,
            Title = title,
        });
        _signal.Release();
        return Snapshot(job);
    }

    /// <summary>What happened to each item of a job so far, or null when the job is unknown or has no results yet.</summary>
    public JobResult? GetResult(Guid jobId) =>
        _scheduler.Find(jobId) is null ? null : new JobResult(jobId, _progress.TryGetValue(jobId, out var context) ? context.Results : []);

    /// <summary>Pauses a job; a running job's executor is stopped and can resume later.</summary>
    public void Pause(Guid jobId)
    {
        if (_scheduler.Pause(jobId) == JobState.Running && _running.TryGetValue(jobId, out var run))
        {
            run.Cancel();
        }
    }

    /// <summary>Resumes a paused job.</summary>
    public void Resume(Guid jobId)
    {
        if (_scheduler.Resume(jobId))
        {
            _signal.Release();
        }
    }

    /// <summary>Cancels a job.</summary>
    public void Cancel(Guid jobId)
    {
        if (_scheduler.Cancel(jobId) == JobState.Running && _running.TryGetValue(jobId, out var run))
        {
            run.Cancel();
        }

        _signal.Release();
    }

    /// <summary>Removes a job that is not running.</summary>
    public bool Remove(Guid jobId) => _scheduler.Remove(jobId);

    /// <summary>Moves a queued job.</summary>
    public bool Move(Guid jobId, int position) => _scheduler.Move(jobId, position);

    /// <summary>Changes a job's priority.</summary>
    public bool SetPriority(Guid jobId, JobPriority priority)
    {
        var changed = _scheduler.SetPriority(jobId, priority);
        _signal.Release();
        return changed;
    }

    /// <summary>Starts a job now, above the limits.</summary>
    public void RunNow(Guid jobId)
    {
        if (_scheduler.RunNow(jobId))
        {
            _signal.Release();
        }
    }

    /// <summary>Changes the limits; takes effect for the next start.</summary>
    public void SetLimits(ConcurrencyLimits limits)
    {
        _scheduler.SetLimits(limits);
        _signal.Release();
    }

    /// <summary>Pauses every job that is not finished.</summary>
    public void PauseAll()
    {
        foreach (var job in _scheduler.Snapshot().Where(j => !j.IsFinished))
        {
            Pause(job.Id);
        }
    }

    /// <summary>Resumes every paused job.</summary>
    public void ResumeAll()
    {
        foreach (var job in _scheduler.Snapshot().Where(j => j.State == JobState.Paused))
        {
            _scheduler.Resume(job.Id);
        }

        _signal.Release();
    }

    /// <summary>Snapshots of every job, in display order.</summary>
    public IReadOnlyList<JobSnapshot> GetSnapshots() => [.. _scheduler.Snapshot().Select(Snapshot)];

    /// <summary>Builds the snapshot for one job.</summary>
    public JobSnapshot Snapshot(ScheduledJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _progress.TryGetValue(job.Id, out var ctx);
        var remaining = ctx is not null && ctx.BytesTotal >= 0 ? ctx.Speed.EstimateRemaining(ctx.BytesTotal - ctx.BytesDone) : null;
        return new JobSnapshot
        {
            Id = job.Id,
            Kind = job.Request.Kind,
            Lane = job.Lane,
            Title = job.Title,
            State = job.State,
            Priority = job.Priority,
            QueuePosition = _scheduler.QueuePosition(job.Id),
            ColorIndex = job.ColorIndex,
            DeviceKey = job.DeviceKey,
            BytesDone = ctx?.BytesDone ?? 0,
            BytesTotal = ctx?.BytesTotal ?? -1,
            ItemsDone = ctx?.ItemsDone ?? 0,
            ItemsTotal = ctx?.ItemsTotal ?? -1,
            BytesPerSecond = job.State == JobState.Running ? ctx?.Speed.BytesPerSecond ?? 0 : 0,
            SpeedHistory = ctx?.Speed.History() ?? [],
            Remaining = job.State == JobState.Running ? remaining : null,
            WaitingReason = job.WaitingReason,
            CurrentItem = ctx?.CurrentItem,
            Error = job.Error,
            CreatedAt = job.CreatedAt,
        };
    }

    /// <summary>Stops the loop and every running executor.</summary>
    public async ValueTask DisposeAsync()
    {
        _scheduler.Changed -= OnSchedulerChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var run in _running.Values)
        {
            run.Cancel();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        await Task.WhenAll(_running.Values.Select(r => r.Completion)).ConfigureAwait(false);
        _stopping.Dispose();
        _signal.Dispose();
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        var lastPrune = _time.GetTimestamp();
        while (!stopping.IsCancellationRequested)
        {
            // Wake on any change, and at least once a second for housekeeping.
            await _signal.WaitAsync(TimeSpan.FromSeconds(1), stopping).ConfigureAwait(false);
            while (_signal.CurrentCount > 0 && _signal.Wait(0, CancellationToken.None))
            {
                // Coalesce bursts of signals into one scheduling pass.
            }

            foreach (var job in _scheduler.TakeRunnable())
            {
                StartJob(job, stopping);
            }

            if (_time.GetElapsedTime(lastPrune) > TimeSpan.FromMinutes(10))
            {
                _scheduler.PruneFinished(FinishedRetention);
                lastPrune = _time.GetTimestamp();
            }
        }
    }

    private void StartJob(ScheduledJob job, CancellationToken stopping)
    {
        var executor = _executors.FirstOrDefault(e => e.CanExecute(job.Request.Kind));
        if (executor is null)
        {
            _scheduler.MarkFinished(job.Id, JobState.Failed, $"{job.Request.Kind} jobs are not supported yet.");
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var context = _progress.GetOrAdd(job.Id, _ => new JobContext(job, new SpeedMeter(_time), OnProgress));
        var completion = Task.Run(() => RunAsync(job, executor, context, cts), CancellationToken.None);
        _running[job.Id] = new RunState(cts, completion);
    }

    private async Task RunAsync(ScheduledJob job, IJobExecutor executor, JobContext context, CancellationTokenSource cts)
    {
        try
        {
            var outcome = await executor.ExecuteAsync(context, cts.Token).ConfigureAwait(false);
            if (_scheduler.Find(job.Id)?.State == JobState.Running)
            {
                _scheduler.MarkFinished(job.Id, outcome.State, outcome.Error);
            }

            _retryAttempts.TryRemove(job.Id, out _);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Paused jobs keep their Paused state; anything else that was stopped is Cancelled.
            if (_scheduler.Find(job.Id)?.State == JobState.Running && !_stopping.IsCancellationRequested)
            {
                _scheduler.MarkFinished(job.Id, JobState.Cancelled);
            }
        }
        catch (LocationException ex) when (ex.IsTransient)
        {
            var attempt = _retryAttempts.AddOrUpdate(job.Id, 0, (_, a) => a + 1);
            var delay = _retryBackoff.GetDelay(attempt);
            _logger.LogWarning(ex, "Job {JobId} waiting {Delay} for {Location}", job.Id, delay, ex.Location);
            _scheduler.MarkWaiting(job.Id, $"Waiting for {ex.Location.Server ?? ex.Location.Root} — retrying in {Math.Ceiling(delay.TotalSeconds)} s");
            _ = RequeueLaterAsync(job.Id, delay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} ({Kind}) failed", job.Id, job.Request.Kind);
            if (_scheduler.Find(job.Id)?.State == JobState.Running)
            {
                _scheduler.MarkFinished(job.Id, JobState.Failed, ex.Message);
            }
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
            cts.Dispose();
            if (!_stopping.IsCancellationRequested)
            {
                _signal.Release();
            }
        }
    }

    private async Task RequeueLaterAsync(Guid jobId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _time, _stopping.Token).ConfigureAwait(false);
            _scheduler.Requeue(jobId);
            _signal.Release();
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the journal brings the job back on the next start.
        }
        catch (ObjectDisposedException)
        {
            // Host disposed while waiting.
        }
    }

    private void OnProgress(JobContext context)
    {
        var now = _time.GetTimestamp();
        var last = _lastPublished.GetOrAdd(context.Job.Id, 0);
        if (last != 0 && _time.GetElapsedTime(last, now) < ProgressThrottle)
        {
            return;
        }

        _lastPublished[context.Job.Id] = now;
        JobChanged?.Invoke(this, Snapshot(context.Job));
    }

    private void OnSchedulerChanged(object? sender, SchedulerChange change)
    {
        if (change.Removed)
        {
            _progress.TryRemove(change.JobId, out _);
            _lastPublished.TryRemove(change.JobId, out _);
            _journal.Append(new JournalRecord { Type = JournalRecordType.Removed, JobId = change.JobId, At = _time.GetUtcNow() });
            JobRemoved?.Invoke(this, change.JobId);
            return;
        }

        if (_scheduler.Find(change.JobId) is not { } job)
        {
            return;
        }

        _journal.Append(new JournalRecord { Type = JournalRecordType.StateChanged, JobId = job.Id, At = _time.GetUtcNow(), State = job.State });
        JobChanged?.Invoke(this, Snapshot(job));
    }

    private sealed record RunState(CancellationTokenSource Cts, Task Completion)
    {
        public void Cancel()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }
}
