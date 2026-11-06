using System.IO.Pipes;
using Leviathan.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Leviathan.Ipc;

/// <summary>
/// The UI's connection to the media process. Connects with a timeout, checks the contract version and reports disconnects so the
/// caller can restart the process and reconnect (a media file that kills it costs one restart, spec §2.2, §9.3).
/// </summary>
public sealed class MediaClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private NamedPipeClientStream? _stream;
    private JsonRpc? _rpc;

    /// <summary>Creates a client for the current user's media process, or for <paramref name="pipeName"/> in tests.</summary>
    public MediaClient(string? pipeName = null, ILogger? logger = null)
    {
        _pipeName = pipeName ?? PipeNaming.MediaPipe();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The process proxy; valid after <see cref="ConnectAsync"/> succeeds.</summary>
    public IMediaService Service { get; private set; } = null!;

    /// <summary>Information the process reported at connect.</summary>
    public MediaHostInfo? Info { get; private set; }

    /// <summary>True while connected.</summary>
    public bool IsConnected => _rpc is { IsDisposed: false } && _stream is { IsConnected: true };

    /// <summary>Raised when the connection drops.</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Connects, or throws <see cref="TimeoutException"/> if the process does not answer in time.</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(timeout);
            await stream.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException($"The media process did not accept a connection within {timeout.TotalSeconds:0.#} s.");
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var rpc = RpcChannel.Create(stream);
        var service = rpc.Attach<IMediaService>();
        rpc.StartListening();

        _ = rpc.Completion.ContinueWith(
            t => Disconnected?.Invoke(this, t.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var info = await service.PingAsync(cancellationToken).ConfigureAwait(false);
        if (info.ContractMajor != ContractVersion.Major)
        {
            rpc.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The media process speaks contract v{info.ContractMajor}; this app needs v{ContractVersion.Major}. Restart Leviathan.");
        }

        _stream = stream;
        _rpc = rpc;
        Service = service;
        Info = info;
        _logger.LogInformation("Connected to media process {Version} (pid {Pid})", info.AppVersion, info.ProcessId);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisposeConnectionAsync().ConfigureAwait(false);

    private async Task DisposeConnectionAsync()
    {
        _rpc?.Dispose();
        _rpc = null;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }
}
