using Leviathan.Contracts;

namespace Leviathan.Engine.Scheduling;

/// <summary>
/// The job queue and its rules (spec §12.3): a global limit of running operation jobs, a per-device limit, a separate
/// background lane that never takes an operations slot and yields to operations on the same device, priorities,
/// manual ordering, "Run now", and a color per job (spec §12.6). Thread-safe; all state changes raise <see cref="Changed"/>.
/// </summary>
/// <remarks>The scheduler only decides. <see cref="Execution.JobHost"/> starts and stops the actual work.</remarks>
public sealed class JobScheduler
{
    /// <summary>Number of distinct job colors (spec §12.6).</summary>
    public const int PaletteSize = 8;

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly List<ScheduledJob> _jobs = [];
    private readonly int[] _colorUse = new int[PaletteSize];
    private ConcurrencyLimits _limits;
    private long _sequence;
    private int _nextColor;

    /// <summary>Creates a scheduler.</summary>
    public JobScheduler(ConcurrencyLimits? limits = null, TimeProvider? time = null)
    {
        _limits = Validate(limits ?? new ConcurrencyLimits());
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after any change, outside the scheduler's lock.</summary>
    public event EventHandler<SchedulerChange>? Changed;

    /// <summary>Current limits.</summary>
    public ConcurrencyLimits Limits
    {
        get
        {
            lock (_lock)
            {
                return _limits;
            }
        }
    }

    /// <summary>Changes the limits. Running jobs are not stopped; new limits apply to the next start.</summary>
    public void SetLimits(ConcurrencyLimits limits)
    {
        lock (_lock)
        {
            _limits = Validate(limits);
        }
    }

    /// <summary>Queues a job. It is inserted after every queued job of equal or higher priority.</summary>
    public ScheduledJob Enqueue(JobRequest request, JobLane lane, string? deviceKey, string title, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        ScheduledJob job;
        lock (_lock)
        {
            var color = lane == JobLane.Operations ? AllocateColor() : -1;
            job = new ScheduledJob(id ?? Guid.NewGuid(), request, lane, deviceKey, title, color, _sequence++, _time.GetUtcNow());
            _jobs.Insert(InsertionIndex(job.Priority), job);
        }

        Raise(job.Id);
        return job;
    }

    /// <summary>
    /// Picks every job that may start now under the limits, marks them Running and returns them in start order.
    /// Queued jobs that cannot start get a <see cref="ScheduledJob.WaitingReason"/>.
    /// </summary>
    public IReadOnlyList<ScheduledJob> TakeRunnable()
    {
        var started = new List<ScheduledJob>();
        var touched = new List<Guid>();
        lock (_lock)
        {
            foreach (var job in _jobs)
            {
                if (job.State != JobState.Queued)
                {
                    continue;
                }

                var reason = job.Forced ? null : BlockingReason(job);
                if (reason is null)
                {
                    job.State = JobState.Running;
                    job.WaitingReason = null;
                job.RetryAt = null;
                    started.Add(job);
                    touched.Add(job.Id);
                }
                else if (job.WaitingReason != reason)
                {
                    job.WaitingReason = reason;
                    touched.Add(job.Id);
                }
            }
        }

        foreach (var id in touched)
        {
            Raise(id);
        }

        return started;
    }

    /// <summary>Records that a running job finished.</summary>
    public void MarkFinished(Guid jobId, JobState finalState, string? error = null)
    {
        if (finalState is not (JobState.Completed or JobState.Failed or JobState.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(finalState), finalState, "Final state must be Completed, Failed or Cancelled.");
        }

        Update(jobId, job =>
        {
            job.State = finalState;
            job.Error = error;
            job.FinishedAt = _time.GetUtcNow();
            job.WaitingReason = null;
                job.RetryAt = null;
            job.Forced = false;
            ReleaseColor(job.ColorIndex);
            return true;
        });
    }

    /// <summary>Returns a running job to the queue in the Waiting state (share offline; retries automatically).</summary>
    public void MarkWaiting(Guid jobId, string reason, DateTimeOffset? retryAt = null) =>
        Update(jobId, job =>
        {
            if (job.State != JobState.Running)
            {
                return false;
            }

            job.State = JobState.Waiting;
            job.WaitingReason = reason;
            job.RetryAt = retryAt;
            return true;
        });

    /// <summary>Makes a Waiting job eligible to start again.</summary>
    public void Requeue(Guid jobId) =>
        Update(jobId, job =>
        {
            if (job.State != JobState.Waiting)
            {
                return false;
            }

            job.State = JobState.Queued;
            return true;
        });

    /// <summary>Pauses a queued, waiting or running job. Returns the state it had, or null when not found.</summary>
    public JobState? Pause(Guid jobId)
    {
        JobState? previous = null;
        Update(jobId, job =>
        {
            previous = job.State;
            if (job.State is not (JobState.Queued or JobState.Waiting or JobState.Running))
            {
                return false;
            }

            job.State = JobState.Paused;
            job.WaitingReason = null;
                job.RetryAt = null;
            return true;
        });
        return previous;
    }

    /// <summary>Resumes a paused job (it re-enters the queue in its current position).</summary>
    public bool Resume(Guid jobId)
    {
        var resumed = false;
        Update(jobId, job =>
        {
            if (job.State != JobState.Paused)
            {
                return false;
            }

            job.State = JobState.Queued;
            resumed = true;
            return true;
        });
        return resumed;
    }

    /// <summary>Cancels a job. Returns the state it had, or null when not found or already finished.</summary>
    public JobState? Cancel(Guid jobId)
    {
        JobState? previous = null;
        Update(jobId, job =>
        {
            if (job.IsFinished)
            {
                return false;
            }

            previous = job.State;
            if (job.State != JobState.Running)
            {
                // A running job is finished by its executor once it observes cancellation.
                job.State = JobState.Cancelled;
                job.FinishedAt = _time.GetUtcNow();
                job.WaitingReason = null;
                job.RetryAt = null;
                ReleaseColor(job.ColorIndex);
            }

            return true;
        });
        return previous;
    }

    /// <summary>Removes a job that is not running. Returns false when it is running or unknown.</summary>
    public bool Remove(Guid jobId)
    {
        lock (_lock)
        {
            var index = _jobs.FindIndex(j => j.Id == jobId);
            if (index < 0 || _jobs[index].State == JobState.Running)
            {
                return false;
            }

            var job = _jobs[index];
            if (!job.IsFinished)
            {
                ReleaseColor(job.ColorIndex);
            }

            _jobs.RemoveAt(index);
        }

        Changed?.Invoke(this, new SchedulerChange(jobId, Removed: true));
        return true;
    }

    /// <summary>
    /// Moves a queued or paused job to <paramref name="queuePosition"/> among the not-yet-started jobs (0 = next).
    /// Manual order wins over priority until the priority is changed again.
    /// </summary>
    public bool Move(Guid jobId, int queuePosition)
    {
        lock (_lock)
        {
            var job = _jobs.Find(j => j.Id == jobId);
            if (job is null || job.State is not (JobState.Queued or JobState.Paused or JobState.Waiting))
            {
                return false;
            }

            _jobs.Remove(job);
            var pending = _jobs.Where(IsPending).ToList();
            var position = Math.Clamp(queuePosition, 0, pending.Count);
            var insertAt = position == pending.Count
                ? (pending.Count == 0 ? _jobs.Count : _jobs.IndexOf(pending[^1]) + 1)
                : _jobs.IndexOf(pending[position]);
            _jobs.Insert(insertAt, job);
        }

        Raise(jobId);
        return true;
    }

    /// <summary>Changes a job's priority and re-positions it in the queue.</summary>
    public bool SetPriority(Guid jobId, JobPriority priority)
    {
        lock (_lock)
        {
            var job = _jobs.Find(j => j.Id == jobId);
            if (job is null || job.IsFinished)
            {
                return false;
            }

            job.Priority = priority;
            if (IsPending(job))
            {
                _jobs.Remove(job);
                _jobs.Insert(InsertionIndex(priority), job);
            }
        }

        Raise(jobId);
        return true;
    }

    /// <summary>Marks a queued or paused job to start immediately, regardless of limits.</summary>
    public bool RunNow(Guid jobId)
    {
        var changed = false;
        Update(jobId, job =>
        {
            if (job.State is not (JobState.Queued or JobState.Paused or JobState.Waiting))
            {
                return false;
            }

            job.Forced = true;
            job.State = JobState.Queued;
            changed = true;
            return true;
        });
        return changed;
    }

    /// <summary>Finds a job.</summary>
    public ScheduledJob? Find(Guid jobId)
    {
        lock (_lock)
        {
            return _jobs.Find(j => j.Id == jobId);
        }
    }

    /// <summary>All jobs in display order (running and queued first, in queue order, then finished).</summary>
    public IReadOnlyList<ScheduledJob> Snapshot()
    {
        lock (_lock)
        {
            return [.. _jobs];
        }
    }

    /// <summary>Position of a job among not-yet-started jobs, or -1.</summary>
    public int QueuePosition(Guid jobId)
    {
        lock (_lock)
        {
            var index = 0;
            foreach (var job in _jobs)
            {
                if (!IsPending(job))
                {
                    continue;
                }

                if (job.Id == jobId)
                {
                    return index;
                }

                index++;
            }

            return -1;
        }
    }

    /// <summary>Removes finished jobs older than <paramref name="age"/>; returns how many were removed.</summary>
    public int PruneFinished(TimeSpan age)
    {
        List<Guid> removed;
        lock (_lock)
        {
            var cutoff = _time.GetUtcNow() - age;
            removed = [.. _jobs.Where(j => j.IsFinished && j.FinishedAt < cutoff).Select(j => j.Id)];
            _jobs.RemoveAll(j => removed.Contains(j.Id));
        }

        foreach (var id in removed)
        {
            Changed?.Invoke(this, new SchedulerChange(id, Removed: true));
        }

        return removed.Count;
    }

    private static bool IsPending(ScheduledJob job) => job.State is JobState.Queued or JobState.Paused or JobState.Waiting;

    private string? BlockingReason(ScheduledJob job)
    {
        var running = _jobs.Where(j => j.State == JobState.Running).ToList();
        if (job.Lane == JobLane.Background)
        {
            if (running.Count(j => j.Lane == JobLane.Background) >= _limits.MaxBackgroundJobs)
            {
                return "Waiting: background lane busy";
            }

            if (job.DeviceKey is not null && running.Any(j => j.Lane == JobLane.Operations && SameDevice(j.DeviceKey, job.DeviceKey)))
            {
                return "Waiting: a job is using this device";
            }

            return null;
        }

        if (running.Count(j => j.Lane == JobLane.Operations && !j.Forced) >= _limits.MaxRunningJobs)
        {
            return $"Waiting: {_limits.MaxRunningJobs} jobs running";
        }

        if (job.DeviceKey is not null)
        {
            var limit = _limits.LimitFor(job.DeviceKey);
            var onDevice = running.Count(j => j.Lane == JobLane.Operations && SameDevice(j.DeviceKey, job.DeviceKey));
            if (onDevice >= limit)
            {
                return limit == 1 ? $"Waiting: 1 job per device ({job.DeviceKey})" : $"Waiting: {limit} jobs per device ({job.DeviceKey})";
            }
        }

        return null;
    }

    private static bool SameDevice(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private int InsertionIndex(JobPriority priority)
    {
        // After the last pending job with priority >= this one; finished jobs stay at the end.
        var index = 0;
        for (var i = 0; i < _jobs.Count; i++)
        {
            var existing = _jobs[i];
            if (existing.State == JobState.Running || (IsPending(existing) && existing.Priority >= priority))
            {
                index = i + 1;
            }
        }

        return index;
    }

    private int AllocateColor()
    {
        // Next unused color in palette order; when all 8 are in use, colors repeat in turn.
        for (var offset = 0; offset < PaletteSize; offset++)
        {
            var candidate = (_nextColor + offset) % PaletteSize;
            if (_colorUse[candidate] == 0)
            {
                _colorUse[candidate]++;
                _nextColor = (candidate + 1) % PaletteSize;
                return candidate;
            }
        }

        var reused = _nextColor;
        _colorUse[reused]++;
        _nextColor = (reused + 1) % PaletteSize;
        return reused;
    }

    private void ReleaseColor(int color)
    {
        if (color >= 0 && _colorUse[color] > 0)
        {
            _colorUse[color]--;
        }
    }

    private void Update(Guid jobId, Func<ScheduledJob, bool> change)
    {
        bool changed;
        lock (_lock)
        {
            var job = _jobs.Find(j => j.Id == jobId);
            changed = job is not null && change(job);
        }

        if (changed)
        {
            Raise(jobId);
        }
    }

    private void Raise(Guid jobId) => Changed?.Invoke(this, new SchedulerChange(jobId, Removed: false));

    private static ConcurrencyLimits Validate(ConcurrencyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxRunningJobs, 1, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limits.MaxRunningJobs, 16, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxPerDevice, 1, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxBackgroundJobs, 1, nameof(limits));
        return limits;
    }
}
