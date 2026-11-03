using Leviathan.Contracts;
using Leviathan.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leviathan.ShellHost;

/// <summary>Accepts UI connections on the per-user shell host pipe.</summary>
internal sealed class ShellPipeService : IHostedService, IAsyncDisposable
{
    private readonly PipeServer _server;

    public ShellPipeService(ShellHostService service, ILogger<PipeServer> logger, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(service);
        _server = new PipeServer(pipeName, connection => connection.Rpc.AddLocalRpcTarget<IShellHostService>(service, null), logger);
    }

    public PipeServer Server => _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await _server.DisposeAsync().ConfigureAwait(false);

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// Exits the host when no UI has been connected for <see cref="IdleTimeout"/> (spec §2.2: ShellHost runs "on demand"). The UI
/// starts it again at the next right-click.
/// </summary>
internal sealed class ShellIdleShutdownService(ShellPipeService pipe, IHostApplicationLifetime lifetime, ILogger<ShellIdleShutdownService> logger) : BackgroundService
{
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (pipe.Server.ConnectionCount == 0 && DateTimeOffset.UtcNow - pipe.Server.LastDisconnect > IdleTimeout)
            {
                logger.LogInformation("No clients for {Timeout}; exiting", IdleTimeout);
                lifetime.StopApplication();
                return;
            }
        }
    }
}

/// <summary>Closes context menus nobody has used for a while: the user dismissed the popup and the UI never said so.</summary>
internal sealed class MenuSweeperService(ShellMenuManager menus) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            menus.SweepIdle();
        }
    }
}

/// <summary>
/// Ends the process with a failure code when the STA pool or the menus have too many threads stuck inside shell extensions, so
/// the supervisor starts a clean host instead of the UI waiting on one that can no longer answer (spec §3.4).
/// </summary>
internal sealed class PoolHealthService(StaPool pool, ShellMenuManager menus, IHostApplicationLifetime lifetime, ILogger<PoolHealthService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        pool.Unhealthy += OnUnhealthy;
        menus.Unhealthy += OnUnhealthy;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        pool.Unhealthy -= OnUnhealthy;
        menus.Unhealthy -= OnUnhealthy;
        return Task.CompletedTask;
    }

    private void OnUnhealthy(object? sender, EventArgs e)
    {
        logger.LogError("Too many shell threads are stuck ({Pool} in the pool, {Menus} menus); exiting to be restarted", pool.Status.Abandoned, menus.Abandoned);
        Environment.ExitCode = 2;
        lifetime.StopApplication();
    }
}
