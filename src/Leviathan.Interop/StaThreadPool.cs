using System.Collections.Concurrent;

namespace Leviathan.Interop;

/// <summary>
/// A few dedicated single-threaded-apartment threads for shell COM work, so it never runs on the UI thread or the thread
/// pool (spec §3.2 rule 3). Work queued with <see cref="RunAsync{T}"/> starts only if it has not been cancelled by then.
/// </summary>
public sealed class StaThreadPool : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly List<Thread> _threads = [];

    /// <summary>Starts <paramref name="threads"/> STA threads.</summary>
    public StaThreadPool(int threads, string name = "Leviathan STA")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        for (var i = 0; i < threads; i++)
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = $"{name} {i + 1}" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _threads.Add(thread);
        }
    }

    /// <summary>Runs <paramref name="work"/> on an STA thread. A request cancelled before it reaches the front of the queue never runs.</summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetCanceled(cancellationToken);
            return completion.Task;
        }

        try
        {
            _queue.Add(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(StaThreadPool)));
        }

        return completion.Task;
    }

    /// <inheritdoc />
    public void Dispose() => _queue.CompleteAdding();

    private void Loop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }
}
