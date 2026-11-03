using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.Folders;

/// <summary>The cache the file view asks for thumbnails (spec §3.1: under 200 ms from cache, under 1 s generated).</summary>
public interface IThumbnailCache
{
    /// <summary>Gets a thumbnail, generating it when needed. Cancelling (the item scrolled off screen) stops waiting and, when nobody else waits, stops the work.</summary>
    Task<ThumbnailImage?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken);

    /// <summary>Returns the thumbnail only when it is already in memory.</summary>
    bool TryGetCached(ThumbnailRequest request, out ThumbnailImage? image);

    /// <summary>Forgets every thumbnail below <paramref name="folder"/> (Shift+F5, spec §5.1).</summary>
    void InvalidateFolder(LocationPath folder);

    /// <summary>Empties the cache.</summary>
    void Clear();
}

/// <summary>The state of the thumbnail cache.</summary>
/// <param name="Cached">Entries held, including remembered "no thumbnail" results.</param>
/// <param name="Bytes">Pixel bytes held.</param>
/// <param name="Capacity">The byte budget.</param>
/// <param name="Generating">Thumbnails being made right now.</param>
/// <param name="Failed">Files whose thumbnail failed and is not retried until they change.</param>
public sealed record ThumbnailStats(int Cached, long Bytes, long Capacity, int Generating, int Failed);

/// <summary>
/// An in-memory thumbnail cache with a byte budget (spec §2.4, "size-capped LRU"). Concurrent requests for the same image share
/// one generation; at most <c>maxConcurrent</c> generations run at once so fast scrolling cannot flood the shell; a request
/// cancelled before it starts never runs; a file whose thumbnail failed is not retried until it changes (spec §3.4).
/// </summary>
public sealed class ThumbnailCache : IThumbnailCache, IDisposable
{
    /// <summary>Default memory budget.</summary>
    public const long DefaultCapacityBytes = 64L * 1024 * 1024;

    private readonly IThumbnailSource _source;
    private readonly long _capacity;
    private readonly SemaphoreSlim _gate;
    private readonly Lock _lock = new();
    private readonly Dictionary<ThumbnailRequest, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<ThumbnailRequest, InFlight> _inFlight = [];
    private readonly HashSet<ThumbnailRequest> _failed = [];

    /// <summary>Creates a cache over <paramref name="source"/>.</summary>
    public ThumbnailCache(IThumbnailSource source, long capacityBytes = DefaultCapacityBytes, int maxConcurrent = 4)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        _source = source;
        _capacity = capacityBytes;
        _gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>Bytes of pixel data held.</summary>
    public long CachedBytes { get; private set; }

    /// <summary>What the cache holds and is doing now, for the Background tab (spec 16.1).</summary>
    public ThumbnailStats Stats
    {
        get
        {
            lock (_lock)
            {
                return new ThumbnailStats(_entries.Count, CachedBytes, _capacity, _inFlight.Count, _failed.Count);
            }
        }
    }

    /// <summary>Number of cached entries (including remembered "no thumbnail" results).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetCached(ThumbnailRequest request, out ThumbnailImage? image)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(request, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<ThumbnailImage?> GetAsync(ThumbnailRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCached(request, out var cached))
        {
            return cached;
        }

        InFlight flight;
        lock (_lock)
        {
            if (_failed.Contains(request))
            {
                return null;
            }

            if (!_inFlight.TryGetValue(request, out flight!))
            {
                flight = new InFlight();
                _inFlight[request] = flight;
                flight.Task = Task.Run(() => GenerateAsync(request, flight), CancellationToken.None);
            }

            flight.Waiters++;
        }

        try
        {
            return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                flight.Waiters--;
                if (flight.Waiters == 0)
                {
                    // Nobody wants it any more: stop the work and let a later request start afresh.
                    flight.Cancel.Cancel();
                    if (_inFlight.TryGetValue(request, out var current) && current == flight)
                    {
                        _inFlight.Remove(request);
                    }
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    public void InvalidateFolder(LocationPath folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        lock (_lock)
        {
            foreach (var key in _entries.Keys.Where(k => folder.Contains(k.Location)).ToList())
            {
                Remove(key);
            }

            _failed.RemoveWhere(k => folder.Contains(k.Location));
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
            _failed.Clear();
            CachedBytes = 0;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<ThumbnailImage?> GenerateAsync(ThumbnailRequest request, InFlight flight)
    {
        var token = flight.Cancel.Token;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var image = await _source.CreateAsync(request, token).ConfigureAwait(false);
                Store(request, image);
                return image;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A thumbnailer that fails for this exact file version is not asked again (spec §3.4).
            lock (_lock)
            {
                _failed.Add(request);
            }

            return null;
        }
        finally
        {
            lock (_lock)
            {
                if (_inFlight.TryGetValue(request, out var current) && current == flight)
                {
                    _inFlight.Remove(request);
                }
            }
        }
    }

    private void Store(ThumbnailRequest request, ThumbnailImage? image)
    {
        lock (_lock)
        {
            if (_entries.ContainsKey(request))
            {
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(request, image));
            _lru.AddFirst(node);
            _entries[request] = node;
            CachedBytes += image?.SizeBytes ?? 0;

            // Evict least-recently-used entries until under budget, but never the one just added.
            while (CachedBytes > _capacity && _lru.Count > 1)
            {
                Remove(_lru.Last!.Value.Request);
            }
        }
    }

    private void Remove(ThumbnailRequest key)
    {
        if (_entries.Remove(key, out var node))
        {
            _lru.Remove(node);
            CachedBytes -= node.Value.Image?.SizeBytes ?? 0;
        }
    }

    private sealed record Entry(ThumbnailRequest Request, ThumbnailImage? Image);

    private sealed class InFlight
    {
        public CancellationTokenSource Cancel { get; } = new();

        public Task<ThumbnailImage?> Task { get; set; } = null!;

        public int Waiters { get; set; }
    }
}
