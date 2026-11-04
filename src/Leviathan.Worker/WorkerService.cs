using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Microsoft.Extensions.Logging;

namespace Leviathan.Worker;

/// <summary>Fans job notifications out to every connected UI process and drops connections that stop answering.</summary>
public sealed class WorkerHub
{
    private readonly ConcurrentDictionary<int, IWorkerCallbacks> _clients = new();
    private readonly ILogger<WorkerHub> _logger;

    /// <summary>Creates the hub and subscribes to the job host.</summary>
    public WorkerHub(JobHost host, ILogger<WorkerHub> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        _logger = logger;
        host.JobChanged += (_, snapshot) => Broadcast(c => c.OnJobChangedAsync(snapshot));
        host.JobRemoved += (_, id) => Broadcast(c => c.OnJobRemovedAsync(id));
    }

    /// <summary>Registers a client's callback proxy.</summary>
    public void Add(int connectionId, IWorkerCallbacks callbacks) => _clients[connectionId] = callbacks;

    /// <summary>Removes a client.</summary>
    public void Remove(int connectionId) => _clients.TryRemove(connectionId, out _);

    private void Broadcast(Func<IWorkerCallbacks, Task> send)
    {
        foreach (var (id, client) in _clients)
        {
            _ = SendAsync(id, client, send);
        }
    }

    private async Task SendAsync(int id, IWorkerCallbacks client, Func<IWorkerCallbacks, Task> send)
    {
        try
        {
            await send(client).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A UI that closed or hung must not affect the worker.
            _logger.LogDebug(ex, "Dropping client {Id} after a failed notification", id);
            _clients.TryRemove(id, out _);
        }
    }
}

/// <summary>The worker's IPC endpoint; a thin translation layer over <see cref="JobHost"/>.</summary>
public sealed class WorkerService : IWorkerService
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    private static readonly string AppVersion =
        typeof(WorkerService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static readonly ActivitySource Activities = new("Leviathan.Worker");

    private static readonly TimeSpan IdentifyTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly JobHost _host;
    private readonly ServerIdentityMap? _identities;

    /// <summary>Creates the service. With <paramref name="identities"/>, names of one server share a device limit (spec §3.5).</summary>
    public WorkerService(JobHost host, ServerIdentityMap? identities = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _identities = identities;
    }

    /// <inheritdoc />
    public Task<WorkerInfo> PingAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new WorkerInfo(ContractVersion.Major, ContractVersion.Minor, AppVersion, Environment.ProcessId, StartedAt));

    /// <inheritdoc />
    public async Task<JobSnapshot> SubmitAsync(JobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = Activities.StartActivity("SubmitJob");
        activity?.SetTag("job.kind", request.Kind.ToString());

        if (JobRequestValidator.Validate(request) is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        var sources = request.Sources.Select(LocationPath.Parse).ToList();
        var destination = request.Destination is null ? null : LocationPath.Parse(request.Destination);
        var lane = request.Kind == JobKind.ComputeFolderSize ? JobLane.Background : JobLane.Operations;
        IEnumerable<LocationPath> touched = destination is null ? sources : [destination, .. sources];
        string? device;
        if (_identities is null)
        {
            device = DeviceKeys.ForJob(touched);
        }
        else
        {
            foreach (var server in touched.Where(l => l.Kind == LocationKind.Unc).Select(l => l.Server!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await _identities.IdentifyAsync(server, IdentifyTimeout, cancellationToken).ConfigureAwait(false);
            }

            device = DeviceKeys.ForJob(touched, _identities);
        }

        var title = request.Title ?? JobTitles.For(request.Kind, sources, destination);
        return _host.Submit(request, lane, device, title);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobSnapshot>> GetJobsAsync(CancellationToken cancellationToken) => Task.FromResult(_host.GetSnapshots());

    /// <inheritdoc />
    public Task<JobResult?> GetResultAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(_host.GetResult(jobId));

    /// <inheritdoc />
    public Task PauseAsync(Guid jobId, CancellationToken cancellationToken) => Run(() => _host.Pause(jobId));

    /// <inheritdoc />
    public Task ResumeAsync(Guid jobId, CancellationToken cancellationToken) => Run(() => _host.Resume(jobId));

    /// <inheritdoc />
    public Task CancelAsync(Guid jobId, CancellationToken cancellationToken) => Run(() => _host.Cancel(jobId));

    /// <inheritdoc />
    public Task RemoveAsync(Guid jobId, CancellationToken cancellationToken) => Run(() => _host.Remove(jobId));

    /// <inheritdoc />
    public Task MoveAsync(Guid jobId, int queuePosition, CancellationToken cancellationToken) => Run(() => _host.Move(jobId, queuePosition));

    /// <inheritdoc />
    public Task SetPriorityAsync(Guid jobId, JobPriority priority, CancellationToken cancellationToken) => Run(() => _host.SetPriority(jobId, priority));

    /// <inheritdoc />
    public Task RunNowAsync(Guid jobId, CancellationToken cancellationToken) => Run(() => _host.RunNow(jobId));

    /// <inheritdoc />
    public Task PauseAllAsync(CancellationToken cancellationToken) => Run(_host.PauseAll);

    /// <inheritdoc />
    public Task ResumeAllAsync(CancellationToken cancellationToken) => Run(_host.ResumeAll);

    /// <inheritdoc />
    public Task<ConcurrencyLimits> GetLimitsAsync(CancellationToken cancellationToken) => Task.FromResult(_host.Scheduler.Limits);

    /// <inheritdoc />
    public Task SetLimitsAsync(ConcurrencyLimits limits, CancellationToken cancellationToken) => Run(() => _host.SetLimits(limits));

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static Task Run(Func<bool> action)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>Composes job titles such as "Move 312 items to \\nas\media\keep".</summary>
public static class JobTitles
{
    /// <summary>Title for a job.</summary>
    public static string For(JobKind kind, IReadOnlyList<LocationPath> sources, LocationPath? destination)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var what = sources.Count == 1 ? sources[0].Name : $"{sources.Count} items";
        return kind switch
        {
            JobKind.Copy => $"Copy {what} to {destination}",
            JobKind.Move => $"Move {what} to {destination}",
            JobKind.Delete => $"Delete {what}",
            JobKind.Purge => $"Purge {what}",
            JobKind.Extract => $"Extract {what} to {destination}",
            JobKind.Compress => $"Compress {what}",
            JobKind.Sync => $"Sync {what} → {destination}",
            JobKind.BulkRename => $"Rename {what}",
            JobKind.Rename => $"Rename {what}",
            JobKind.Link => $"Create shortcut to {what} in {destination}",
            JobKind.Restore => $"Restore {what}",
            JobKind.ComputeFolderSize => $"Folder sizes: {(sources.Count == 1 ? sources[0].Value : what)}",
            _ => $"{kind} {what}",
        };
    }
}
