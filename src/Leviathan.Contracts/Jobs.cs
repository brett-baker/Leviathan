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

    /// <summary>Rename one item in place (<see cref="JobRequest.NewName"/>). Made a job so it is journaled and can be undone (spec §12.5).</summary>
    Rename,

    /// <summary>Create shortcuts to the sources in the destination folder (drag with Alt, spec §8.4).</summary>
    Link,

    /// <summary>
    /// Move each source back to the matching entry of <see cref="JobRequest.Targets"/> without overwriting anything. Undo and redo of
    /// a rename or a delete are this job (spec §12.5); it also tidies the Recycle Bin's bookkeeping when the source was in the bin.
    /// </summary>
    Restore,
}

/// <summary>What to do when an item with the same name is already in the destination (spec §12.2). Asking is a later slice; until then the choice is made up front.</summary>
public enum ConflictPolicy
{
    /// <summary>Keep both: the new item gets a free name such as "report (2).pdf". Never overwrites, so it is the default.</summary>
    KeepBoth,

    /// <summary>Replace the existing item.</summary>
    Replace,

    /// <summary>Leave the existing item and skip the new one.</summary>
    Skip,

    /// <summary>Ask once for the whole job (spec §12.4): the job lists every clash and waits for a <see cref="ConflictAnswer"/>. Not a valid answer itself.</summary>
    Ask,

    /// <summary>Replace the existing file only when the new one was modified later; otherwise leave it.</summary>
    ReplaceIfNewer,

    /// <summary>Replace the existing file only when the new one is larger; otherwise leave it.</summary>
    ReplaceIfLarger,

    /// <summary>Replace the existing file only when the contents differ (size, then checksum); identical files are left alone.</summary>
    ReplaceIfDifferent,
}

/// <summary>One name clash a job asks about (spec §12.4): a file that would be written over an existing one.</summary>
/// <param name="Source">The file being copied or moved.</param>
/// <param name="Target">The existing file at the destination; answers are keyed by it.</param>
/// <param name="SourceSize">Size of the new file.</param>
/// <param name="SourceModified">When the new file was last modified.</param>
/// <param name="TargetSize">Size of the existing file.</param>
/// <param name="TargetModified">When the existing file was last modified.</param>
public sealed record ConflictItem(string Source, string Target, long SourceSize, DateTimeOffset SourceModified, long TargetSize, DateTimeOffset TargetModified);

/// <summary>The clashes a running job waits on (spec §12.4). One question for the whole job, not one per file.</summary>
/// <param name="Id">Identifies the question; the answer must quote it.</param>
/// <param name="Items">The clashes, up to a limit; the rest follow the default answer.</param>
/// <param name="TotalCount">How many clashes there are in all.</param>
public sealed record JobConflicts(Guid Id, IReadOnlyList<ConflictItem> Items, int TotalCount);

/// <summary>The user's answer to <see cref="JobConflicts"/>: one rule for all clashes, with per-file overrides.</summary>
/// <param name="ForAll">The rule for every clash not listed in <paramref name="Overrides"/>; never <see cref="ConflictPolicy.Ask"/>.</param>
/// <param name="Overrides">Rules for single files, keyed by <see cref="ConflictItem.Target"/>.</param>
public sealed record ConflictAnswer(ConflictPolicy ForAll, IReadOnlyDictionary<string, ConflictPolicy>? Overrides = null);

/// <summary>What to do when an item fails for a reason that retrying later will not fix (access denied, disk full, invalid name; spec §12.2).</summary>
public enum ErrorMode
{
    /// <summary>Ask once, with Skip / Retry / Skip all like this.</summary>
    Ask,

    /// <summary>Skip the item, carry on, and list it in the job's report.</summary>
    SkipAndReport,
}

/// <summary>The user's answer to a <see cref="JobPrompt"/>.</summary>
public enum ErrorDecision
{
    /// <summary>Leave this item undone and continue with the next.</summary>
    Skip,

    /// <summary>Try the same step again, for example after making room or fixing permissions.</summary>
    Retry,

    /// <summary>Skip this item and every later one that fails for the same reason without asking again.</summary>
    SkipAllLikeThis,
}

/// <summary>Why an item could not be done, in a form the prompt and "skip all like this" can use.</summary>
public enum ErrorKind
{
    /// <summary>Anything else.</summary>
    Other,

    /// <summary>Access was denied.</summary>
    AccessDenied,

    /// <summary>The destination is full.</summary>
    DiskFull,

    /// <summary>The destination file system does not accept the name.</summary>
    InvalidName,

    /// <summary>The copy did not match the original.</summary>
    VerifyFailed,
}

/// <summary>A question a running job is waiting on (spec §12.2): "Skip / Retry / Skip all like this".</summary>
/// <param name="Id">Identifies the question; the answer must quote it.</param>
/// <param name="Kind">The reason, used by "skip all like this".</param>
/// <param name="Item">The file or folder involved.</param>
/// <param name="Message">What went wrong, in words.</param>
public sealed record JobPrompt(Guid Id, ErrorKind Kind, string Item, string Message);

/// <summary>How a copy is checked against its source (spec §12.1 step 6, §3.5 item 4).</summary>
public enum VerifyMode
{
    /// <summary>The default policy: every move that deletes its source is checked, and so is any copy to or from a network location; local copies are not. Files up to 4 GB are hashed in full, larger ones are sampled.</summary>
    Default,

    /// <summary>Hash every file in full, whatever its size. Slower; for when a mistake would be expensive.</summary>
    Full,

    /// <summary>Do not check copies. A move still follows the default policy, because the source is deleted afterwards and must never be lost.</summary>
    Off,
}

/// <summary>Where deleted items go (spec §12.5).</summary>
public enum DeleteMode
{
    /// <summary>The Recycle Bin on local disks, the share's <c>#recycle</c> folder on shares that have one. Undoable.</summary>
    RecycleBin,

    /// <summary>Gone for good. Only after the user confirmed.</summary>
    Permanent,
}

/// <summary>What happened to one item of a job.</summary>
public enum ItemStatus
{
    /// <summary>Done.</summary>
    Done,

    /// <summary>Left alone, for example because of <see cref="ConflictPolicy.Skip"/>.</summary>
    Skipped,

    /// <summary>Could not be done; see <see cref="ItemOutcome.Error"/>.</summary>
    Failed,
}

/// <summary>The result for one source of a job. Undo (Ctrl+Z) is built from these (spec §12.5).</summary>
/// <param name="Source">The item as it was before the job.</param>
/// <param name="Result">Where it is now (the copy, the new name, the Recycle Bin entry), or null when it is gone or nothing was made.</param>
/// <param name="Status">Done, skipped or failed.</param>
/// <param name="Error">Why it failed.</param>
public sealed record ItemOutcome(string Source, string? Result, ItemStatus Status, string? Error = null);

/// <summary>
/// What a finished job did, kept for seven days (spec §12.3 "Reports"): counts, every item with its outcome and reason, and notes such
/// as names that were changed to be valid on the destination.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="Title">The job's title, such as "Move 312 items to \nas\media\keep".</param>
/// <param name="Kind">What kind of job it was.</param>
/// <param name="State">How it ended: Completed, Failed or Cancelled.</param>
/// <param name="FinishedAt">When it ended.</param>
/// <param name="BytesDone">Bytes moved.</param>
/// <param name="Done">Items done.</param>
/// <param name="Skipped">Items skipped.</param>
/// <param name="Failed">Items that failed.</param>
/// <param name="Items">One entry per item handled.</param>
/// <param name="Notes">Things worth knowing that are not failures.</param>
/// <param name="Error">The job's error summary, when it failed as a whole.</param>
/// <param name="Destination">Where the job put things (copy and move), so "Retry failed" can run the same job again for the items that failed; null for other kinds.</param>
public sealed record JobReport(
    Guid JobId, string Title, JobKind Kind, JobState State, DateTimeOffset FinishedAt, long BytesDone,
    int Done, int Skipped, int Failed, IReadOnlyList<ItemOutcome> Items, IReadOnlyList<string> Notes, string? Error, string? Destination = null);

/// <summary>The per-item results of a finished (or running) job.</summary>
/// <param name="JobId">The job.</param>
/// <param name="Items">One entry per source handled so far.</param>
public sealed record JobResult(Guid JobId, IReadOnlyList<ItemOutcome> Items);

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

/// <summary>When the automatic purge removes soft-deleted items (spec §12.5). A limit of zero or less is off.</summary>
/// <param name="MaxAgeDays">Items deleted this many days ago or longer are purged.</param>
/// <param name="MaxBytes">The oldest items go until the bin is under this size.</param>
public sealed record PurgePolicy(int MaxAgeDays, long MaxBytes);

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

    /// <summary>What to do with a name that is already taken (copy, move, link).</summary>
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.KeepBoth;

    /// <summary>A speed limit for this job alone, in bytes per second; 0 = none. Combined with the global and per-device limits, the slowest wins.</summary>
    public long BytesPerSecondLimit { get; init; }

    /// <summary>What to do when an item fails for a reason retrying later will not fix (copy and move).</summary>
    public ErrorMode OnError { get; init; } = ErrorMode.Ask;

    /// <summary>How hard to check copies against their sources (copy and move, spec §12.1 step 6).</summary>
    public VerifyMode Verify { get; init; } = VerifyMode.Default;

    /// <summary>Where deleted items go (delete only).</summary>
    public DeleteMode DeleteMode { get; init; } = DeleteMode.RecycleBin;

    /// <summary>
    /// Delete on a network share: when true (the default) an item with no visible NAS recycle folder goes into Leviathan's hidden
    /// <c>.leviathan-trash</c>. When false it is deleted on the share itself, for the NAS's own bin to catch, and cannot be undone.
    /// </summary>
    public bool KeepOwnTrash { get; init; } = true;

    /// <summary>
    /// Purge only: when set, the job ignores <see cref="Sources"/> and empties the Soft-delete bin by this rule (the automatic purge,
    /// spec §12.5). When null the job purges exactly the soft-deleted items in <see cref="Sources"/>.
    /// </summary>
    public PurgePolicy? PurgePolicy { get; init; }

    /// <summary>The new name (rename only): a name, not a path.</summary>
    public string? NewName { get; init; }

    /// <summary>Where each source goes, one per source in the same order (restore only).</summary>
    public IReadOnlyList<string>? Targets { get; init; }
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

    /// <summary>When a Waiting job tries again (the countdown of "Waiting for \nas, retrying in 8 s", spec §12.2); null otherwise.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>A question the job is waiting for the user to answer (spec §12.2), or null.</summary>
    public JobPrompt? Prompt { get; init; }

    /// <summary>The name clashes the job is waiting for an answer on (spec §12.4), or null.</summary>
    public JobConflicts? Conflicts { get; init; }

    /// <summary>Current item, e.g. the file being copied.</summary>
    public string? CurrentItem { get; init; }

    /// <summary>Error summary for failed jobs.</summary>
    public string? Error { get; init; }

    /// <summary>When the job was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A time of day (and optionally certain days) when a different speed limit applies, such as "unlimited at night" (spec §12.3).</summary>
/// <param name="From">Start, as a time of day in local time.</param>
/// <param name="To">End (exclusive). Earlier than <paramref name="From"/> means the window runs past midnight.</param>
/// <param name="BytesPerSecond">The limit while the window is active; 0 = unlimited.</param>
public sealed record BandwidthWindow(TimeSpan From, TimeSpan To, long BytesPerSecond)
{
    /// <summary>Days the window starts on; null or empty = every day.</summary>
    public IReadOnlyList<DayOfWeek>? Days { get; init; }

    /// <summary>True when the window is active at <paramref name="localNow"/>.</summary>
    public bool IsActiveAt(DateTime localNow)
    {
        var time = localNow.TimeOfDay;
        if (From == To)
        {
            return false;
        }

        // A window past midnight belongs to the day it started on, so after midnight look at yesterday.
        var wraps = From > To;
        var startedToday = wraps ? time >= From : time >= From && time < To;
        var startedYesterday = wraps && time < To;
        if (!startedToday && !startedYesterday)
        {
            return false;
        }

        var day = startedYesterday ? localNow.AddDays(-1).DayOfWeek : localNow.DayOfWeek;
        return Days is not { Count: > 0 } || Days.Contains(day);
    }
}

/// <summary>A speed limit with an optional schedule: the first active window wins, otherwise the default applies.</summary>
/// <param name="BytesPerSecond">The default limit; 0 = unlimited.</param>
/// <param name="Windows">Scheduled overrides.</param>
public sealed record BandwidthRule(long BytesPerSecond = 0, IReadOnlyList<BandwidthWindow>? Windows = null)
{
    /// <summary>The limit that applies at <paramref name="localNow"/>; 0 = unlimited.</summary>
    public long LimitAt(DateTime localNow) => Windows?.FirstOrDefault(w => w.IsActiveAt(localNow))?.BytesPerSecond ?? BytesPerSecond;
}

/// <summary>
/// Bandwidth limits (spec §12.3): one for everything the worker moves, and optionally one per destination device such as the NAS.
/// A job can add its own limit. The slowest limit that applies wins.
/// </summary>
public sealed record BandwidthSettings
{
    /// <summary>The limit for all transfers together.</summary>
    public BandwidthRule Global { get; init; } = new();

    /// <summary>Limits per device key (for example <c>\nas</c>), shared by every job touching that device.</summary>
    public IReadOnlyDictionary<string, BandwidthRule> PerDevice { get; init; } = new Dictionary<string, BandwidthRule>(StringComparer.OrdinalIgnoreCase);
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
