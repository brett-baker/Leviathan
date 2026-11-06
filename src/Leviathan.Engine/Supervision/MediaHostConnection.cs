using Leviathan.Contracts;
using Leviathan.Ipc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leviathan.Engine.Supervision;

/// <summary>The media process cannot be used right now; columns that need it stay empty and the reason is shown (spec §3.3).</summary>
public sealed class MediaHostUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MediaHostUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Starts the media process the first time somebody needs it, keeps it alive through <see cref="ProcessSupervisor"/> and hands out a
/// connected proxy. After a crash the next call reconnects to the restarted process; after three crashes in five minutes it is disabled
/// and every call fails at once with <see cref="MediaHostUnavailableException"/> rather than waiting (spec §3.3, §3.4). The same shape
/// as <see cref="ShellHostConnection"/>: both are "a process that may die and must not take the window with it".
/// </summary>
public sealed class MediaHostConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromSeconds(1);

    private readonly ProcessSupervisor _supervisor;
    private readonly MediaClient _client;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connecting = new(1, 1);

    /// <summary>Creates the connection. Nothing starts until <see cref="GetServiceAsync"/>.</summary>
    /// <param name="launcher">Starts the process.</param>
    /// <param name="pipeName">The process's pipe; null for the current user's.</param>
    /// <param name="policy">Restart rules; the defaults of spec §3.4 when null.</param>
    /// <param name="logger">Logger.</param>
    public MediaHostConnection(IProcessLauncher launcher, string? pipeName = null, RestartPolicy? policy = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _supervisor = new ProcessSupervisor("Leviathan.Media", launcher, policy, logger: _logger);
        _client = new MediaClient(pipeName, _logger);
        _supervisor.StateChanged += (_, state) => StateChanged?.Invoke(this, state);
        _client.Disconnected += (_, _) => Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the process starts, restarts, stops or is disabled.</summary>
    public event EventHandler<SupervisedState>? StateChanged;

    /// <summary>Raised when the connection to the process drops (a crash, or the idle exit); callers that cached answers keep them, and ask again after the next connect.</summary>
    public event EventHandler? Disconnected;

    /// <summary>State of the process.</summary>
    public SupervisedState State => _supervisor.State;

    /// <summary>How many times the process was started again after a crash.</summary>
    public int RestartCount => _supervisor.RestartCount;

    /// <summary>Process id of the running process, or null.</summary>
    public int? SupervisedProcessId => _supervisor.ProcessId;

    /// <summary>Why the process was disabled, when it was.</summary>
    public string? DisabledReason => _supervisor.DisabledReason;

    /// <summary>True while a connection to a running process is open.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>What the process said about itself at connect (where ffprobe is, how many files were quarantined at start), or null.</summary>
    public MediaHostInfo? Info => _client.IsConnected ? _client.Info : null;

    /// <summary>Re-enables a disabled process (the user asked, spec §3.4) and starts it.</summary>
    public void Reenable() => _supervisor.Reenable();

    /// <summary>A proxy to the running process: starts it if needed and connects, trying for at most <paramref name="timeout"/> in all.</summary>
    /// <exception cref="MediaHostUnavailableException">The process is disabled, or did not come up in time.</exception>
    public async Task<IMediaService> GetServiceAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return _client.Service;
        }

        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
            {
                return _client.Service;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            Exception? last = null;
            try
            {
                while (true)
                {
                    if (_supervisor.State == SupervisedState.Disabled)
                    {
                        throw new MediaHostUnavailableException($"Media information is turned off: {_supervisor.DisabledReason}");
                    }

                    _supervisor.EnsureRunning();
                    try
                    {
                        await _client.ConnectAsync(ConnectAttempt, limit.Token).ConfigureAwait(false);
                        return _client.Service;
                    }
                    catch (Exception ex) when (ex is TimeoutException or IOException or StreamJsonRpc.ConnectionLostException or ObjectDisposedException)
                    {
                        // Not up yet (starting, or restarting after a crash), or we reached the old process while it was still dying.
                        last = ex;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), limit.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(last, "Media process did not come up within {Timeout}", timeout);
                throw new MediaHostUnavailableException("The media process did not start in time; media columns stay empty.", last);
            }
        }
        finally
        {
            _connecting.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _supervisor.RequestStop();
        await _client.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _connecting.Dispose();
    }
}
