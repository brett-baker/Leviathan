namespace Leviathan.Contracts;

/// <summary>Version of the IPC contracts. Bump on any breaking change; client and worker refuse to talk across majors.</summary>
public static class ContractVersion
{
    /// <summary>Major version. Must match between client and worker.</summary>
    public const int Major = 2;

    /// <summary>Minor version. Additive changes only.</summary>
    public const int Minor = 17;
}

/// <summary>Information the worker reports about itself.</summary>
/// <param name="ContractMajor">Contract major version the worker speaks.</param>
/// <param name="ContractMinor">Contract minor version.</param>
/// <param name="AppVersion">Informational version including the commit SHA.</param>
/// <param name="ProcessId">Worker process ID.</param>
/// <param name="StartedAt">When the worker started.</param>
public sealed record WorkerInfo(int ContractMajor, int ContractMinor, string AppVersion, int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// The file-operations worker (Leviathan.Worker), called by the UI over a named pipe. Every method is safe to call
/// from several windows at once.
/// </summary>
public interface IWorkerService
{
    /// <summary>Returns worker and contract versions; also used as a heartbeat.</summary>
    Task<WorkerInfo> PingAsync(CancellationToken cancellationToken);

    /// <summary>Queues a job and returns its first snapshot.</summary>
    Task<JobSnapshot> SubmitAsync(JobRequest request, CancellationToken cancellationToken);

    /// <summary>All jobs: running, queued, and finished in the last 7 days.</summary>
    Task<IReadOnlyList<JobSnapshot>> GetJobsAsync(CancellationToken cancellationToken);

    /// <summary>What happened to each item of a job so far, or null when the job is unknown (undo is built from this).</summary>
    Task<JobResult?> GetResultAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The report of a finished job (kept for 7 days), or null when there is none.</summary>
    Task<JobReport?> GetReportAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// The Soft-delete bin: everything soft-deleted across the shares Leviathan has deleted from (spec §12.5), read from the trash
    /// folders' manifests. <paramref name="measureFolders"/> also adds up folder sizes, which can be slow on a share.
    /// </summary>
    Task<BinListing> GetBinAsync(bool measureFolders, CancellationToken cancellationToken);

    /// <summary>Pauses a queued or running job.</summary>
    Task PauseAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Resumes a paused job.</summary>
    Task ResumeAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Cancels a job.</summary>
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Removes a queued, paused or finished job from the list.</summary>
    Task RemoveAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Moves a queued job to <paramref name="queuePosition"/> (0 = next).</summary>
    Task MoveAsync(Guid jobId, int queuePosition, CancellationToken cancellationToken);

    /// <summary>Changes a job's priority.</summary>
    Task SetPriorityAsync(Guid jobId, JobPriority priority, CancellationToken cancellationToken);

    /// <summary>Starts a queued job immediately, above the limits.</summary>
    Task RunNowAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Answers the question a job is waiting on (<see cref="JobSnapshot.Prompt"/>). An answer to a question that is gone is ignored.</summary>
    Task AnswerPromptAsync(Guid jobId, Guid promptId, ErrorDecision decision, CancellationToken cancellationToken);

    /// <summary>Answers the name clashes a job is waiting on (<see cref="JobSnapshot.Conflicts"/>). An answer to a question that is gone is ignored.</summary>
    Task AnswerConflictsAsync(Guid jobId, Guid promptId, ConflictAnswer answer, CancellationToken cancellationToken);

    /// <summary>Pauses every job.</summary>
    Task PauseAllAsync(CancellationToken cancellationToken);

    /// <summary>Resumes every paused job.</summary>
    Task ResumeAllAsync(CancellationToken cancellationToken);

    /// <summary>Current concurrency limits.</summary>
    Task<ConcurrencyLimits> GetLimitsAsync(CancellationToken cancellationToken);

    /// <summary>Changes the concurrency limits; takes effect immediately without restarting jobs.</summary>
    Task SetLimitsAsync(ConcurrencyLimits limits, CancellationToken cancellationToken);

    /// <summary>Current bandwidth limits and schedules.</summary>
    Task<BandwidthSettings> GetBandwidthAsync(CancellationToken cancellationToken);

    /// <summary>Changes the bandwidth limits; running copies slow down or speed up at once.</summary>
    Task SetBandwidthAsync(BandwidthSettings settings, CancellationToken cancellationToken);
}

/// <summary>Notifications the worker pushes to connected UI processes.</summary>
public interface IWorkerCallbacks
{
    /// <summary>A job changed (progress, state, position). Sent at most a few times per second per job.</summary>
    Task OnJobChangedAsync(JobSnapshot snapshot);

    /// <summary>A job was removed from the list.</summary>
    Task OnJobRemovedAsync(Guid jobId);
}

/// <summary>Names shared by both ends of the pipe.</summary>
public static class PipeNames
{
    /// <summary>
    /// Prefix of the worker pipe. The full name adds a hash of the user's SID so each Windows user gets their own worker;
    /// the pipe is also created with current-user-only access.
    /// </summary>
    public const string WorkerPrefix = "Leviathan.Worker.v2.";

    /// <summary>Prefix of the shell host pipe, built the same way as <see cref="WorkerPrefix"/>.</summary>
    public const string ShellHostPrefix = "Leviathan.ShellHost.v2.";

    /// <summary>Prefix of the media process pipe, built the same way as <see cref="WorkerPrefix"/>.</summary>
    public const string MediaPrefix = "Leviathan.Media.v2.";
}
