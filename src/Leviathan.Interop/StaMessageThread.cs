using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Leviathan.Interop;

/// <summary>
/// One single-threaded-apartment thread with a message loop. Shell extensions assume the thread that holds their COM objects
/// pumps messages: handlers create hidden windows, a verb that opens a dialog needs its loop to keep running after
/// <c>InvokeCommand</c> returns, and COM delivers calls into the apartment through it. The thread also runs the work queued with
/// <see cref="RunAsync{T}"/>, in order, so an object created by one call can be used by the next (spec §3.2). The idea follows
/// Files' <c>ThreadWithMessageQueue</c> (MIT); this one is written against Leviathan's pointer-style bindings.
/// </summary>
public sealed unsafe class StaMessageThread : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly string _name;
    private HANDLE _wake;
    private volatile bool _stop;
    private int _disposed;

    /// <summary>Starts the thread.</summary>
    /// <param name="name">Thread name, shown in crash reports and debuggers.</param>
    public StaMessageThread(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _wake = PInvoke.CreateEvent(null, false, false, (PCWSTR)null);
        if (_wake.IsNull)
        {
            throw new InvalidOperationException("Could not create the wake-up event for a shell thread.");
        }

        var thread = new Thread(Loop) { IsBackground = true, Name = _name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread after everything queued before it. Cancelling before it starts means it never
    /// runs; cancelling after only stops the caller waiting.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stop)
        {
            completion.SetException(new ObjectDisposedException(nameof(StaMessageThread)));
            return completion.Task;
        }

        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _queue.Enqueue(() =>
        {
            try
            {
                if (completion.Task.IsCompleted)
                {
                    return;
                }

                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });
        PInvoke.SetEvent(_wake);
        return completion.Task;
    }

    /// <summary>
    /// Asks the thread to finish what is queued and exit. It does not wait: a handler stuck on the thread would hold the caller
    /// too. The thread is a background thread, so a stuck one dies with the process.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop = true;
        PInvoke.SetEvent(_wake);
    }

    private void Loop()
    {
        try
        {
            while (true)
            {
                while (_queue.TryDequeue(out var action))
                {
                    action();
                }

                if (_stop && _queue.IsEmpty)
                {
                    break;
                }

                var wake = _wake;
                PInvoke.MsgWaitForMultipleObjectsEx(1, &wake, 0xFFFFFFFF, QUEUE_STATUS_FLAGS.QS_ALLINPUT, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);

                MSG message;
                while (PInvoke.PeekMessage(&message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                {
                    PInvoke.TranslateMessage(&message);
                    PInvoke.DispatchMessage(&message);
                }
            }
        }
        finally
        {
            PInvoke.CloseHandle(_wake);
            _wake = default;
        }
    }
}
