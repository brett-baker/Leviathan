using System.IO.Pipes;
using Leviathan.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Leviathan.Ipc;

/// <summary>
/// The UI's connection to the worker. Connects with a timeout, checks the contract version, and reports disconnects so
/// the caller can restart the worker and reconnect (the worker's journal means nothing is lost meanwhile).
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IWorkerCallbacks _callbacks;
    private readonly ILogger _logger;
    private NamedPipeClientStream? _stream;
    private JsonRpc? _rpc;

    /// <summary>Creates a client.</summary>
    public WorkerClient(IWorkerCallbacks callbacks, string? pipeName = null, ILogger? logger = null)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _pipeName = pipeName ?? PipeNaming.WorkerPipe();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The worker proxy; valid after <see cref="ConnectAsync"/> succeeds.</summary>
    public IWorkerService Service { get; private set; } = null!;

    /// <summary>Information the worker reported at connect.</summary>
    public WorkerInfo? Info { get; private set; }

    /// <summary>True while connected.</summary>
    public bool IsConnected => _rpc is { IsDisposed: false } && _stream is { IsConnected: true };

    /// <summary>Raised when the connection drops.</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Connects, or throws <see cref="TimeoutException"/> if the worker does not answer in time.</summary>
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
            throw new TimeoutException($"The worker did not accept a connection within {timeout.TotalSeconds:0.#} s.");
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var rpc = RpcChannel.Create(stream);
        rpc.AddLocalRpcTarget<IWorkerCallbacks>(_callbacks, null);
        var service = rpc.Attach<IWorkerService>();
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
            throw new InvalidOperationException($"Worker speaks contract v{info.ContractMajor}; this app needs v{ContractVersion.Major}. Restart Leviathan.");
        }

        _stream = stream;
        _rpc = rpc;
        Service = service;
        Info = info;
        _logger.LogInformation("Connected to worker {Version} (pid {Pid})", info.AppVersion, info.ProcessId);
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
