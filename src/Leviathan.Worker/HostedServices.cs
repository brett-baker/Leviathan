using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leviathan.Worker;

/// <summary>Starts and stops the job host with the process.</summary>
public sealed class JobHostService(JobHost host) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        host.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken) => await host.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Accepts UI connections on the per-user pipe.</summary>
public sealed class PipeServerService : IHostedService, IAsyncDisposable
{
    private readonly PipeServer _server;

    /// <summary>Creates the service.</summary>
    public PipeServerService(WorkerService service, WorkerHub hub, ILogger<PipeServer> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(hub);
        _server = new PipeServer(
            PipeNaming.WorkerPipe(),
            connection =>
            {
                connection.Rpc.AddLocalRpcTarget<IWorkerService>(service, null);
                hub.Add(connection.Id, connection.Rpc.Attach<IWorkerCallbacks>());
                _ = connection.Rpc.Completion.ContinueWith(_ => hub.Remove(connection.Id), TaskScheduler.Default);
            },
            logger);
    }

    /// <summary>The underlying server.</summary>
    public PipeServer Server => _server;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken) => await _server.DisposeAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// Exits the worker when no UI is connected and no job is pending for <see cref="IdleTimeout"/> (spec §2.2: "exits when
/// idle and queue is empty"). The UI starts it again on demand.
/// </summary>
public sealed class IdleShutdownService(JobHost host, PipeServerService pipe, IHostApplicationLifetime lifetime, ILogger<IdleShutdownService> logger) : BackgroundService
{
    /// <summary>Idle time before exiting.</summary>
#if DEBUG
    // A debug session ends by Stop Debugging, which kills the UI without a goodbye. Leaving at once frees the output files for the next
    // build; a release build keeps the two minutes so a quick restart reuses the warm process.
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);
#else
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);
#endif

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var idle = pipe.Server.ConnectionCount == 0 && !host.IsBusy && DateTimeOffset.UtcNow - pipe.Server.LastDisconnect > IdleTimeout;
            if (idle)
            {
                logger.LogInformation("No clients and no pending jobs for {Timeout}; exiting", IdleTimeout);
                lifetime.StopApplication();
                return;
            }
        }
    }
}
