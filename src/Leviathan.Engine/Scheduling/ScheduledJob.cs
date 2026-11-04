using Leviathan.Contracts;

namespace Leviathan.Engine.Scheduling;

/// <summary>A job as the scheduler tracks it. Mutated only by <see cref="JobScheduler"/> under its lock.</summary>
public sealed class ScheduledJob
{
    internal ScheduledJob(Guid id, JobRequest request, JobLane lane, string? deviceKey, string title, int colorIndex, long sequence, DateTimeOffset createdAt)
    {
        Id = id;
        Request = request;
        Lane = lane;
        DeviceKey = deviceKey;
        Title = title;
        ColorIndex = colorIndex;
        Sequence = sequence;
        CreatedAt = createdAt;
        Priority = request.Priority;
        State = request.StartPaused ? JobState.Paused : JobState.Queued;
    }

    /// <summary>Job ID.</summary>
    public Guid Id { get; }

    /// <summary>The original request.</summary>
    public JobRequest Request { get; }

    /// <summary>Lane.</summary>
    public JobLane Lane { get; }

    /// <summary>Device the job is limited by; null when none.</summary>
    public string? DeviceKey { get; }

    /// <summary>Display title.</summary>
    public string Title { get; }

    /// <summary>Palette index (0–7) or -1 for background jobs.</summary>
    public int ColorIndex { get; }

    /// <summary>Enqueue order, used for stable ordering.</summary>
    public long Sequence { get; }

    /// <summary>When the job was queued.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Current state.</summary>
    public JobState State { get; internal set; }

    /// <summary>Current priority.</summary>
    public JobPriority Priority { get; internal set; }

    /// <summary>True after "Run now": starts regardless of limits.</summary>
    public bool Forced { get; internal set; }

    /// <summary>Why the job is not running, when queued or waiting.</summary>
    public string? WaitingReason { get; internal set; }

    /// <summary>When a Waiting job will try again, so the UI can count down.</summary>
    public DateTimeOffset? RetryAt { get; internal set; }

    /// <summary>Error summary for failed jobs.</summary>
    public string? Error { get; internal set; }

    /// <summary>When the job finished, if it has.</summary>
    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>True when the job has reached a final state.</summary>
    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
}

/// <summary>What changed in the scheduler.</summary>
/// <param name="JobId">The job.</param>
/// <param name="Removed">True when the job was removed from the list.</param>
public sealed record SchedulerChange(Guid JobId, bool Removed);
