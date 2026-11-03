using Leviathan.Contracts;
using Leviathan.Ipc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leviathan.Engine.Supervision;

/// <summary>The shell host cannot be used right now; the caller shows the built-in items only and says why (spec §2.2).</summary>
public sealed class ShellHostUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ShellHostUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Starts the shell host the first time somebody needs it, keeps it alive through <see cref="ProcessSupervisor"/> and hands out a
/// connected proxy. After a crash the next call reconnects to the restarted host; after three crashes in five minutes the host is
/// disabled and every call fails at once with <see cref="ShellHostUnavailableException"/> rather than waiting (spec §3.3, §3.4).
/// </summary>
public sealed class ShellHostConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromSeconds(1);

    private readonly ProcessSupervisor _supervisor;
    private readonly ShellHostClient _client;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connecting = new(1, 1);

    /// <summary>Creates the connection. Nothing starts until <see cref="GetServiceAsync"/>.</summary>
    /// <param name="launcher">Starts the host process.</param>
    /// <param name="pipeName">The host's pipe; null for the current user's.</param>
    /// <param name="policy">Restart rules; the defaults of spec §3.4 when null.</param>
    /// <param name="logger">Logger.</param>
    public ShellHostConnection(IProcessLauncher launcher, string? pipeName = null, RestartPolicy? policy = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _supervisor = new ProcessSupervisor("Leviathan.ShellHost", launcher, policy, logger: _logger);
        _client = new ShellHostClient(pipeName, _logger);
        _supervisor.StateChanged += (_, state) => StateChanged?.Invoke(this, state);
    }

    /// <summary>Raised when the host process starts, restarts, stops or is disabled.</summary>
    public event EventHandler<SupervisedState>? StateChanged;

    /// <summary>State of the host process.</summary>
    public SupervisedState State => _supervisor.State;

    /// <summary>Why the host was disabled, when it was.</summary>
    public string? DisabledReason => _supervisor.DisabledReason;

    /// <summary>True while a connection to a running host is open.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>Re-enables a disabled host (the user asked, spec §3.4) and starts it.</summary>
    public void Reenable() => _supervisor.Reenable();

    /// <summary>
    /// A proxy to the running host: starts it if needed and connects, trying for at most <paramref name="timeout"/> in all.
    /// </summary>
    /// <exception cref="ShellHostUnavailableException">The host is disabled, or did not come up in time.</exception>
    public async Task<IShellHostService> GetServiceAsync(TimeSpan timeout, CancellationToken cancellationToken)
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
                        throw new ShellHostUnavailableException($"Shell extensions are turned off: {_supervisor.DisabledReason}");
                    }

                    _supervisor.EnsureRunning();
                    try
                    {
                        await _client.ConnectAsync(ConnectAttempt, limit.Token).ConfigureAwait(false);
                        return _client.Service;
                    }
                    catch (Exception ex) when (ex is TimeoutException or IOException)
                    {
                        // Not up yet (starting, or restarting after a crash). Try again until the limit.
                        last = ex;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), limit.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(last, "Shell host did not come up within {Timeout}", timeout);
                throw new ShellHostUnavailableException("Shell extensions did not start in time; showing the built-in items only.", last);
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
