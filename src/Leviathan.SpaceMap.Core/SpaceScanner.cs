using System.Threading.Channels;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.SpaceMap;

/// <summary>Limits for a scan (spec §14: "network scans run with limited parallelism and can be paused").</summary>
public sealed record SpaceScanOptions
{
    /// <summary>Folders listed at the same time (default 4).</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Folders listed at the same time on one share or server (default 2).</summary>
    public int ParallelismPerShare { get; init; } = 2;

    /// <summary>How often the scan is saved while it runs, so a crash loses at most this much work (default 10 seconds).</summary>
    public TimeSpan CheckpointEvery { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Where a scan has got to.</summary>
/// <param name="FoldersListed">Folders listed so far (including the ones restored from a saved scan).</param>
/// <param name="FoldersPending">Folders waiting to be listed.</param>
/// <param name="Files">Files found.</param>
/// <param name="Bytes">Their total size.</param>
/// <param name="Errors">Folders that could not be read.</param>
/// <param name="IsPaused">True while the scan is paused.</param>
/// <param name="IsComplete">True when every folder was listed.</param>
public sealed record SpaceScanProgress(int FoldersListed, int FoldersPending, long Files, long Bytes, int Errors, bool IsPaused, bool IsComplete);

/// <summary>
/// A scan in progress or finished (spec §14): the tree it builds, which the view lays out while the scan still runs, and the controls. The tree is built by
/// scanner threads under a lock; read it only through <see cref="Read{T}"/>, which holds that lock, so a layout never sees a half-added folder.
/// </summary>
public sealed class SpaceScanSession : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Func<SpaceScanSnapshot, Task>? _save;
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _foldersListed;
    private int _pending;
    private long _files;
    private long _bytes;
    private int _errors;
    private volatile bool _paused;

    internal SpaceScanSession(SpaceNode root, string rootPath, Func<SpaceScanSnapshot, Task>? save)
    {
        Root = root;
        RootPath = rootPath;
        _save = save;
        _resume.SetResult();
    }

    /// <summary>The root folder of the scan.</summary>
    public SpaceNode Root { get; }

    /// <summary>The path scanned.</summary>
    public string RootPath { get; }

    /// <summary>Completes when the scan has listed every folder; faults or is cancelled if it stopped early.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    /// <summary>Raised (on a scanner thread) after each folder is added to the tree; throttle before drawing.</summary>
    public event EventHandler? Changed;

    /// <summary>True while paused.</summary>
    public bool IsPaused => _paused;

    internal CancellationToken Token => _cts.Token;

    /// <summary>The counts so far.</summary>
    public SpaceScanProgress Progress => new(Volatile.Read(ref _foldersListed), Volatile.Read(ref _pending), Interlocked.Read(ref _files), Interlocked.Read(ref _bytes), Volatile.Read(ref _errors), _paused, Completion.IsCompletedSuccessfully);

    /// <summary>Runs <paramref name="read"/> over the tree with the scanner held still.</summary>
    public T Read<T>(Func<SpaceNode, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_lock)
        {
            return read(Root);
        }
    }

    /// <summary>Holds the scan back after the folders being listed now (they finish first).</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lets a paused scan go on.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            _resume.TrySetResult();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the scan. What was found is kept and saved, so the scan can be resumed.</summary>
    public void Cancel()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _resume.TrySetResult();
        }
    }

    /// <summary>A copy of the scan as it is now, for saving.</summary>
    public SpaceScanSnapshot Snapshot(DateTimeOffset now, bool complete)
    {
        lock (_lock)
        {
            var rows = new List<SpaceScanRow>();
            var index = new Dictionary<SpaceNode, int>();
            var queue = new Queue<SpaceNode>();
            queue.Enqueue(Root);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                index[node] = rows.Count;
                rows.Add(new SpaceScanRow(node.Parent is null ? -1 : index[node.Parent], node.Name, node.IsFolder, node.IsFolder ? 0 : node.Size, node.IsFolder ? 0 : node.Modified.UtcTicks, node.Listed, node.Unreadable));
                foreach (var child in node.Children)
                {
                    queue.Enqueue(child);
                }
            }

            return new SpaceScanSnapshot(RootPath, now, complete, rows);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cts.Dispose();

    internal Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_lock)
        {
            wait = _resume.Task;
        }

        return wait.WaitAsync(cancellationToken);
    }

    internal void AddFolderResult(SpaceNode folder, IReadOnlyList<FileEntry> entries, List<SpaceNode> newFolders)
    {
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (entry.Kind == FileEntryKind.Folder)
                {
                    var sub = new SpaceNode(entry.Name, true, 0, entry.Modified, folder);
                    folder.Add(sub);
                    newFolders.Add(sub);
                }
                else
                {
                    var size = entry.Kind == FileEntryKind.Link ? 0 : entry.Size;
                    var file = new SpaceNode(entry.Name, false, size, entry.Modified, folder);
                    folder.Add(file);
                    Interlocked.Increment(ref _files);
                    Interlocked.Add(ref _bytes, size);
                }
            }

            folder.Listed = true;
        }

        Interlocked.Increment(ref _foldersListed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void MarkUnreadable(SpaceNode folder)
    {
        lock (_lock)
        {
            folder.Unreadable = true;
            folder.Listed = true;
        }

        Interlocked.Increment(ref _errors);
        Interlocked.Increment(ref _foldersListed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetPending(int pending) => Volatile.Write(ref _pending, pending);

    internal int AddPending(int by) => Interlocked.Add(ref _pending, by);

    internal void CountRestored(int folders, long files, long bytes, int errors)
    {
        Interlocked.Add(ref _foldersListed, folders);
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _errors, errors);
    }

    internal void SortAll()
    {
        lock (_lock)
        {
            var stack = new Stack<SpaceNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                node.SortBySize();
                foreach (var child in node.Children)
                {
                    if (child.IsFolder)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }

    internal Task SaveAsync(SpaceScanSnapshot snapshot) => _save?.Invoke(snapshot) ?? Task.CompletedTask;
}

/// <summary>
/// Scans a folder tree into a <see cref="SpaceNode"/> tree (spec §14): folders are listed in parallel with a small bound (smaller per share), the tree fills in
/// while it runs so the map can be drawn progressively, the scan can be paused, and it is saved every few seconds and when it stops. A scan stopped
/// part-way, or killed with its worker, is resumed from the saved copy by listing only the folders it had not listed.
/// </summary>
public sealed class SpaceScanner(LocationProviderRegistry providers, ISpaceScanStore? store = null, TimeProvider? time = null, SpaceScanOptions? options = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SpaceScanOptions _options = options ?? new SpaceScanOptions();

    /// <summary>The saved scan of <paramref name="root"/>, if there is one.</summary>
    public SpaceScanSnapshot? Saved(LocationPath root) => store?.Load(root.ToString());

    /// <summary>Starts scanning <paramref name="root"/> (from nothing, or from a saved scan to continue) and returns at once; the work runs in the background.</summary>
    public SpaceScanSession Start(LocationPath root, SpaceScanSnapshot? resumeFrom = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var pending = new List<SpaceNode>();
        SpaceNode rootNode;
        var restored = (Folders: 0, Files: 0L, Bytes: 0L, Errors: 0);
        if (resumeFrom is { Rows.Count: > 0 } snapshot && snapshot.Root == root.ToString() && snapshot.Rows[0].Parent == -1)
        {
            var nodes = new List<SpaceNode>(snapshot.Rows.Count);
            rootNode = new SpaceNode(snapshot.Rows[0].Name, true, 0, DateTimeOffset.UnixEpoch) { Listed = snapshot.Rows[0].Listed, Unreadable = snapshot.Rows[0].Unreadable };
            nodes.Add(rootNode);
            for (var i = 1; i < snapshot.Rows.Count; i++)
            {
                var row = snapshot.Rows[i];
                var node = new SpaceNode(row.Name, row.IsFolder, row.OwnSize, new DateTimeOffset(Math.Clamp(row.ModifiedTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero), nodes[row.Parent])
                {
                    Listed = row.Listed,
                    Unreadable = row.Unreadable,
                };
                nodes[row.Parent].Add(node);
                nodes.Add(node);
                if (!row.IsFolder)
                {
                    restored.Files++;
                    restored.Bytes += row.OwnSize;
                }
            }

            foreach (var node in nodes.Where(n => n.IsFolder))
            {
                if (node.Listed)
                {
                    restored.Folders++;
                    if (node.Unreadable)
                    {
                        restored.Errors++;
                    }
                }
                else
                {
                    pending.Add(node);
                }
            }
        }
        else
        {
            rootNode = new SpaceNode(root.ToString(), true, 0, DateTimeOffset.UnixEpoch);
            pending.Add(rootNode);
        }

        var session = new SpaceScanSession(rootNode, root.ToString(), store is null ? null : snap => Task.Run(() => store.Save(snap)));
        session.CountRestored(restored.Folders, restored.Files, restored.Bytes, restored.Errors);
        session.SetPending(pending.Count);
        session.Completion = Task.Run(() => RunAsync(session, root, pending));
        return session;
    }

    private async Task RunAsync(SpaceScanSession session, LocationPath root, List<SpaceNode> start)
    {
        var token = session.Token;
        var queue = Channel.CreateUnbounded<SpaceNode>();
        foreach (var node in start)
        {
            queue.Writer.TryWrite(node);
        }

        if (start.Count == 0)
        {
            queue.Writer.TryComplete();
        }

        var gates = new Dictionary<string, SemaphoreSlim>();
        var lastSaved = _time.GetUtcNow();
        var savingGate = new SemaphoreSlim(1, 1);

        SemaphoreSlim? GateFor(LocationPath folder)
        {
            if (!folder.IsNetwork)
            {
                return null;
            }

            lock (gates)
            {
                var key = (folder.Server ?? folder.Root) + "/" + folder.Share;
                if (!gates.TryGetValue(key, out var gate))
                {
                    var limit = Math.Max(1, _options.ParallelismPerShare);
                    gates[key] = gate = new SemaphoreSlim(limit, limit);
                }

                return gate;
            }
        }

        async Task SaveIfDueAsync(bool force, bool complete)
        {
            var now = _time.GetUtcNow();
            if (!force && now - lastSaved < _options.CheckpointEvery)
            {
                return;
            }

            if (!await savingGate.WaitAsync(force ? Timeout.Infinite : 0, CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                lastSaved = now;
                await session.SaveAsync(session.Snapshot(now, complete)).ConfigureAwait(false);
            }
            finally
            {
                savingGate.Release();
            }
        }

        async Task ScanFolderAsync(SpaceNode node)
        {
            await session.WaitIfPausedAsync(token).ConfigureAwait(false);
            var path = LocationPath.Parse(node.FullPath);
            var gate = GateFor(path);
            if (gate is not null)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
            }

            try
            {
                var entries = new List<FileEntry>();
                var listing = new ListingOptions { IncludeHidden = true, IncludeSystem = true, BatchSize = 1024 };
                await foreach (var batch in providers.Resolve(path).ListAsync(path, listing, token).ConfigureAwait(false))
                {
                    entries.AddRange(batch.Items);
                }

                var folders = new List<SpaceNode>();
                session.AddFolderResult(node, entries, folders);
                foreach (var sub in folders)
                {
                    session.AddPending(1);
                    queue.Writer.TryWrite(sub);
                }
            }
            catch (Exception ex) when (ex is LocationException or IOException or UnauthorizedAccessException)
            {
                session.MarkUnreadable(node);
            }
            finally
            {
                gate?.Release();
            }

            await SaveIfDueAsync(force: false, complete: false).ConfigureAwait(false);
        }

        async Task WorkerAsync()
        {
            while (await queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var node))
                {
                    try
                    {
                        await ScanFolderAsync(node).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (session.AddPending(-1) == 0)
                        {
                            queue.Writer.TryComplete();
                        }
                    }
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.Parallelism)).Select(_ => Task.Run(WorkerAsync, CancellationToken.None))).ConfigureAwait(false);
            session.SortAll();
            await SaveIfDueAsync(force: true, complete: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped (or killed): keep what was found so the scan can go on from here.
            session.SortAll();
            await SaveIfDueAsync(force: true, complete: false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var gate in gates.Values)
            {
                gate.Dispose();
            }

            savingGate.Dispose();
        }
    }
}
