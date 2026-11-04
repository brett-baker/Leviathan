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
    private readonly List<ItemOutcome> _results = [];
    private readonly List<string> _notes = [];
    private readonly Action<JobContext> _onProgress;
    private readonly Action<JobContext> _onPrompt;
    private TaskCompletionSource<ErrorDecision>? _answer;

    internal JobContext(ScheduledJob job, SpeedMeter speed, Action<JobContext> onProgress, Action<JobContext>? onPrompt = null)
    {
        Job = job;
        Speed = speed;
        _onProgress = onProgress;
        _onPrompt = onPrompt ?? onProgress;
    }

    /// <summary>The question the job is waiting on, or null.</summary>
    public JobPrompt? PendingPrompt { get; private set; }

    /// <summary>
    /// Asks the user what to do about an item that failed for a permanent reason (spec §12.2) and waits for the answer. The question is
    /// published at once (not throttled like progress) and shows in the job's snapshot until it is answered or the job is stopped.
    /// </summary>
    public async Task<ErrorDecision> AskAsync(ErrorKind kind, string item, string message, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<ErrorDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _answer = answer;
            PendingPrompt = new JobPrompt(Guid.NewGuid(), kind, item, message);
        }

        _onPrompt(this);
        using var registration = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
        try
        {
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _answer = null;
                PendingPrompt = null;
            }

            _onPrompt(this);
        }
    }

    /// <summary>Delivers the user's answer; false when that question is no longer pending.</summary>
    public bool Answer(Guid promptId, ErrorDecision decision)
    {
        TaskCompletionSource<ErrorDecision>? answer;
        lock (_lock)
        {
            if (PendingPrompt?.Id != promptId)
            {
                return false;
            }

            answer = _answer;
        }

        return answer?.TrySetResult(decision) == true;
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

    /// <summary>The per-item results recorded so far.</summary>
    public IReadOnlyList<ItemOutcome> Results
    {
        get
        {
            lock (_lock)
            {
                return [.. _results];
            }
        }
    }

    /// <summary>Things worth knowing that are not failures (names changed for the destination, streams a share could not take); they go into the job's report.</summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            lock (_lock)
            {
                return [.. _notes];
            }
        }
    }

    /// <summary>Adds a note to the report; the same note twice is kept once, so a resumed job does not repeat itself.</summary>
    public void AddNote(string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        lock (_lock)
        {
            if (!_notes.Contains(note))
            {
                _notes.Add(note);
            }
        }
    }

    /// <summary>Records what happened to one source. Undo and the job report are built from these (spec §12.5).</summary>
    public void AddResult(ItemOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_lock)
        {
            _results.Add(outcome);
        }
    }

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
