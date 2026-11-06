using Leviathan.Contracts;
using Leviathan.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leviathan.MediaHost;

/// <summary>Accepts UI connections on the media pipe.</summary>
internal sealed class MediaPipeService : IHostedService, IAsyncDisposable
{
    private readonly PipeServer _server;

    public MediaPipeService(MediaHostService service, ILogger<PipeServer> logger, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(service);
        _server = new PipeServer(pipeName, connection => connection.Rpc.AddLocalRpcTarget<IMediaService>(service, null), logger);
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

/// <summary>Exits when no UI has been connected for two minutes (spec §2.2: Media runs "on demand, killed when idle"); the UI starts it again when a folder with media is shown.</summary>
internal sealed class MediaIdleShutdownService(MediaPipeService pipe, IHostApplicationLifetime lifetime, ILogger<MediaIdleShutdownService> logger) : BackgroundService
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
