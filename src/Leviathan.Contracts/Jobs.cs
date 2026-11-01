namespace Leviathan.Contracts;

/// <summary>Kinds of job the worker runs (spec §12.3).</summary>
public enum JobKind
{
    /// <summary>Copy items to a destination.</summary>
    Copy,

    /// <summary>Move items to a destination.</summary>
    Move,

    /// <summary>Delete items (soft delete on network shares).</summary>
    Delete,

    /// <summary>Purge soft-deleted items.</summary>
    Purge,

    /// <summary>Extract an archive.</summary>
    Extract,

    /// <summary>Create an archive.</summary>
    Compress,

    /// <summary>Synchronise two folders.</summary>
    Sync,

    /// <summary>Rename many items.</summary>
    BulkRename,

    /// <summary>Compute folder sizes (background lane).</summary>
    ComputeFolderSize,
}

/// <summary>Job lifecycle (spec §12.3).</summary>
public enum JobState
{
    /// <summary>Waiting for a slot.</summary>
    Queued,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Paused by the user.</summary>
    Paused,

    /// <summary>Waiting for a share to come back; retries automatically.</summary>
    Waiting,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Finished with some items failed.</summary>
    Failed,

    /// <summary>Cancelled by the user.</summary>
    Cancelled,
}

/// <summary>Job priority. "Run now" bypasses the limits entirely.</summary>
public enum JobPriority
{
    /// <summary>Low.</summary>
    Low,

    /// <summary>Normal.</summary>
    Normal,

    /// <summary>High.</summary>
    High,
}

/// <summary>Which lane a job runs in. Background work never takes a slot from user operations (spec §12.3).</summary>
public enum JobLane
{
    /// <summary>User-initiated operations.</summary>
    Operations,

    /// <summary>Folder sizes, metadata, thumbnails, indexing.</summary>
    Background,
}

/// <summary>A request to run a job.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Sources">Source locations, as full location strings.</param>
/// <param name="Destination">Destination location for copy/move/extract/sync; otherwise null.</param>
public sealed record JobRequest(JobKind Kind, IReadOnlyList<string> Sources, string? Destination)
{
    /// <summary>Priority within the queue.</summary>
    public JobPriority Priority { get; init; } = JobPriority.Normal;

    /// <summary>Queue the job paused, to release a staged batch together.</summary>
    public bool StartPaused { get; init; }

    /// <summary>Optional title; the worker composes one when null.</summary>
    public string? Title { get; init; }
}

/// <summary>A point-in-time view of a job, as shown in the Operations panel.</summary>
public sealed record JobSnapshot
{
    /// <summary>Job ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Kind.</summary>
    public required JobKind Kind { get; init; }

    /// <summary>Lane.</summary>
    public required JobLane Lane { get; init; }

    /// <summary>Title such as "Move 312 items to \\nas\media\keep".</summary>
    public required string Title { get; init; }

    /// <summary>State.</summary>
    public required JobState State { get; init; }

    /// <summary>Priority.</summary>
    public required JobPriority Priority { get; init; }

    /// <summary>Position in the queue (0 = next to run); -1 when not queued.</summary>
    public int QueuePosition { get; init; } = -1;

    /// <summary>Palette index of the job's color (0–7); -1 for background jobs, which share gray (spec §12.6).</summary>
    public int ColorIndex { get; init; } = -1;

    /// <summary>Device the job is limited by (e.g. "\\nas" or "C:"); null when none.</summary>
    public string? DeviceKey { get; init; }

    /// <summary>Bytes completed.</summary>
    public long BytesDone { get; init; }

    /// <summary>Total bytes, or -1 while still enumerating.</summary>
    public long BytesTotal { get; init; } = -1;

    /// <summary>Items completed.</summary>
    public int ItemsDone { get; init; }

    /// <summary>Total items, or -1 while still enumerating.</summary>
    public int ItemsTotal { get; init; } = -1;

    /// <summary>Current speed in bytes per second.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Speed samples, oldest first, one per second for up to 2 minutes (the sparkline, spec §12.6).</summary>
    public IReadOnlyList<double> SpeedHistory { get; init; } = [];

    /// <summary>Estimated time remaining, when known.</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>Why a queued or waiting job is not running, e.g. "Waiting: 1 job per NAS volume".</summary>
    public string? WaitingReason { get; init; }

    /// <summary>Current item, e.g. the file being copied.</summary>
    public string? CurrentItem { get; init; }

    /// <summary>Error summary for failed jobs.</summary>
    public string? Error { get; init; }

    /// <summary>When the job was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Concurrency limits for the scheduler (spec §12.3).</summary>
/// <param name="MaxRunningJobs">Global limit of running operation jobs (1–16).</param>
/// <param name="MaxPerDevice">Default limit of running jobs per device.</param>
/// <param name="MaxBackgroundJobs">Thread budget of the background lane.</param>
public sealed record ConcurrencyLimits(int MaxRunningJobs = 3, int MaxPerDevice = 1, int MaxBackgroundJobs = 2)
{
    /// <summary>Per-device overrides keyed by device key (e.g. "C:" = 2 for an SSD).</summary>
    public IReadOnlyDictionary<string, int> DeviceOverrides { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The limit that applies to <paramref name="deviceKey"/>.</summary>
    public int LimitFor(string deviceKey) => DeviceOverrides.TryGetValue(deviceKey, out var limit) ? limit : MaxPerDevice;
}
