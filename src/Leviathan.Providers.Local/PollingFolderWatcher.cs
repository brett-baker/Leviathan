using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;

namespace Leviathan.Providers.Local;

/// <summary>
/// Watches network folders (UNC paths and mapped drives) by asking now and then for the folder's last-write time (spec §3.3, §3.1):
/// a single cheap stat, not a listing, so a visible share costs a few bytes a minute instead of the traffic the cache exists to
/// save. A changed time is reported once, and the view then lists the folder and applies the difference. While nothing changes the
/// gap between questions grows with <see cref="BackoffPolicy"/> (3 s, 6 s, 12 s … 60 s); a change brings it back to 3 s. A share that does not answer is not asked again at once either, and one that the health tracker
/// calls unreachable (or that the user works offline with) is not asked at all (<see cref="CanPoll"/>). Polling ends when the
/// subscription is disposed, which the view does as soon as the folder is no longer shown.
/// </summary>
/// <remarks>
/// A folder's time moves when items are added, removed or renamed in it; on most servers it does not move when an existing file's
/// contents change. Those edits show up on the next visit or refresh, the same as for a share that is not watched.
/// </remarks>
public sealed class PollingFolderWatcher : IFolderWatcher
{
    private readonly TimeProvider _time;

    /// <summary>Creates the watcher service.</summary>
    /// <param name="time">Clock for the waits.</param>
    public PollingFolderWatcher(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The waits between questions: 3 s doubling to 60 s, with a little jitter so several tabs do not ask in step.</summary>
    public BackoffPolicy Backoff { get; init; } = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60), 0.2);

    /// <summary>How long one question may take before the watcher gives up on it and backs off.</summary>
    public TimeSpan StatTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Decides whether a location is polled at all; replaced in tests.</summary>
    public Func<LocationPath, bool> IsPollable { get; init; } = static folder => IsFolderOnNetwork(folder);

    // A server on its own (\\nas) lists shares, not files: there is no folder to stat (it has no Win32 path), and the share list rarely changes.
    private static bool IsFolderOnNetwork(LocationPath folder) =>
        !(folder.Kind == LocationKind.Unc && folder.Share is null) && (folder.IsNetwork || MappedDrives.IsMappedDrive(folder));

    /// <summary>
    /// Decides, before each question, whether the server may be asked now: false while it is unreachable or the user works offline
    /// with it, so a dead share is never poked. The wait still passes, so polling resumes by itself.
    /// </summary>
    public Func<LocationPath, bool> CanPoll { get; init; } = static _ => true;

    /// <summary>Reads the folder's last-write time (UTC), or null when it cannot be read; replaced in tests.</summary>
    public Func<LocationPath, CancellationToken, Task<DateTime?>> ReadStamp { get; init; } = DefaultReadStamp;

    /// <inheritdoc />
    public IDisposable Watch(LocationPath folder, Action changed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(changed);
        if (!IsPollable(folder))
        {
            return Nothing.Instance;
        }

        var source = new CancellationTokenSource();
        Interlocked.Increment(ref _watching);
        _ = Task.Run(() => PollAsync(folder, changed, source.Token));
        return new Subscription(source, () => Interlocked.Decrement(ref _watching));
    }

    private int _watching;

    /// <summary>How many network folders are being watched now; the Background tab of the status hub shows it (spec §16.1).</summary>
    public int Watching => Volatile.Read(ref _watching);

    private async Task PollAsync(LocationPath folder, Action changed, CancellationToken ct)
    {
        try
        {
            var last = await AskAsync(folder, ct).ConfigureAwait(false);
            var quiet = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Backoff.GetDelay(quiet), _time, ct).ConfigureAwait(false);
                if (!CanPoll(folder))
                {
                    continue;
                }

                var now = await AskAsync(folder, ct).ConfigureAwait(false);
                if (now is null)
                {
                    quiet++;
                }
                else if (last is { } before && now != before)
                {
                    last = now;
                    quiet = 0;
                    changed();
                }
                else
                {
                    last ??= now;
                    quiet++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed: the folder is no longer shown.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a wait was starting.
        }
    }

    // One question at a time and never longer than StatTimeout: a stat on a share that hangs ties up a pool thread until Windows
    // gives up, and asking again meanwhile would only pile more of them up behind it.
    private Task<DateTime?>? _stuck;

    private async Task<DateTime?> AskAsync(LocationPath folder, CancellationToken ct)
    {
        if (_stuck is { IsCompleted: false })
        {
            return null;
        }

        var ask = Task.Run(() => ReadStamp(folder, ct), ct);
        try
        {
            var stamp = await ask.WaitAsync(StatTimeout, _time, ct).ConfigureAwait(false);
            _stuck = null;
            return stamp;
        }
        catch (TimeoutException)
        {
            _stuck = ask;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Task<DateTime?> DefaultReadStamp(LocationPath folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new DirectoryInfo(folder.ToWin32Path());
        return Task.FromResult<DateTime?>(info.Exists ? info.LastWriteTimeUtc : null);
    }

    private sealed class Subscription(CancellationTokenSource source, Action ended) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ended();
            source.Cancel();
            source.Dispose();
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
