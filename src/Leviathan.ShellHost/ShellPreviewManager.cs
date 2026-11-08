using System.Collections.Concurrent;
using Leviathan.Contracts;
using Leviathan.Interop;

namespace Leviathan.ShellHost;

/// <summary>A preview handler showing one file. All members run on the thread that made it.</summary>
internal interface IShellPreviewInstance : IDisposable
{
    /// <summary>The handler's CLSID.</summary>
    string Handler { get; }

    /// <summary>Moves and resizes the preview window.</summary>
    void Move(ShellRect bounds);
}

/// <summary>Makes preview windows. Seam over the Win32 and COM code so the manager's limits and clean-up are tested without handlers.</summary>
internal interface IShellPreviewSource
{
    /// <summary>
    /// Creates the preview window and loads the handler; called on the thread that will own it. Returns null, with
    /// <paramref name="error"/> set, when the file has no handler or the handler refuses it.
    /// </summary>
    IShellPreviewInstance? Open(ShellPreviewRequest request, out string? error);
}

/// <summary>
/// Keeps the open shell previews of the host (spec §9.1). Each preview lives on a thread of its own with a message loop, because the
/// handler's objects and its window must be used where they were made. A call that outlives its limit means a handler is stuck: the
/// preview is written off, the caller gets a <see cref="TimeoutException"/>, and enough of those raise <see cref="Unhealthy"/> so the
/// host restarts (spec §3.4). At most <c>maxOpen</c> previews exist; opening another closes the oldest, because the pane shows one
/// file at a time. A preview is closed by the UI, by the host closing, or when its owner window is gone.
/// </summary>
internal sealed class ShellPreviewManager : IDisposable
{
    private readonly IShellPreviewSource _source;
    private readonly TimeProvider _time;
    private readonly int _maxOpen;
    private readonly int _maxAbandoned;
    private readonly ConcurrentDictionary<Guid, Entry> _open = new();
    private int _abandoned;

    public ShellPreviewManager(IShellPreviewSource source, TimeProvider? time = null, int maxOpen = 2, int maxAbandoned = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _time = time ?? TimeProvider.System;
        _maxOpen = maxOpen;
        _maxAbandoned = maxAbandoned;
    }

    /// <summary>How long loading a preview may take; a cold Office handler can need several seconds.</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long moving or closing a preview may take.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised once too many previews have hung. The host should exit and be restarted.</summary>
    public event EventHandler? Unhealthy;

    public int OpenCount => _open.Count;

    public int Abandoned => Volatile.Read(ref _abandoned);

    public async Task<ShellPreviewResult> OpenAsync(ShellPreviewRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        while (_open.Count >= _maxOpen && _open.OrderBy(e => e.Value.Opened).FirstOrDefault() is { Value: { } oldest })
        {
            await CloseAsync(oldest.Id).ConfigureAwait(false);
        }

        var entry = new Entry(new StaMessageThread("Leviathan shell preview"), _time.GetUtcNow());
        try
        {
            string? error = null;
            entry.Instance = await RunAsync(entry, () => _source.Open(request, out error), OpenTimeout, cancellationToken).ConfigureAwait(false);
            if (entry.Instance is null)
            {
                Release(entry);
                return new ShellPreviewResult(Guid.Empty, null, error ?? "No preview is available for this file.");
            }
        }
        catch
        {
            Release(entry);
            throw;
        }

        _open[entry.Id] = entry;
        return new ShellPreviewResult(entry.Id, entry.Instance.Handler, null);
    }

    public async Task MoveAsync(Guid previewId, ShellRect bounds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (!_open.TryGetValue(previewId, out var entry) || entry.Dead)
        {
            return;
        }

        await RunAsync(entry, () =>
        {
            entry.Instance!.Move(bounds);
            return 0;
        }, CallTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(Guid previewId)
    {
        if (!_open.TryRemove(previewId, out var entry))
        {
            return;
        }

        // Release the handler where it was made; the thread then ends. Not awaited past a limit: a stuck handler must not hold the caller.
        if (entry.Instance is { } instance && !entry.Dead)
        {
            try
            {
                await entry.Thread.RunAsync(() =>
                {
                    instance.Dispose();
                    return 0;
                }).WaitAsync(CallTimeout, _time).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException)
            {
                WriteOff(entry);
            }
        }

        entry.Thread.Dispose();
    }

    public void Dispose()
    {
        foreach (var entry in _open.Values)
        {
            entry.Thread.Dispose();
        }

        _open.Clear();
    }

    private static void Validate(ShellPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathFullyQualified(request.Path))
        {
            throw new ArgumentException($"'{request.Path}' is not a full path.", nameof(request));
        }

        if (request.Bounds is not { Width: > 0 and <= 20000, Height: > 0 and <= 20000 })
        {
            throw new ArgumentException("A preview needs a size.", nameof(request));
        }
    }

    private async Task<T> RunAsync<T>(Entry entry, Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await entry.Thread.RunAsync(work, CancellationToken.None).WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            WriteOff(entry);
            throw new TimeoutException($"A preview handler did not answer within {timeout.TotalSeconds:0.#} s.");
        }
    }

    private void WriteOff(Entry entry)
    {
        if (entry.Dead)
        {
            return;
        }

        entry.Dead = true;
        _open.TryRemove(entry.Id, out _);
        entry.Thread.Dispose();
        if (Interlocked.Increment(ref _abandoned) >= _maxAbandoned)
        {
            Unhealthy?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Release(Entry entry)
    {
        _open.TryRemove(entry.Id, out _);
        entry.Thread.Dispose();
    }

    private sealed class Entry(StaMessageThread thread, DateTimeOffset opened)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public StaMessageThread Thread { get; } = thread;

        public IShellPreviewInstance? Instance { get; set; }

        public DateTimeOffset Opened { get; } = opened;

        public volatile bool Dead;
    }
}
