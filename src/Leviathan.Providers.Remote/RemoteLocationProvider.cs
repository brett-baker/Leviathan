using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Providers.Remote;

/// <summary>
/// Lists folders on SFTP, FTP and FTPS servers (spec §7.1) as an <see cref="ILocationProvider"/>, so a server's folders open in a tab like any other. One
/// session is kept per server and reused; a session that fails is dropped and the call tried once more on a new one when the failure may be passing; every
/// failure leaves as a classified <see cref="LocationException"/> (spec §3.3), so a dead server fails its own tab with a clear message and nothing else.
/// </summary>
public sealed class RemoteLocationProvider : ILocationProvider, IAsyncDisposable
{
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);

    private readonly IReadOnlyDictionary<RemoteProtocol, IRemoteSessionFactory> _factories;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Pooled> _pool = [];
    private readonly Lock _lock = new();

    /// <summary>Creates the provider with a session factory for each protocol it serves.</summary>
    public RemoteLocationProvider(IReadOnlyDictionary<RemoteProtocol, IRemoteSessionFactory> factories, TimeProvider? time = null)
    {
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "remote";

    /// <inheritdoc />
    public bool CanHandle(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps && _factories.ContainsKey(ProtocolOf(location.Kind));
    }

    /// <summary>The server a location is on.</summary>
    public static RemoteEndpoint EndpointOf(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var protocol = ProtocolOf(location.Kind);
        return new RemoteEndpoint(protocol, location.Server ?? string.Empty, location.Port ?? RemoteEndpoint.DefaultPort(protocol), location.User);
    }

    /// <summary>The path on the server (<c>/</c> is the top).</summary>
    public static string RemotePathOf(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return "/" + string.Join('/', location.Segments);
    }

    private static RemoteProtocol ProtocolOf(LocationKind kind) => kind switch
    {
        LocationKind.Sftp => RemoteProtocol.Sftp,
        LocationKind.Ftps => RemoteProtocol.Ftps,
        _ => RemoteProtocol.Ftp,
    };

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(options);
        var items = await RunAsync(location, (session, ct) => session.ListAsync(RemotePathOf(location), ct), cancellationToken).ConfigureAwait(false);
        var entries = new List<FileEntry>(items.Count);
        foreach (var item in items)
        {
            var entry = ToEntry(location, item);
            if (options.Includes(entry))
            {
                entries.Add(entry);
            }
        }

        for (var i = 0; i < entries.Count; i += options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ListingBatch(entries.GetRange(i, Math.Min(options.BatchSize, entries.Count - i)), i + options.BatchSize >= entries.Count);
        }

        if (entries.Count == 0)
        {
            yield return new ListingBatch([], true);
        }
    }

    /// <summary>Describes one item on a server, or null when there is none.</summary>
    public async Task<FileEntry?> StatAsync(LocationPath location, CancellationToken cancellationToken)
    {
        var item = await RunAsync(location, (session, ct) => session.StatAsync(RemotePathOf(location), ct), cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToEntry(location.Parent ?? location, item);
    }

    /// <summary>Opens a file on a server for reading; the stream keeps the server's session busy until it is disposed.</summary>
    public async Task<Stream> OpenReadAsync(LocationPath location, CancellationToken cancellationToken) =>
        await RunAsync(location, (session, ct) => session.OpenReadAsync(RemotePathOf(location), ct), cancellationToken).ConfigureAwait(false);

    /// <summary>Writes a file on a server, replacing it.</summary>
    public Task WriteAsync(LocationPath location, Stream content, CancellationToken cancellationToken) =>
        RunAsync<object?>(location, async (session, ct) =>
        {
            await session.WriteAsync(RemotePathOf(location), content, ct).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    /// <summary>Renames or moves an item on its server.</summary>
    public Task RenameAsync(LocationPath from, LocationPath to, CancellationToken cancellationToken) =>
        RunAsync<object?>(from, async (session, ct) =>
        {
            await session.RenameAsync(RemotePathOf(from), RemotePathOf(to), ct).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    /// <summary>Deletes a file, or an empty folder, on a server.</summary>
    public Task DeleteAsync(LocationPath location, bool isFolder, CancellationToken cancellationToken) =>
        RunAsync<object?>(location, async (session, ct) =>
        {
            await session.DeleteAsync(RemotePathOf(location), isFolder, ct).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    /// <summary>Creates a folder on a server.</summary>
    public Task CreateFolderAsync(LocationPath location, CancellationToken cancellationToken) =>
        RunAsync<object?>(location, async (session, ct) =>
        {
            await session.CreateFolderAsync(RemotePathOf(location), ct).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    /// <summary>Closes the connections that have been idle a while. Call now and then (a minute apart is plenty).</summary>
    public async Task CloseIdleAsync()
    {
        List<IRemoteSession> idle = [];
        lock (_lock)
        {
            var cutoff = _time.GetUtcNow() - IdleFor;
            foreach (var (key, pooled) in _pool.ToList())
            {
                if (pooled.LastUsed < cutoff && pooled.Gate.CurrentCount == 1)
                {
                    idle.Add(pooled.Session);
                    _pool.Remove(key);
                }
            }
        }

        foreach (var session in idle)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The number of open connections (for the Background tab and tests).</summary>
    public int OpenConnections
    {
        get
        {
            lock (_lock)
            {
                return _pool.Count;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<Pooled> all;
        lock (_lock)
        {
            all = [.. _pool.Values];
            _pool.Clear();
        }

        foreach (var pooled in all)
        {
            await pooled.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<T> RunAsync<T>(LocationPath location, Func<IRemoteSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var endpoint = EndpointOf(location);
        for (var attempt = 0; ; attempt++)
        {
            Pooled? pooled = null;
            try
            {
                pooled = await GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                await pooled.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await work(pooled.Session, cancellationToken).ConfigureAwait(false);
                    pooled.LastUsed = _time.GetUtcNow();
                    return result;
                }
                finally
                {
                    pooled.Gate.Release();
                }
            }
            catch (RemoteException ex)
            {
                // A connection that broke is dropped. A passing failure is tried once more on a new one; anything else is the answer.
                if (ex.Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.Unknown && pooled is not null)
                {
                    await DropAsync(endpoint, pooled).ConfigureAwait(false);
                }

                if (attempt == 0 && ex.IsTransient && pooled is not null)
                {
                    continue;
                }

                throw Wrap(ex, location);
            }
        }
    }

    private async Task<Pooled> GetAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_pool.TryGetValue(endpoint.Key, out var existing) && existing.Session.IsConnected)
            {
                return existing;
            }
        }

        if (!_factories.TryGetValue(endpoint.Protocol, out var factory))
        {
            throw new RemoteException(RemoteFailure.Unknown, $"{endpoint.Protocol} is not available.");
        }

        var session = await factory.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var created = new Pooled(session, _time.GetUtcNow());
        Pooled? replaced = null;
        lock (_lock)
        {
            if (_pool.TryGetValue(endpoint.Key, out var raced) && raced.Session.IsConnected)
            {
                replaced = created;
                created = raced;
            }
            else
            {
                _pool[endpoint.Key] = created;
            }
        }

        if (replaced is not null)
        {
            await replaced.Session.DisposeAsync().ConfigureAwait(false);
        }

        return created;
    }

    private async Task DropAsync(RemoteEndpoint endpoint, Pooled pooled)
    {
        lock (_lock)
        {
            if (_pool.TryGetValue(endpoint.Key, out var current) && current == pooled)
            {
                _pool.Remove(endpoint.Key);
            }
        }

        await pooled.Session.DisposeAsync().ConfigureAwait(false);
    }

    private static LocationException Wrap(RemoteException ex, LocationPath location) => new(
        ex.Failure switch
        {
            RemoteFailure.Unreachable => LocationError.Unreachable,
            RemoteFailure.Timeout => LocationError.Timeout,
            RemoteFailure.AuthenticationRequired or RemoteFailure.UntrustedServer => LocationError.AuthenticationRequired,
            RemoteFailure.NotFound => LocationError.NotFound,
            RemoteFailure.AccessDenied => LocationError.AccessDenied,
            _ => LocationError.Unknown,
        },
        location,
        ex.Message,
        ex);

    private static FileEntry ToEntry(LocationPath folder, RemoteItem item)
    {
        var attributes = item.IsFolder ? FileAttributes.Directory : FileAttributes.Normal;
        if (item.Name.StartsWith('.'))
        {
            attributes |= FileAttributes.Hidden;
        }

        var kind = item.IsLink ? FileEntryKind.Link : item.IsFolder ? FileEntryKind.Folder : FileEntryKind.File;
        return new FileEntry(item.Name, folder.Combine(item.Name), kind, item.IsFolder ? 0 : item.Size, item.Modified, item.Modified, attributes);
    }

    private sealed class Pooled(IRemoteSession session, DateTimeOffset lastUsed)
    {
        public IRemoteSession Session { get; } = session;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
