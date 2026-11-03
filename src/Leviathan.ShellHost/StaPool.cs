using System.Collections.Concurrent;

namespace Leviathan.ShellHost;

/// <summary>Counters of a <see cref="StaPool"/>.</summary>
/// <param name="Size">Threads the pool keeps available.</param>
/// <param name="Busy">Threads running a request.</param>
/// <param name="Queued">Requests waiting for a thread.</param>
/// <param name="Completed">Requests that returned.</param>
/// <param name="Failed">Requests that threw.</param>
/// <param name="TimedOut">Requests given up on after their time limit.</param>
/// <param name="Abandoned">Threads still stuck inside a request that timed out.</param>
internal sealed record StaPoolStatus(int Size, int Busy, int Queued, long Completed, long Failed, long TimedOut, int Abandoned);

/// <summary>
/// A pool of single-threaded-apartment threads for shell COM objects (spec §3.2: shell COM runs on STA threads, never on the UI
/// thread). Every request has a time limit. A thread that is still inside a request when the limit passes is stuck in a shell
/// extension: it cannot be killed safely, so it is written off, the caller gets a <see cref="TimeoutException"/>, and a fresh
/// thread takes its place. Too many written-off threads raise <see cref="Unhealthy"/>, and the host exits so the supervisor can
/// start a clean one (spec §3.4).
/// </summary>
internal sealed class StaPool : IDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = [];
    private readonly TimeProvider _time;
    private readonly string _name;
    private readonly int _size;
    private readonly int _maxAbandoned;
    private int _busy;
    private int _abandoned;
    private int _threadNumber;
    private long _completed;
    private long _failed;
    private long _timedOut;
    private volatile bool _disposed;

    /// <summary>Creates the pool and starts its threads.</summary>
    /// <param name="size">Threads to keep available.</param>
    /// <param name="time">Clock for time limits.</param>
    /// <param name="name">Thread name prefix.</param>
    /// <param name="maxAbandoned">Written-off threads that make the pool <see cref="Unhealthy"/>.</param>
    public StaPool(int size, TimeProvider? time = null, string name = "Leviathan.Sta", int maxAbandoned = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAbandoned, 1);
        _size = size;
        _time = time ?? TimeProvider.System;
        _name = name;
        _maxAbandoned = maxAbandoned;
        for (var i = 0; i < size; i++)
        {
            StartThread();
        }
    }

    /// <summary>Raised once too many threads are stuck. The host should exit and be restarted.</summary>
    public event EventHandler? Unhealthy;

    /// <summary>The counters right now.</summary>
    public StaPoolStatus Status => new(
        _size, Volatile.Read(ref _busy), _queue.Count, Interlocked.Read(ref _completed), Interlocked.Read(ref _failed),
        Interlocked.Read(ref _timedOut), Volatile.Read(ref _abandoned));

    /// <summary>
    /// Runs <paramref name="work"/> on a pool thread. The limit counts from when a thread starts the work, not from when it was
    /// queued. Cancelling stops the caller waiting; work already running carries on until it returns.
    /// </summary>
    /// <exception cref="TimeoutException">The work ran longer than <paramref name="timeout"/>.</exception>
    public Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var item = new WorkItem<T>(this, work, timeout, cancellationToken);
        _queue.Add(item, CancellationToken.None);
        return item.Task;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;

        // Threads leave when they finish what they are doing; a stuck one is a background thread and dies with the process.
        _queue.CompleteAdding();
    }

    private void StartThread()
    {
        var slot = new Slot();
        var thread = new Thread(() => Loop(slot))
        {
            IsBackground = true,
            Name = $"{_name}-{Interlocked.Increment(ref _threadNumber)}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Loop(Slot slot)
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (!item.TryBegin())
            {
                continue;
            }

            Interlocked.Increment(ref _busy);
            try
            {
                item.Run(slot);
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }

            if (slot.WrittenOff)
            {
                // The request outlived its limit and was replaced while this thread was stuck. It is free again; let it go.
                Interlocked.Decrement(ref _abandoned);
                return;
            }
        }
    }

    private void WriteOff(Slot slot)
    {
        slot.WrittenOff = true;
        Interlocked.Increment(ref _timedOut);
        var abandoned = Interlocked.Increment(ref _abandoned);
        if (!_disposed)
        {
            StartThread();
        }

        if (abandoned >= _maxAbandoned)
        {
            Unhealthy?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Slot
    {
        public volatile bool WrittenOff;
    }

    private interface IWorkItem
    {
        /// <summary>False when the caller cancelled while the request was still queued.</summary>
        bool TryBegin();

        void Run(Slot slot);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Done = 2;

        private readonly StaPool _pool;
        private readonly Func<T> _work;
        private readonly TimeSpan _timeout;
        private readonly TaskCompletionSource<T> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancel;
        private int _state;

        public WorkItem(StaPool pool, Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _pool = pool;
            _work = work;
            _timeout = timeout;
            _cancel = cancellationToken.Register(() =>
            {
                // Queued: it never starts. Running: the caller stops waiting and the thread finishes in its own time.
                Interlocked.CompareExchange(ref _state, Done, Queued);
                _result.TrySetCanceled(cancellationToken);
            });
        }

        public Task<T> Task => _result.Task;

        public bool TryBegin() => Interlocked.CompareExchange(ref _state, Running, Queued) == Queued;

        public void Run(Slot slot)
        {
            using var limit = _pool._time.CreateTimer(
                _ =>
                {
                    if (Interlocked.CompareExchange(ref _state, Done, Running) == Running)
                    {
                        _pool.WriteOff(slot);
                        _result.TrySetException(new TimeoutException($"The shell did not answer within {_timeout.TotalSeconds:0.#} s."));
                    }
                },
                null,
                _timeout,
                Timeout.InfiniteTimeSpan);

            try
            {
                var value = _work();
                if (Interlocked.CompareExchange(ref _state, Done, Running) == Running)
                {
                    Interlocked.Increment(ref _pool._completed);
                    _result.TrySetResult(value);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.CompareExchange(ref _state, Done, Running) == Running)
                {
                    Interlocked.Increment(ref _pool._failed);
                    _result.TrySetException(ex);
                }
            }
            finally
            {
                _cancel.Dispose();
            }
        }
    }
}
