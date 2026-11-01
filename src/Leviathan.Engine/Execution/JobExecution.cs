using Leviathan.Contracts;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Execution;

/// <summary>Runs one kind of job. Executors are stateless; per-job state lives in <see cref="JobContext"/>.</summary>
public interface IJobExecutor
{
    /// <summary>True when this executor runs jobs of <paramref name="kind"/>.</summary>
    bool CanExecute(JobKind kind);

    /// <summary>
    /// Runs the job. Must observe <paramref name="cancellationToken"/> (pause and cancel both use it) and report progress
    /// through <paramref name="context"/>. Transient network failures should throw <see cref="Core.Providers.LocationException"/>
    /// with <c>IsTransient</c> set, so the host can move the job to Waiting and retry.
    /// </summary>
    Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>How a job ended.</summary>
/// <param name="State">Completed or Failed.</param>
/// <param name="Error">Error summary when failed.</param>
public sealed record JobOutcome(JobState State, string? Error = null)
{
    /// <summary>A successful outcome.</summary>
    public static JobOutcome Completed { get; } = new(JobState.Completed);
}

/// <summary>Per-run state handed to an executor.</summary>
public sealed class JobContext
{
    private readonly Lock _lock = new();
    private readonly Action<JobContext> _onProgress;

    internal JobContext(ScheduledJob job, SpeedMeter speed, Action<JobContext> onProgress)
    {
        Job = job;
        Speed = speed;
        _onProgress = onProgress;
    }

    /// <summary>The job being run.</summary>
    public ScheduledJob Job { get; }

    /// <summary>Speed measurement for this job.</summary>
    public SpeedMeter Speed { get; }

    /// <summary>Bytes completed.</summary>
    public long BytesDone { get; private set; }

    /// <summary>Total bytes, or -1 while unknown.</summary>
    public long BytesTotal { get; private set; } = -1;

    /// <summary>Items completed.</summary>
    public int ItemsDone { get; private set; }

    /// <summary>Total items, or -1 while unknown.</summary>
    public int ItemsTotal { get; private set; } = -1;

    /// <summary>Item currently being processed.</summary>
    public string? CurrentItem { get; private set; }

    /// <summary>Reports progress. Cheap; the host throttles what it forwards to the UI.</summary>
    public void Report(long bytesDone, long bytesTotal, int itemsDone, int itemsTotal, string? currentItem = null)
    {
        lock (_lock)
        {
            BytesDone = bytesDone;
            BytesTotal = bytesTotal;
            ItemsDone = itemsDone;
            ItemsTotal = itemsTotal;
            CurrentItem = currentItem;
        }

        Speed.Record(bytesDone);
        _onProgress(this);
    }
}
