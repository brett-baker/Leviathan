using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Leviathan.Contracts;
using Leviathan.Diagnostics;
using Leviathan.Engine.Supervision;
using Leviathan.Ipc;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Leviathan.App;

/// <summary>Adapts WinUI's dispatcher for the stall watchdog and for marshalling worker callbacks.</summary>
internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool TryEnqueue(Action callback) => queue.TryEnqueue(() =>
    {
        using var scope = UiTrace.Begin($"worker UI callback {callback.Method.Name}", thresholdMs: 5);
        callback();
    });
}

/// <summary>What the status line shows.</summary>
/// <param name="State">Supervisor state.</param>
/// <param name="Connected">True when the pipe is connected.</param>
/// <param name="Message">Text for the status line.</param>
internal sealed record WorkerStatus(SupervisedState State, bool Connected, string Message);

/// <summary>
/// Starts and supervises Leviathan.Worker, connects to it, and mirrors its jobs into <see cref="JobRow"/>s on the UI thread.
/// If the worker crashes it is restarted and the client reconnects; queued jobs survive via the worker's journal.
/// </summary>
internal sealed class WorkerConnection : IWorkerCallbacks, IAsyncDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly ObservableCollection<JobRow> _jobs;
    private readonly ProcessSupervisor _supervisor;
    private readonly WorkerClient _client;
    private readonly CancellationTokenSource _closing = new();
    private int _reconnecting;

    public WorkerConnection(IUiDispatcher ui, ObservableCollection<JobRow> jobs)
    {
        _ui = ui;
        _jobs = jobs;
        _supervisor = new ProcessSupervisor("Leviathan.Worker", WorkerLauncher());
        _supervisor.StateChanged += (_, state) => Publish(state, _client?.IsConnected == true);
        _client = new WorkerClient(this);
        _client.Disconnected += (_, _) => _ = ReconnectAsync();
    }

    public event EventHandler<WorkerStatus>? StatusChanged;

    /// <summary>State of the worker process.</summary>
    public SupervisedState State => _supervisor.State;

    /// <summary>How many times the worker was started again after a crash.</summary>
    public int RestartCount => _supervisor.RestartCount;

    /// <summary>Process id of the running worker, or null.</summary>
    public int? ProcessId => _supervisor.ProcessId;

    /// <summary>Why the worker was disabled, when it was.</summary>
    public string? DisabledReason => _supervisor.DisabledReason;

    /// <summary>Switches a disabled worker back on (the user asked, spec 3.4).</summary>
    public void Reenable() => _supervisor.Reenable();

    /// <summary>Pauses a job (the Background tab).</summary>
    public Task PauseAsync(Guid jobId) => _client.Service.PauseAsync(jobId, _closing.Token);

    /// <summary>Resumes a paused job.</summary>
    public Task ResumeAsync(Guid jobId) => _client.Service.ResumeAsync(jobId, _closing.Token);

    /// <summary>Changes how soon a queued job runs.</summary>
    public Task SetPriorityAsync(Guid jobId, JobPriority priority) => _client.Service.SetPriorityAsync(jobId, priority, _closing.Token);

    /// <summary>Raised on the calling thread (not the UI thread) whenever the worker reports a job's state.</summary>
    public event EventHandler<JobSnapshot>? JobChanged;

    public async Task StartAsync()
    {
        _supervisor.EnsureRunning();
        await ConnectWithRetryAsync();
    }

    public async Task<JobSnapshot> SubmitAsync(JobRequest request)
    {
        if (!_client.IsConnected)
        {
            await StartAsync();
        }

        var snapshot = await _client.Service.SubmitAsync(request, _closing.Token);
        await OnJobChangedAsync(snapshot);
        return snapshot;
    }

    /// <summary>The per-item results of a job (empty when the worker no longer knows it).</summary>
    public async Task<JobResult> GetResultAsync(Guid jobId) =>
        await _client.Service.GetResultAsync(jobId, _closing.Token).ConfigureAwait(false) ?? new JobResult(jobId, []);

    /// <summary>
    /// Submits a short job and waits for it to end, then returns its per-item results. For work whose outcome the caller needs, such
    /// as undo; it gives up after <paramref name="timeout"/> (spec §3.2: every wait has a limit) but the job itself keeps running.
    /// </summary>
    public async Task<JobResult> RunAsync(JobRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ended = new System.Collections.Concurrent.ConcurrentDictionary<Guid, JobSnapshot>();
        using var wake = new SemaphoreSlim(0);

        void OnChanged(object? sender, JobSnapshot snapshot)
        {
            if (snapshot.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            {
                ended[snapshot.Id] = snapshot;
                wake.Release();
            }
        }

        JobChanged += OnChanged;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var submitted = await SubmitAsync(request).ConfigureAwait(false);
            try
            {
                while (!ended.ContainsKey(submitted.Id))
                {
                    await wake.WaitAsync(limit.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The worker did not finish in time.");
            }

            return await _client.Service.GetResultAsync(submitted.Id, cancellationToken).ConfigureAwait(false)
                ?? new JobResult(submitted.Id, []);
        }
        finally
        {
            JobChanged -= OnChanged;
        }
    }

    public Task OnJobChangedAsync(JobSnapshot snapshot)
    {
        JobChanged?.Invoke(this, snapshot);
        _ui.TryEnqueue(() =>
        {
            var row = _jobs.FirstOrDefault(j => j.Id == snapshot.Id);
            if (row is null)
            {
                _jobs.Insert(0, new JobRow(snapshot));
            }
            else
            {
                row.Update(snapshot);
            }
        });
        return Task.CompletedTask;
    }

    public Task OnJobRemovedAsync(Guid jobId)
    {
        _ui.TryEnqueue(() =>
        {
            var row = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (row is not null)
            {
                _jobs.Remove(row);
            }
        });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();

        // The worker keeps running until its queue is empty, then exits on its own (idle shutdown).
        _supervisor.RequestStop();
        await _client.DisposeAsync();
        await _supervisor.DisposeAsync();
        _closing.Dispose();
    }

    private static ExecutableLauncher WorkerLauncher()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Leviathan.Worker.exe");
        return File.Exists(exe)
            ? new ExecutableLauncher(exe)
            : new ExecutableLauncher("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Leviathan.Worker.dll")}\"");
    }

    private async Task ConnectWithRetryAsync()
    {
        for (var attempt = 0; attempt < 10 && !_closing.IsCancellationRequested; attempt++)
        {
            try
            {
                await _client.ConnectAsync(TimeSpan.FromSeconds(2), _closing.Token);
                Publish(_supervisor.State, connected: true);
                var jobs = await _client.Service.GetJobsAsync(_closing.Token);
                foreach (var job in jobs)
                {
                    await OnJobChangedAsync(job);
                }

                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Publish(_supervisor.State, connected: false, $"Connecting to the worker… (attempt {attempt + 1})");
                _supervisor.EnsureRunning();
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), _closing.Token);
            }
        }

        Publish(_supervisor.State, connected: false, "Could not connect to the worker. See the logs in %LOCALAPPDATA%\\Leviathan\\logs.");
    }

    private async Task ReconnectAsync()
    {
        if (_closing.IsCancellationRequested || Interlocked.Exchange(ref _reconnecting, 1) == 1)
        {
            return;
        }

        try
        {
            Publish(_supervisor.State, connected: false, "Worker connection lost; reconnecting…");
            await Task.Delay(TimeSpan.FromMilliseconds(300), _closing.Token);
            await StartAsync();
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    private void Publish(SupervisedState state, bool connected, string? message = null)
    {
        var text = message ?? state switch
        {
            SupervisedState.Running when connected => $"Worker connected · {_client.Info?.AppVersion} · pid {_client.Info?.ProcessId}",
            SupervisedState.Running => "Worker running; connecting…",
            SupervisedState.Restarting => "Worker crashed; restarting…",
            SupervisedState.Disabled => $"Worker disabled: {_supervisor.DisabledReason}",
            _ => "Worker stopped",
        };
        var status = new WorkerStatus(state, connected, text);
        _ui.TryEnqueue(() => StatusChanged?.Invoke(this, status));
    }
}

/// <summary>One row in the jobs list.</summary>
public sealed class JobRow : INotifyPropertyChanged
{
    // Job palette (spec §12.6): 8 distinct colors; red is reserved for errors.
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x00, 0x67, 0xC0),
        Color.FromArgb(255, 0xC2, 0x5A, 0x00),
        Color.FromArgb(255, 0x6B, 0x4F, 0xBB),
        Color.FromArgb(255, 0x2A, 0x8A, 0x8A),
        Color.FromArgb(255, 0xA0, 0x45, 0x7A),
        Color.FromArgb(255, 0x5C, 0x7A, 0x1F),
        Color.FromArgb(255, 0x8A, 0x6D, 0x00),
        Color.FromArgb(255, 0x3B, 0x5B, 0x8C),
    ];

    private JobSnapshot _snapshot;

    internal JobRow(JobSnapshot snapshot) => _snapshot = snapshot;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => _snapshot.Id;

    public string Title => _snapshot.Title;

    public string State => _snapshot.State.ToString();

    public bool IsIndeterminate => _snapshot.State == JobState.Running && (_snapshot.BytesTotal <= 0 || _snapshot.ItemsTotal < 0);

    public double Percent => _snapshot.BytesTotal > 0 ? 100.0 * _snapshot.BytesDone / _snapshot.BytesTotal : _snapshot.State == JobState.Completed ? 100 : 0;

    public Brush ColorBrush => new SolidColorBrush(_snapshot.ColorIndex is >= 0 and < 8 ? Palette[_snapshot.ColorIndex] : Colors.Gray);

    public string Detail
    {
        get
        {
            var size = FormatBytes(_snapshot.BytesDone);
            var items = _snapshot.ItemsDone > 0 ? $"{_snapshot.ItemsDone:N0} files · " : string.Empty;
            return _snapshot.State switch
            {
                JobState.Queued or JobState.Waiting => _snapshot.WaitingReason ?? "Queued",
                JobState.Failed => _snapshot.Error ?? "Failed",
                _ => $"{items}{size}{(_snapshot.Error is null ? string.Empty : " · " + _snapshot.Error)}",
            };
        }
    }

    internal void Update(JobSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(Detail));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
