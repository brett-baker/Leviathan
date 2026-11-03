using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leviathan.Engine.Supervision;

/// <summary>A started child process, abstracted so the supervisor can be tested without real processes.</summary>
public interface ISupervisedProcess : IDisposable
{
    /// <summary>Process ID.</summary>
    int Id { get; }

    /// <summary>Completes with the exit code when the process exits.</summary>
    Task<int> Exited { get; }

    /// <summary>Kills the process tree.</summary>
    void Kill();
}

/// <summary>Starts a child process.</summary>
public interface IProcessLauncher
{
    /// <summary>Starts the process.</summary>
    ISupervisedProcess Start();
}

/// <summary>State of a supervised worker.</summary>
public enum SupervisedState
{
    /// <summary>Not started.</summary>
    Stopped,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Crashed; waiting to restart.</summary>
    Restarting,

    /// <summary>Crashed too often; disabled until the user re-enables it.</summary>
    Disabled,
}

/// <summary>
/// Keeps a worker process alive (spec §2.2, §3.4): restarts it after a crash per <see cref="RestartPolicy"/> and reports
/// state changes so the UI can tell the user when a feature is disabled. Expected exits (idle shutdown) are not restarted;
/// the next request starts the worker again via <see cref="EnsureRunning"/>.
/// </summary>
public sealed class ProcessSupervisor : IAsyncDisposable
{
    private readonly IProcessLauncher _launcher;
    private readonly RestartPolicy _policy;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _disposed = new();
    private ISupervisedProcess? _process;
    private Task? _watch;
    private bool _stopRequested;

    /// <summary>Creates a supervisor.</summary>
    public ProcessSupervisor(string name, IProcessLauncher launcher, RestartPolicy? policy = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _time = time ?? TimeProvider.System;
        _policy = policy ?? new RestartPolicy(_time);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Name for logs, e.g. "Leviathan.Worker".</summary>
    public string Name { get; }

    /// <summary>Current state.</summary>
    public SupervisedState State { get; private set; } = SupervisedState.Stopped;

    /// <summary>Why the worker was disabled, when it is.</summary>
    public string? DisabledReason { get; private set; }

    private int _restartCount;

    /// <summary>How many times the process was started again after a crash since this supervisor was made (the Background tab shows it, spec 16.1).</summary>
    public int RestartCount => Volatile.Read(ref _restartCount);

    /// <summary>Process id of the running process, or null when there is none.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_lock)
            {
                return State == SupervisedState.Running ? _process?.Id : null;
            }
        }
    }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event EventHandler<SupervisedState>? StateChanged;

    /// <summary>Starts the process unless it is already running or disabled.</summary>
    public void EnsureRunning()
    {
        lock (_lock)
        {
            if (State is SupervisedState.Running or SupervisedState.Restarting or SupervisedState.Disabled)
            {
                return;
            }

            _stopRequested = false;
            StartLocked();
        }
    }

    /// <summary>Re-enables a disabled worker and starts it.</summary>
    public void Reenable()
    {
        lock (_lock)
        {
            _policy.Reset();
            DisabledReason = null;
            if (State == SupervisedState.Disabled)
            {
                State = SupervisedState.Stopped;
            }
        }

        EnsureRunning();
    }

    /// <summary>Marks the next exit as expected (e.g. the app is closing and the worker may finish its queue).</summary>
    public void RequestStop()
    {
        lock (_lock)
        {
            _stopRequested = true;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        RequestStop();
        await _disposed.CancelAsync().ConfigureAwait(false);
        if (_watch is not null)
        {
            try
            {
                await _watch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _process?.Dispose();
        _disposed.Dispose();
    }

    private void StartLocked()
    {
        _process?.Dispose();
        _process = _launcher.Start();
        _logger.LogInformation("{Name} started (pid {Pid})", Name, _process.Id);
        SetState(SupervisedState.Running);
        var process = _process;
        _watch = Task.Run(() => WatchAsync(process, _disposed.Token));
    }

    private async Task WatchAsync(ISupervisedProcess process, CancellationToken cancellationToken)
    {
        var exitCode = await process.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        RestartDecision decision;
        lock (_lock)
        {
            decision = _policy.OnExit(exitCode, _stopRequested);
            if (!decision.Restart)
            {
                if (_policy.GaveUp)
                {
                    DisabledReason = decision.Reason;
                    _logger.LogError("{Name} disabled: {Reason}", Name, decision.Reason);
                    SetState(SupervisedState.Disabled);
                }
                else
                {
                    SetState(SupervisedState.Stopped);
                }

                return;
            }

            _logger.LogWarning("{Name} exited with {ExitCode}; restarting in {Delay}", Name, exitCode, decision.Delay);
            SetState(SupervisedState.Restarting);
        }

        await Task.Delay(decision.Delay, _time, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            if (State == SupervisedState.Restarting)
            {
                Interlocked.Increment(ref _restartCount);
                StartLocked();
            }
        }
    }

    private void SetState(SupervisedState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }
}

/// <summary>Launches a real executable and watches it.</summary>
public sealed class ExecutableLauncher : IProcessLauncher
{
    private readonly ProcessStartInfo _startInfo;

    /// <summary>Creates a launcher for <paramref name="path"/>.</summary>
    public ExecutableLauncher(string path, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _startInfo = new ProcessStartInfo(path, arguments ?? string.Empty)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
        };
    }

    /// <inheritdoc />
    public ISupervisedProcess Start()
    {
        var process = Process.Start(_startInfo) ?? throw new InvalidOperationException($"Could not start {_startInfo.FileName}.");
        return new RealProcess(process);
    }

    private sealed class RealProcess(Process process) : ISupervisedProcess
    {
        public int Id => process.Id;

        public Task<int> Exited { get; } = WaitAsync(process);

        public void Kill()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        public void Dispose() => process.Dispose();

        private static async Task<int> WaitAsync(Process p)
        {
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode;
        }
    }
}
