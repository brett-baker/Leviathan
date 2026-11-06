using System.Collections.Concurrent;

namespace Leviathan.Media;

/// <summary>
/// The one place that answers "what is in this file" (spec §9.3, D16): from the cache when this exact version of the file has been
/// seen, otherwise by running ffprobe, parsing, and remembering the answer. It keeps three promises:
/// <list type="bullet">
/// <item>a file is probed once at a time however many callers ask (the grid, the preview pane and a sort can all want it);</item>
/// <item>only a few probes run together, so ffprobe never competes with the UI or a copy for the disk;</item>
/// <item>a file that fails, times out or kills the process is remembered as unreadable until it changes, and a probe that was running
/// when the process died is held against that file, not retried (so no restart loop).</item>
/// </list>
/// Cancelling every caller of a probe that has not started yet cancels it; one that is running finishes on its own 10 s limit and is cached.
/// </summary>
public sealed class MediaMetadataService : IDisposable
{
    /// <summary>The message stored for a file whose probe never finished because the process died.</summary>
    public const string InterruptedReason = "The media process stopped while reading this file.";

    private readonly FfprobeRunner _probe;
    private readonly IMediaMetadataStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, InFlight> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the service. <paramref name="maxParallel"/> probes run at once (2 by default: ffprobe is light, disks and NAS links are not).</summary>
    public MediaMetadataService(FfprobeRunner probe, IMediaMetadataStore store, TimeProvider? time = null, int maxParallel = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);
        _probe = probe;
        _store = store;
        _time = time ?? TimeProvider.System;
        _slots = new SemaphoreSlim(maxParallel, maxParallel);
    }

    /// <summary>Releases the probe slots; probes still running finish on their own.</summary>
    public void Dispose() => _slots.Dispose();

    /// <summary>Raised after a probe finishes and its answer is stored (not for answers read from the cache).</summary>
    public event EventHandler<MediaMetadataEntry>? Probed;

    /// <summary>Call once when the process starts: a probe that was running when the last process died is quarantined. Returns how many.</summary>
    public int RecoverFromCrash() => _store.QuarantineInterrupted(_time.GetUtcNow(), InterruptedReason);

    /// <summary>The cached answer for this version of the file, or null when it has not been probed (never starts a probe).</summary>
    public MediaMetadataEntry? Peek(MediaFileKey key) => _store.Get(key) is { State: MetadataState.Ok or MetadataState.Unreadable } entry ? entry : null;

    /// <summary>Forgets what is known about a path, so the next request probes it again ("retry this file").</summary>
    public bool Retry(string path) => _store.Forget(path);

    /// <summary>The files currently marked unreadable.</summary>
    public IReadOnlyList<MediaMetadataEntry> UnreadableFiles() => _store.Unreadable();

    /// <summary>
    /// The metadata of <paramref name="key"/>. <paramref name="isNetwork"/> limits how much of a network file is read first; a network
    /// file that fails under those limits is tried once more without them before it is called unreadable (an MP4 with its index at the end).
    /// </summary>
    public async Task<MediaMetadataEntry> GetAsync(MediaFileKey key, bool isNetwork, CancellationToken cancellationToken)
    {
        if (Peek(key) is { } cached)
        {
            return cached;
        }

        var flight = Join(key, isNetwork);
        try
        {
            return await flight.Task!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Leave(flight);
            throw;
        }
    }

    private InFlight Join(MediaFileKey key, bool isNetwork)
    {
        while (true)
        {
            var flight = _inFlight.GetOrAdd(key.Id, _ => new InFlight(key));
            lock (flight)
            {
                if (flight.Cancelled)
                {
                    // Everyone left before it started and it was cancelled; the next caller starts a fresh one.
                    _inFlight.TryRemove(new KeyValuePair<string, InFlight>(key.Id, flight));
                    continue;
                }

                flight.Waiters++;
                flight.Task ??= Task.Run(() => RunAsync(flight, isNetwork), CancellationToken.None);
                return flight;
            }
        }
    }

    private static void Leave(InFlight flight)
    {
        lock (flight)
        {
            if (--flight.Waiters == 0 && !flight.Started)
            {
                flight.Cancelled = true;
                try
                {
                    flight.Source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // It finished in the same instant; nothing left to cancel.
                }
            }
        }
    }

    private async Task<MediaMetadataEntry> RunAsync(InFlight flight, bool isNetwork)
    {
        var key = flight.Key;
        var slotHeld = false;
        try
        {
            await _slots.WaitAsync(flight.Source.Token).ConfigureAwait(false);
            slotHeld = true;
            lock (flight)
            {
                flight.Started = true;
            }

            _store.MarkProbing(key, _time.GetUtcNow());
            var result = await _probe.ProbeAsync(key.Path, isNetwork, CancellationToken.None).ConfigureAwait(false);
            if (isNetwork && result.Status == ProbeStatus.Failed)
            {
                // The header-only limits may have been too small for this file; once, without them.
                result = await _probe.ProbeAsync(key.Path, isNetwork: false, CancellationToken.None).ConfigureAwait(false);
            }

            var entry = result.Status switch
            {
                ProbeStatus.Ok when FfprobeParser.TryParse(result.Json!) is { } info => new MediaMetadataEntry(key, MetadataState.Ok, info, null, _time.GetUtcNow()),
                ProbeStatus.Ok => new MediaMetadataEntry(key, MetadataState.Unreadable, null, "ffprobe's answer could not be read.", _time.GetUtcNow()),
                ProbeStatus.ToolMissing => new MediaMetadataEntry(key, MetadataState.ToolMissing, null, result.Message, _time.GetUtcNow()),
                _ => new MediaMetadataEntry(key, MetadataState.Unreadable, null, result.Message, _time.GetUtcNow()),
            };

            if (entry.State == MetadataState.ToolMissing)
            {
                _store.Forget(key.Path);
            }
            else
            {
                _store.Put(entry);
                RaiseProbed(entry);
            }

            return entry;
        }
        catch (OperationCanceledException)
        {
            // Cancelled before it started: nothing was marked.
            throw;
        }
        catch
        {
            // The store or the parser failed: leave no "probing" mark behind that the next start would hold against the file.
            _store.Forget(key.Path);
            throw;
        }
        finally
        {
            if (slotHeld)
            {
                try
                {
                    _slots.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The service was disposed while this probe was finishing.
                }
            }

            _inFlight.TryRemove(new KeyValuePair<string, InFlight>(key.Id, flight));
            flight.Source.Dispose();
        }
    }

    // A listener that throws must not undo the answer that was just stored.
    private void RaiseProbed(MediaMetadataEntry entry)
    {
        try
        {
            Probed?.Invoke(this, entry);
        }
        catch (Exception)
        {
            // Not the service's problem.
        }
    }

    private sealed class InFlight(MediaFileKey key)
    {
        public MediaFileKey Key { get; } = key;

        public CancellationTokenSource Source { get; } = new();

        public Task<MediaMetadataEntry>? Task { get; set; }

        public int Waiters { get; set; }

        public bool Started { get; set; }

        public bool Cancelled { get; set; }
    }
}
