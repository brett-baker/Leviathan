namespace Leviathan.Diagnostics;

/// <summary>Posts work to the UI thread. Implemented over WinUI's DispatcherQueue in the app.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="callback"/> on the UI thread. Returns false if the queue is shut down.</summary>
    bool TryEnqueue(Action callback);
}

/// <summary>A detected UI stall.</summary>
/// <param name="Duration">How long the UI thread did not respond.</param>
/// <param name="IsHang">True when the stall passed the hang threshold.</param>
public sealed record UiStall(TimeSpan Duration, bool IsHang);

/// <summary>
/// Detects UI-thread stalls from a background thread (spec §3.2, §20.2): it posts a heartbeat to the UI thread every
/// <see cref="Interval"/> and measures how long it takes to run. Stalls over <see cref="StallThreshold"/> (50 ms) are
/// reported for the debug log and the UI tests; stalls over <see cref="HangThreshold"/> (5 s) are reported once as a hang
/// while still stuck, so a stack and minidump can be captured before the user kills the app.
/// </summary>
public sealed class UiStallWatchdog : IAsyncDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Creates the watchdog.</summary>
    public UiStallWatchdog(IUiDispatcher dispatcher, TimeProvider? time = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Heartbeat interval.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Stalls longer than this are reported.</summary>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Stalls longer than this are reported as hangs (once per hang, while it is happening).</summary>
    public TimeSpan HangThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised from the watchdog thread when a stall ends (or reaches the hang threshold).</summary>
    public event EventHandler<UiStall>? StallDetected;

    /// <summary>Number of stalls observed since start.</summary>
    public int StallCount { get; private set; }

    /// <summary>Starts watching.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _stop.Dispose();
    }

    /// <summary>Runs one heartbeat and returns how long the UI thread took to process it (null when cancelled).</summary>
    internal async Task<TimeSpan?> BeatAsync(CancellationToken cancellationToken)
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = _time.GetTimestamp();
        if (!_dispatcher.TryEnqueue(() => processed.TrySetResult()))
        {
            return null;
        }

        var hangReported = false;
        while (!processed.Task.IsCompleted)
        {
            var waited = _time.GetElapsedTime(posted);
            if (!hangReported && waited >= HangThreshold)
            {
                hangReported = true;
                StallCount++;
                StallDetected?.Invoke(this, new UiStall(waited, IsHang: true));
            }

            var next = hangReported ? TimeSpan.FromMilliseconds(250) : HangThreshold - waited;
            var finished = await Task.WhenAny(processed.Task, Task.Delay(next, _time, cancellationToken)).ConfigureAwait(false);
            if (finished != processed.Task && cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        var duration = _time.GetElapsedTime(posted);
        if (!hangReported && duration > StallThreshold)
        {
            StallCount++;
            StallDetected?.Invoke(this, new UiStall(duration, IsHang: false));
        }

        return duration;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await BeatAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                return;
            }

            await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
