using Leviathan.Core.Platform;

namespace Leviathan.Playback;

/// <summary>
/// Keeps a player ready before it is asked for (spec §3.1: video preview start under 300 ms, "the engine is pre-warmed while the
/// preview pane is open"; spec §10.1: "the next file is pre-opened and buffered in a second, hidden player instance"). Starting a
/// player is the slow part of showing a video, so one spare is made in the background and handed out on request, and the next file
/// of a queue can be opened paused in a hidden player and taken over when the user gets there. The pool owns what it made until it
/// hands it out; whoever rents an engine disposes it or gives it back. Nothing here throws for a failed warm-up: the next rent just
/// makes a player the slow way.
/// </summary>
/// <param name="create">Makes a new, idle engine.</param>
public sealed class EnginePool(Func<CancellationToken, Task<IVideoEngine>> create) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _closing = new();
    private Task<IVideoEngine?>? _spare;
    private Preload? _preload;
    private bool _disposed;

    private sealed record Preload(string Path, Task<IVideoEngine?> Engine, CancellationTokenSource Cancel);

    /// <summary>True when a spare player has been made or is being made.</summary>
    public bool HasSpare
    {
        get
        {
            lock (_lock)
            {
                return _spare is not null;
            }
        }
    }

    /// <summary>The path being preloaded or ready, or null.</summary>
    public string? PreloadedPath
    {
        get
        {
            lock (_lock)
            {
                return _preload?.Path;
            }
        }
    }

    /// <summary>Makes sure a spare player exists, starting one in the background if not. Returns when it is ready.</summary>
    public async Task WarmAsync()
    {
        Task<IVideoEngine?> spare;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            spare = _spare ??= CreateQuietlyAsync();
        }

        if (await spare.ConfigureAwait(false) is null)
        {
            // It failed: there is no spare after all, and the next warm-up or rent tries again.
            lock (_lock)
            {
                if (ReferenceEquals(_spare, spare))
                {
                    _spare = null;
                }
            }
        }
    }

    /// <summary>A player, the spare if there is one (and then another spare is started), otherwise a new one.</summary>
    public async Task<IVideoEngine> RentAsync(CancellationToken cancellationToken)
    {
        Task<IVideoEngine?>? spare;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            spare = _spare;
            _spare = null;
        }

        var engine = spare is null ? null : await spare.WaitAsync(cancellationToken).ConfigureAwait(false);
        engine ??= await create(cancellationToken).ConfigureAwait(false);
        _ = WarmAsync();
        return engine;
    }

    /// <summary>Gives a player back: it is closed and kept as the spare when there is none, otherwise disposed.</summary>
    public async Task ReturnAsync(IVideoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        try
        {
            await engine.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            return;
        }

        lock (_lock)
        {
            if (!_disposed && _spare is null)
            {
                _spare = Task.FromResult<IVideoEngine?>(engine);
                return;
            }
        }

        await engine.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens <paramref name="path"/> paused at <paramref name="start"/> in a hidden player, so that taking it over later is instant. A
    /// second call for the same path does nothing; for another path it throws the first away. Returns when the file is open.
    /// </summary>
    public Task PreloadAsync(string path, TimeSpan start)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Preload? replaced = null;
        Preload fresh;
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_preload is { } current && string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return current.Engine;
            }

            replaced = _preload;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            fresh = new Preload(path, OpenHiddenAsync(path, start, cancel.Token), cancel);
            _preload = fresh;
        }

        if (replaced is not null)
        {
            _ = DiscardAsync(replaced);
        }

        return fresh.Engine;
    }

    /// <summary>
    /// The hidden player that has <paramref name="path"/> open, waiting for it to finish opening, or null when that file was not
    /// preloaded or could not be opened. It is no longer the pool's: the caller disposes it or returns it.
    /// </summary>
    public async Task<IVideoEngine?> TakePreloadedAsync(string path)
    {
        Preload? taken;
        lock (_lock)
        {
            taken = _preload is { } p && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase) ? p : null;
            if (taken is not null)
            {
                _preload = null;
            }
        }

        if (taken is null)
        {
            return null;
        }

        var engine = await taken.Engine.ConfigureAwait(false);
        taken.Cancel.Dispose();
        return engine;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<IVideoEngine?>? spare;
        Preload? preload;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            spare = _spare;
            preload = _preload;
            _spare = null;
            _preload = null;
        }

        await _closing.CancelAsync().ConfigureAwait(false);
        if (spare is not null && await spare.ConfigureAwait(false) is { } engine)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        if (preload is not null)
        {
            await DiscardAsync(preload).ConfigureAwait(false);
        }

        _closing.Dispose();
    }

    private async Task<IVideoEngine?> CreateQuietlyAsync()
    {
        try
        {
            return await create(_closing.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A warm-up that failed (no video engine, cancelled by dispose) costs nothing: the next rent makes one the slow way.
            return null;
        }
    }

    private async Task<IVideoEngine?> OpenHiddenAsync(string path, TimeSpan start, CancellationToken cancellationToken)
    {
        IVideoEngine? engine = null;
        try
        {
            engine = await RentAsync(cancellationToken).ConfigureAwait(false);
            await engine.OpenAsync(path, start, autoplay: false, cancellationToken).ConfigureAwait(false);
            if (engine.Snapshot.State == PlaybackState.Failed)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return engine;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            if (engine is not null)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }

            return null;
        }
    }

    private static async Task DiscardAsync(Preload preload)
    {
        await preload.Cancel.CancelAsync().ConfigureAwait(false);
        if (await preload.Engine.ConfigureAwait(false) is { } engine)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        preload.Cancel.Dispose();
    }
}
