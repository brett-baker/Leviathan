using System.Collections.Concurrent;
using Leviathan.Contracts;

namespace Leviathan.ViewModels.Elevation;

/// <summary>How an elevated run ended, in words for the person (spec §15.6).</summary>
/// <param name="Started">False when the helper never ran: the prompt was declined, or the helper could not be started.</param>
/// <param name="Succeeded">True when it ran and every item was done.</param>
/// <param name="Message">One line to show.</param>
public sealed record ElevatedOutcome(bool Started, bool Succeeded, string Message);

/// <summary>Runs one request in the elevated helper after the person agreed in the Windows prompt. The app implements it over a process and a pipe; tests with a fake.</summary>
public interface IElevatedRunner
{
    /// <summary>Starts the helper for <paramref name="request"/> and waits for its answer. Never throws for a declined prompt.</summary>
    Task<ElevatedOutcome> RunAsync(JobRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Remembers what each queued copy, move and delete was asked to do, so that when one stops on "access denied" the one item that was refused
/// can be done again as administrator, and only that item (spec §15.6). The worker's question names the item but not the job's destination.
/// </summary>
public sealed class ElevationMemory
{
    private const int Limit = 200;

    private readonly ConcurrentDictionary<Guid, Remembered> _jobs = new();

    private sealed record Remembered(JobKind Kind, IReadOnlyList<string> Sources, string? Destination, bool Permanent);

    /// <summary>Notes a submitted job. Only copy, move and delete are kept; anything else cannot run elevated.</summary>
    public void Remember(Guid jobId, JobKind kind, IReadOnlyList<string> sources, string? destination, bool permanent)
    {
        if (kind is not (JobKind.Copy or JobKind.Move or JobKind.Delete))
        {
            return;
        }

        if (_jobs.Count >= Limit)
        {
            _jobs.Clear();
        }

        _jobs[jobId] = new Remembered(kind, sources, destination, permanent);
    }

    /// <summary>The request that does the refused item again, or null when the entry is not stopped on "access denied" or its job is not remembered.</summary>
    public JobRequest? RequestFor(Hub.OperationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Prompt is not { Kind: ErrorKind.AccessDenied } prompt || !_jobs.TryGetValue(entry.Id, out var job))
        {
            return null;
        }

        var item = job.Sources.FirstOrDefault(s => string.Equals(s.TrimEnd('\\'), prompt.Item.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<string> sources = item is null ? job.Sources : [item];
        return new JobRequest(job.Kind, sources, job.Destination)
        {
            DeleteMode = job.Permanent ? DeleteMode.Permanent : DeleteMode.RecycleBin,
            Conflict = ConflictPolicy.KeepBoth,
            Title = $"{job.Kind} as administrator",
        };
    }
}
