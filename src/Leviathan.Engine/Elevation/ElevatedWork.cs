using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Elevation;

/// <summary>What an elevated run did: how the job ended and what became of each item.</summary>
/// <param name="State">Completed, Failed or Cancelled.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="Items">One entry per item handled.</param>
public sealed record ElevatedResult(JobState State, string? Error, IReadOnlyList<ItemOutcome> Items)
{
    /// <summary>True when the job completed and no item failed.</summary>
    public bool Succeeded => State == JobState.Completed && Items.All(i => i.Status != ItemStatus.Failed);

    /// <summary>False when the helper never did anything: the prompt was declined, it could not start, or it sent nothing.</summary>
    public bool Ran => !(State == JobState.Failed && Items.Count == 0);

    /// <summary>One line for the person: what was done, or what went wrong.</summary>
    public string Summary =>
        !Ran ? Error ?? "The administrator helper did not run."
        : Items.FirstOrDefault(i => i.Status == ItemStatus.Failed) is { } failed ? $"{failed.Source} could not be done even as administrator: {failed.Error}"
        : $"{Items.Count(i => i.Status == ItemStatus.Done)} item(s) done as administrator.";

    /// <summary>A failed result with a reason and no items (the helper could not be started or did not answer).</summary>
    public static ElevatedResult Failure(string error) => new(JobState.Failed, error, []);
}

/// <summary>
/// The request side of the elevated helper (spec §15.6): when one action needs administrator rights only that action runs elevated, through
/// <c>Leviathan.Elevated.exe</c>, never the whole app. The request travels on the helper's command line (so nothing that runs as the same
/// user can change it after the user has said yes to the prompt) and is limited to a short allow-list: copy, move and delete between
/// drives and Windows shares. The answer comes back over a pipe the app opened. Pure; no process is started here.
/// </summary>
public static class ElevatedWork
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>True when <paramref name="request"/> may run elevated: copy, move or delete of local or share paths, nothing else.</summary>
    public static bool IsAllowed(JobRequest request, out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        reason = string.Empty;
        if (request.Kind is not (JobKind.Copy or JobKind.Move or JobKind.Delete))
        {
            reason = $"{request.Kind} cannot run as administrator; only copy, move and delete can.";
            return false;
        }

        if (request.Sources.Count == 0 || request.Sources.Count > 5000)
        {
            reason = "There is nothing to do, or too much for one elevated run.";
            return false;
        }

        var needsDestination = request.Kind != JobKind.Delete;
        if (needsDestination != !string.IsNullOrWhiteSpace(request.Destination))
        {
            reason = needsDestination ? "A copy or move needs a destination." : "A delete has no destination.";
            return false;
        }

        foreach (var place in request.Sources.Concat(request.Destination is null ? [] : [request.Destination]))
        {
            if (!IsLocalOrShare(place))
            {
                reason = $"{place} is not on a drive or a Windows share.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Turns a request into the single argument the helper takes, or throws when it is not allowed.</summary>
    public static string Encode(JobRequest request)
    {
        if (!IsAllowed(request, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request with { ArchivePassword = null }, Json)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        // A command line holds about 32,000 characters; a bigger selection is asked for in smaller parts.
        return encoded.Length <= 28000 ? encoded : throw new InvalidOperationException("That is too many items for one administrator run. Try fewer at a time.");
    }

    /// <summary>The request in <paramref name="argument"/>, or null when it is damaged or not allowed.</summary>
    public static JobRequest? Decode(string argument)
    {
        try
        {
            var text = argument.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
            var request = JsonSerializer.Deserialize<JobRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(text)), Json);
            return request is not null && IsAllowed(request, out _) ? request : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The answer as one line of JSON for the pipe.</summary>
    public static string EncodeResult(ElevatedResult result) => JsonSerializer.Serialize(result, Json);

    /// <summary>The answer read from the pipe, or a failure when the text is not one.</summary>
    public static ElevatedResult DecodeResult(string? line)
    {
        try
        {
            return line is { Length: > 0 } && JsonSerializer.Deserialize<ElevatedResult>(line, Json) is { } result ? result : ElevatedResult.Failure("The administrator helper sent no answer.");
        }
        catch (JsonException)
        {
            return ElevatedResult.Failure("The administrator helper sent an answer that could not be read.");
        }
    }

    /// <summary>Runs <paramref name="request"/> once, in this process, with <paramref name="executors"/>, and says how it ended. Used by the helper.</summary>
    public static async Task<ElevatedResult> RunAsync(JobRequest request, IEnumerable<IJobExecutor> executors, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executors);
        if (!IsAllowed(request, out var reason))
        {
            return ElevatedResult.Failure(reason);
        }

        var host = new JobHost(new JobScheduler(), executors, new InMemoryJobJournal());
        await using var _ = host.ConfigureAwait(false);
        var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.JobChanged += (_, snapshot) =>
        {
            if (snapshot.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            {
                done.TrySetResult(snapshot);
            }
        };
        host.Start();
        var submitted = host.Submit(request with { OnError = ErrorMode.SkipAndReport }, JobLane.Operations, null, request.Title ?? $"{request.Kind} as administrator");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var ended = await done.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            return new ElevatedResult(ended.State, ended.Error, host.GetResult(submitted.Id)?.Items ?? []);
        }
        catch (OperationCanceledException)
        {
            host.Cancel(submitted.Id);
            return ElevatedResult.Failure("The administrator run took too long and was stopped.");
        }
    }

    private static bool IsLocalOrShare(string place) =>
        place.Length >= 3 && (place[1] == ':' && place[2] == '\\' || place.StartsWith(@"\\", StringComparison.Ordinal) && !place.StartsWith(@"\\?\", StringComparison.Ordinal) || place.StartsWith(@"\\?\", StringComparison.Ordinal) && place.Length > 6 && place[5] == ':');
}
