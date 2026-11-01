using System.Collections.Concurrent;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Leviathan.Ipc;

/// <summary>One accepted client connection.</summary>
/// <param name="Id">Connection number.</param>
/// <param name="Rpc">The JSON-RPC connection.</param>
public sealed record PipeConnection(int Id, JsonRpc Rpc);

/// <summary>
/// Accepts JSON-RPC clients on a named pipe that only the current Windows user can open (spec §2.2). For each connection
/// the configure callback registers the local RPC target and any callback proxies before listening starts.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Action<PipeConnection> _configure;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, PipeConnection> _connections = new();
    private Task? _accept;
    private int _nextId;

    /// <summary>Creates the server.</summary>
    /// <param name="pipeName">Pipe name (see <see cref="PipeNaming"/>).</param>
    /// <param name="configure">Called for each new connection before it starts listening.</param>
    /// <param name="logger">Logger.</param>
    public PipeServer(string pipeName, Action<PipeConnection> configure, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Number of connected clients.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>When the last client disconnected (used for idle shutdown).</summary>
    public DateTimeOffset LastDisconnect { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Starts accepting connections.</summary>
    public void Start() => _accept ??= Task.Run(() => AcceptLoopAsync(_stop.Token));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var connection in _connections.Values)
        {
            connection.Rpc.Dispose();
        }

        if (_accept is not null)
        {
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                // CurrentUserOnly: Windows restricts the pipe to the current user's SID and verifies the client's identity.
                stream = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                Accept(stream);
                stream = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Pipe accept failed on {Pipe}; retrying", _pipeName);
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private void Accept(NamedPipeServerStream stream)
    {
        var id = Interlocked.Increment(ref _nextId);
        var rpc = RpcChannel.Create(stream);
        var connection = new PipeConnection(id, rpc);
        _configure(connection);
        _connections[id] = connection;
        rpc.StartListening();
        _logger.LogInformation("Client {Id} connected on {Pipe}", id, _pipeName);

        _ = rpc.Completion.ContinueWith(
            t =>
            {
                _connections.TryRemove(id, out _);
                LastDisconnect = DateTimeOffset.UtcNow;
                rpc.Dispose();
                stream.Dispose();
                _logger.LogInformation("Client {Id} disconnected{Reason}", id, t.Exception is null ? string.Empty : $": {t.Exception.GetBaseException().Message}");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
