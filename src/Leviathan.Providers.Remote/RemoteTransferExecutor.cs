using System.Diagnostics;
using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Transfers;

namespace Leviathan.Providers.Remote;

/// <summary>
/// Copies, moves and deletes between this PC and SFTP/FTP/FTPS servers, and between two servers (spec §12, §7.1). The worker's own copy engine speaks Win32
/// paths; this one speaks to a server through <see cref="RemoteLocationProvider"/> and to disk through <see cref="IFileSystem"/>, and keeps the rules that matter:
/// names are planned and clashes settled before a byte moves, a file is written under a <c>*.leviathan-part</c> name and renamed when complete (spec §3.4), a move deletes
/// its source only after the copy arrived with the right size (spec §12.1), a passing network failure leaves the job to be run again, and a refusal asks once or is skipped
/// and reported (spec §12.2). Not here yet: resuming a half-copied file after a restart (the job starts over), a hash check (size only) and keeping times on servers.
/// </summary>
public sealed class RemoteTransferExecutor : IJobExecutor
{
    /// <summary>The temporary name while a file is being written.</summary>
    public const string PartSuffix = ".leviathan-part";

    private readonly RemoteLocationProvider _remote;
    private readonly IFileSystem _files;

    /// <summary>Creates the executor.</summary>
    public RemoteTransferExecutor(RemoteLocationProvider remote, IFileSystem files)
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>True when a job touches a server: any source, or the destination, is an sftp/ftp/ftps location.</summary>
    public static bool Involves(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Sources.Any(IsRemote) || (request.Destination is not null && IsRemote(request.Destination));
    }

    private static bool IsRemote(string location) => LocationPath.TryParse(location, out var path) && IsRemote(path);

    private static bool IsRemote(LocationPath path) => path.Kind is LocationKind.Sftp or LocationKind.Ftp or LocationKind.Ftps;

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => kind is JobKind.Copy or JobKind.Move or JobKind.Delete;

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        try
        {
            return request.Kind == JobKind.Delete
                ? await DeleteAsync(context, request, cancellationToken).ConfigureAwait(false)
                : await TransferAsync(context, request, cancellationToken).ConfigureAwait(false);
        }
        catch (LocationException ex) when (!ex.IsTransient)
        {
            return new JobOutcome(JobState.Failed, ex.Message);
        }
    }

    // ---- the two sides ----

    private sealed record Item(string Name, bool IsFolder, long Size, DateTimeOffset Modified);

    private interface IStore
    {
        Task<IReadOnlyList<Item>> ListAsync(LocationPath folder, CancellationToken ct);

        Task<Item?> StatAsync(LocationPath path, CancellationToken ct);

        Task<Stream> OpenReadAsync(LocationPath path, CancellationToken ct);

        Task WriteAsync(LocationPath path, Stream content, DateTimeOffset? modified, CancellationToken ct);

        Task CreateFolderAsync(LocationPath path, CancellationToken ct);

        Task DeleteAsync(LocationPath path, bool isFolder, CancellationToken ct);

        Task RenameAsync(LocationPath from, LocationPath to, CancellationToken ct);
    }

    private IStore StoreFor(LocationPath path) => IsRemote(path) ? new RemoteStore(_remote) : new LocalStore(_files);

    private sealed class RemoteStore(RemoteLocationProvider remote) : IStore
    {
        public async Task<IReadOnlyList<Item>> ListAsync(LocationPath folder, CancellationToken ct)
        {
            var items = new List<Item>();
            await foreach (var batch in remote.ListAsync(folder, new ListingOptions { IncludeHidden = true, IncludeSystem = true, BatchSize = 4096 }, ct).ConfigureAwait(false))
            {
                items.AddRange(batch.Items.Where(e => e.Kind != Core.Model.FileEntryKind.Link).Select(e => new Item(e.Name, e.Kind == Core.Model.FileEntryKind.Folder, e.Size, e.Modified)));
            }

            return items;
        }

        public async Task<Item?> StatAsync(LocationPath path, CancellationToken ct) =>
            await remote.StatAsync(path, ct).ConfigureAwait(false) is { } e ? new Item(e.Name, e.Kind == Core.Model.FileEntryKind.Folder, e.Size, e.Modified) : null;

        public Task<Stream> OpenReadAsync(LocationPath path, CancellationToken ct) => remote.OpenReadAsync(path, ct);

        public async Task WriteAsync(LocationPath path, Stream content, DateTimeOffset? modified, CancellationToken ct)
        {
            var part = (path.Parent ?? path).Combine(path.Name + PartSuffix);
            try
            {
                await remote.WriteAsync(part, content, ct).ConfigureAwait(false);
                if (await remote.StatAsync(path, ct).ConfigureAwait(false) is not null)
                {
                    await remote.DeleteAsync(path, isFolder: false, ct).ConfigureAwait(false);
                }

                await remote.RenameAsync(part, path, ct).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await remote.DeleteAsync(part, isFolder: false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is LocationException or IOException)
                {
                    // The server may be gone; the part file is named so it can be told from real data.
                }

                throw;
            }
        }

        public Task CreateFolderAsync(LocationPath path, CancellationToken ct) => remote.CreateFolderAsync(path, ct);

        public Task DeleteAsync(LocationPath path, bool isFolder, CancellationToken ct) => remote.DeleteAsync(path, isFolder, ct);

        public Task RenameAsync(LocationPath from, LocationPath to, CancellationToken ct) => remote.RenameAsync(from, to, ct);
    }

    private sealed class LocalStore(IFileSystem files) : IStore
    {
        public Task<IReadOnlyList<Item>> ListAsync(LocationPath folder, CancellationToken ct)
        {
            var directory = files.DirectoryInfo.New(folder.ToWin32Path());
            IReadOnlyList<Item> items =
            [
                .. directory.EnumerateFileSystemInfos().Where(i => !i.Attributes.HasFlag(FileAttributes.ReparsePoint)).Select(i =>
                    i is IFileInfo f ? new Item(f.Name, false, f.Length, f.LastWriteTimeUtc) : new Item(i.Name, true, 0, i.LastWriteTimeUtc)),
            ];
            return Task.FromResult(items);
        }

        public Task<Item?> StatAsync(LocationPath path, CancellationToken ct)
        {
            var io = path.ToWin32Path();
            Item? item = files.File.Exists(io)
                ? new Item(path.Name, false, files.FileInfo.New(io).Length, files.File.GetLastWriteTimeUtc(io))
                : files.Directory.Exists(io) ? new Item(path.Name, true, 0, files.Directory.GetLastWriteTimeUtc(io)) : null;
            return Task.FromResult(item);
        }

        public Task<Stream> OpenReadAsync(LocationPath path, CancellationToken ct) =>
            Task.FromResult<Stream>(files.FileStream.New(path.ToWin32Path(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous));

        public async Task WriteAsync(LocationPath path, Stream content, DateTimeOffset? modified, CancellationToken ct)
        {
            var final = path.ToWin32Path();
            var part = final + PartSuffix;
            files.Directory.CreateDirectory(files.Path.GetDirectoryName(final)!);
            try
            {
                var target = files.FileStream.New(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
                try
                {
                    await content.CopyToAsync(target, 1 << 20, ct).ConfigureAwait(false);
                }
                finally
                {
                    await target.DisposeAsync().ConfigureAwait(false);
                }

                if (modified is { } when)
                {
                    files.File.SetLastWriteTimeUtc(part, when.UtcDateTime);
                }

                files.File.Move(part, final, overwrite: true);
            }
            catch
            {
                if (files.File.Exists(part))
                {
                    try
                    {
                        files.File.Delete(part);
                    }
                    catch (IOException)
                    {
                        // Left behind under its part name.
                    }
                }

                throw;
            }
        }

        public Task CreateFolderAsync(LocationPath path, CancellationToken ct)
        {
            files.Directory.CreateDirectory(path.ToWin32Path());
            return Task.CompletedTask;
        }

        public Task DeleteAsync(LocationPath path, bool isFolder, CancellationToken ct)
        {
            if (isFolder)
            {
                files.Directory.Delete(path.ToWin32Path(), recursive: false);
            }
            else
            {
                files.File.Delete(path.ToWin32Path());
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(LocationPath from, LocationPath to, CancellationToken ct)
        {
            var io = from.ToWin32Path();
            if (files.Directory.Exists(io))
            {
                files.Directory.Move(io, to.ToWin32Path());
            }
            else
            {
                files.File.Move(io, to.ToWin32Path());
            }

            return Task.CompletedTask;
        }
    }

    // ---- the plan ----

    private abstract record Step;

    private sealed record MakeFolder(LocationPath Target) : Step;

    private sealed record CopyFile(LocationPath Source, LocationPath Target, long Size, DateTimeOffset Modified, bool Replaces) : Step;

    private sealed record RemoveFolder(LocationPath Source) : Step;

    private sealed record RenameWhole(LocationPath Source, LocationPath Target, bool Replaces, bool IsFolder) : Step;

    private sealed class Entry(LocationPath source)
    {
        public LocationPath Source { get; } = source;

        public LocationPath? Target { get; set; }

        public List<Step> Steps { get; } = [];

        public bool Clash { get; set; }

        public ItemStatus Status { get; set; } = ItemStatus.Done;

        public string? Note { get; set; }
    }

    private async Task<JobOutcome> TransferAsync(JobContext context, JobRequest request, CancellationToken ct)
    {
        var move = request.Kind == JobKind.Move;
        var destination = LocationPath.Parse(request.Destination!);
        var destinationStore = StoreFor(destination);
        if (await destinationStore.StatAsync(destination, ct).ConfigureAwait(false) is not { IsFolder: true })
        {
            return new JobOutcome(JobState.Failed, $"{destination.Name} does not exist.");
        }

        context.Report(0, -1, 0, request.Sources.Count);
        var entries = new List<Entry>();
        foreach (var text in request.Sources)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(await PlanAsync(LocationPath.Parse(text), destination, destinationStore, move, ct).ConfigureAwait(false));
        }

        await SettleClashesAsync(context, request, entries, destinationStore, ct).ConfigureAwait(false);

        var total = entries.Where(e => e.Status == ItemStatus.Done).SelectMany(e => e.Steps).OfType<CopyFile>().Sum(c => c.Size);
        long done = 0;
        var itemsDone = 0;
        var failures = new List<string>();
        var skipAll = new HashSet<ErrorKind>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Status != ItemStatus.Done)
            {
                context.AddResult(new ItemOutcome(entry.Source.Value, null, entry.Status, entry.Note));
                if (entry.Status == ItemStatus.Failed)
                {
                    failures.Add($"{entry.Source.Name}: {entry.Note}");
                }
            }
            else
            {
                var skipped = new List<string>();
                foreach (var step in entry.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    if (step is RemoveFolder && skipped.Count > 0)
                    {
                        continue;
                    }

                    while (true)
                    {
                        try
                        {
                            done += await RunStepAsync(context, request, step, move, done, total, itemsDone, request.Sources.Count, ct).ConfigureAwait(false);
                            break;
                        }
                        catch (LocationException ex) when (ex.IsTransient)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            if (await ShouldRetryAsync(context, request, skipAll, NameOf(step), ex, ct).ConfigureAwait(false))
                            {
                                continue;
                            }

                            if (entry.Steps.Count == 1 || step is not CopyFile)
                            {
                                skipped.Add($"{NameOf(step)}: {ex.Message}");
                                break;
                            }

                            skipped.Add($"{NameOf(step)}: {ex.Message}");
                            break;
                        }
                    }
                }

                var outcome = skipped.Count == 0
                    ? new ItemOutcome(entry.Source.Value, entry.Target?.Value, ItemStatus.Done)
                    : new ItemOutcome(entry.Source.Value, null, ItemStatus.Failed, $"{skipped.Count} file(s) skipped. {string.Join(" ", skipped.Take(3))}");
                context.AddResult(outcome);
                if (outcome.Status == ItemStatus.Failed)
                {
                    failures.Add($"{entry.Source.Name}: {outcome.Error}");
                }
            }

            itemsDone++;
            context.Report(done, total, itemsDone, request.Sources.Count);
        }

        return failures.Count == 0
            ? JobOutcome.Completed
            : new JobOutcome(JobState.Failed, $"{failures.Count} of {entries.Count} item(s) failed. {string.Join(" ", failures.Take(3))}");
    }

    private static string NameOf(Step step) => step switch
    {
        CopyFile c => c.Source.Name,
        RenameWhole r => r.Source.Name,
        RemoveFolder f => f.Source.Name,
        MakeFolder m => m.Target.Name,
        _ => string.Empty,
    };

    private static bool SameServer(LocationPath a, LocationPath b) =>
        IsRemote(a) && IsRemote(b) && string.Equals(RemoteLocationProvider.EndpointOf(a).Key, RemoteLocationProvider.EndpointOf(b).Key, StringComparison.OrdinalIgnoreCase);

    private async Task<Entry> PlanAsync(LocationPath source, LocationPath destination, IStore destinationStore, bool move, CancellationToken ct)
    {
        var entry = new Entry(source);
        var sourceStore = StoreFor(source);
        var item = await sourceStore.StatAsync(source, ct).ConfigureAwait(false);
        if (item is null)
        {
            entry.Status = ItemStatus.Failed;
            entry.Note = $"{source.Name} is not there any more.";
            return entry;
        }

        var target = destination.Combine(source.Name);
        entry.Target = target;
        var existing = await destinationStore.StatAsync(target, ct).ConfigureAwait(false);
        if (item.IsFolder && existing is { IsFolder: true })
        {
            // A folder that is already there is merged into, file by file.
        }
        else if (existing is not null)
        {
            entry.Clash = true;
        }

        if (move && SameServer(source, destination) && existing is null)
        {
            entry.Steps.Add(new RenameWhole(source, target, Replaces: false, item.IsFolder));
            return entry;
        }

        await AddSteps(entry, source, target, item, sourceStore, destinationStore, move, ct).ConfigureAwait(false);
        return entry;
    }

    private static async Task AddSteps(Entry entry, LocationPath source, LocationPath target, Item item, IStore sourceStore, IStore destinationStore, bool move, CancellationToken ct)
    {
        if (!item.IsFolder)
        {
            entry.Steps.Add(new CopyFile(source, target, item.Size, item.Modified, Replaces: false));
            return;
        }

        var folders = new Stack<(LocationPath Source, LocationPath Target)>();
        var removals = new List<RemoveFolder>();
        folders.Push((source, target));
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (from, to) = folders.Pop();
            entry.Steps.Add(new MakeFolder(to));
            removals.Add(new RemoveFolder(from));
            foreach (var child in await sourceStore.ListAsync(from, ct).ConfigureAwait(false))
            {
                var childFrom = from.Combine(child.Name);
                var childTo = to.Combine(child.Name);
                if (child.IsFolder)
                {
                    folders.Push((childFrom, childTo));
                }
                else
                {
                    entry.Steps.Add(new CopyFile(childFrom, childTo, child.Size, child.Modified, Replaces: false));
                }
            }
        }

        if (move)
        {
            // Deepest folders first, so each is empty by the time it is removed.
            entry.Steps.AddRange(Enumerable.Reverse(removals));
        }
    }

    // ---- name clashes ----

    private static async Task SettleClashesAsync(JobContext context, JobRequest request, List<Entry> entries, IStore destinationStore, CancellationToken ct)
    {
        var clashes = new List<(Entry Entry, CopyFile? File, LocationPath Target)>();
        foreach (var entry in entries.Where(e => e.Status == ItemStatus.Done))
        {
            if (entry.Clash && entry.Target is { } top)
            {
                clashes.Add((entry, entry.Steps.OfType<CopyFile>().FirstOrDefault(c => c.Target == top), top));
            }

            foreach (var file in entry.Steps.OfType<CopyFile>().Where(c => entry.Target is null || c.Target != entry.Target))
            {
                if (await destinationStore.StatAsync(file.Target, ct).ConfigureAwait(false) is { IsFolder: false })
                {
                    clashes.Add((entry, file, file.Target));
                }
            }
        }

        if (clashes.Count == 0)
        {
            return;
        }

        ConflictAnswer answer;
        if (request.Conflict == ConflictPolicy.Ask)
        {
            var items = new List<ConflictItem>();
            foreach (var (_, file, target) in clashes.Take(1000))
            {
                var there = await destinationStore.StatAsync(target, ct).ConfigureAwait(false);
                items.Add(new ConflictItem(file?.Source.Value ?? string.Empty, target.Value, file?.Size ?? 0, file?.Modified ?? DateTimeOffset.UnixEpoch, there?.Size ?? 0, there?.Modified ?? DateTimeOffset.UnixEpoch));
            }

            answer = await context.AskConflictsAsync(items, clashes.Count, ct).ConfigureAwait(false);
        }
        else
        {
            answer = new ConflictAnswer(request.Conflict);
        }

        foreach (var (entry, file, target) in clashes)
        {
            var policy = answer.Overrides is { } overrides && overrides.TryGetValue(target.Value, out var own) ? own : answer.ForAll;
            var there = await destinationStore.StatAsync(target, ct).ConfigureAwait(false);
            var replace = policy switch
            {
                ConflictPolicy.Replace => true,
                ConflictPolicy.ReplaceIfNewer => file is not null && there is not null && file.Modified > there.Modified,
                ConflictPolicy.ReplaceIfLarger => file is not null && there is not null && file.Size > there.Size,
                ConflictPolicy.ReplaceIfDifferent => file is not null && there is not null && (file.Size != there.Size || file.Modified != there.Modified),
                _ => false,
            };
            if (replace)
            {
                continue; // the write replaces what is there
            }

            if (policy == ConflictPolicy.KeepBoth && file is not null)
            {
                var renamed = await UniqueNameAsync(target, destinationStore, ct).ConfigureAwait(false);
                var at = entry.Steps.IndexOf(file);
                entry.Steps[at] = file with { Target = renamed };
                if (entry.Target == target)
                {
                    entry.Target = renamed;
                }

                continue;
            }

            // Skip (or a policy that decided not to replace): the file stays where it is; a top-level item with nothing else left is reported skipped.
            if (file is not null)
            {
                entry.Steps.Remove(file);
            }

            if (entry.Target == target || entry.Steps.OfType<CopyFile>().Any() == false)
            {
                entry.Status = ItemStatus.Skipped;
                entry.Note = "Already there.";
            }
        }
    }

    private static async Task<LocationPath> UniqueNameAsync(LocationPath target, IStore store, CancellationToken ct)
    {
        var parent = target.Parent ?? target;
        var stem = Path.GetFileNameWithoutExtension(target.Name);
        var extension = Path.GetExtension(target.Name);
        for (var number = 2; ; number++)
        {
            var candidate = parent.Combine($"{stem} ({number}){extension}");
            if (await store.StatAsync(candidate, ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }
    }

    // ---- running a step ----

    private async Task<long> RunStepAsync(JobContext context, JobRequest request, Step step, bool move, long done, long total, int itemsDone, int itemsTotal, CancellationToken ct)
    {
        switch (step)
        {
            case MakeFolder make:
                var destinationStore = StoreFor(make.Target);
                if (await destinationStore.StatAsync(make.Target, ct).ConfigureAwait(false) is null)
                {
                    await destinationStore.CreateFolderAsync(make.Target, ct).ConfigureAwait(false);
                }

                return 0;
            case RemoveFolder remove:
                await StoreFor(remove.Source).DeleteAsync(remove.Source, isFolder: true, ct).ConfigureAwait(false);
                return 0;
            case RenameWhole rename:
                try
                {
                    await StoreFor(rename.Source).RenameAsync(rename.Source, rename.Target, ct).ConfigureAwait(false);
                    return 0;
                }
                catch (LocationException ex) when (!ex.IsTransient)
                {
                    throw new IOException($"{rename.Source.Name} could not be moved on the server: {ex.Message}", ex);
                }

            case CopyFile copy:
                var sourceStore = StoreFor(copy.Source);
                var targetStore = StoreFor(copy.Target);
                var sofar = done;
                var limiter = request.BytesPerSecondLimit > 0 ? new RateLimiter(request.BytesPerSecondLimit) : null;
                var read = await sourceStore.OpenReadAsync(copy.Source, ct).ConfigureAwait(false);
                try
                {
                    var counting = new CountingStream(read, limiter, bytes =>
                    {
                        sofar += bytes;
                        context.Report(sofar, total, itemsDone, itemsTotal, copy.Source.Name);
                    });
                    await targetStore.WriteAsync(copy.Target, counting, copy.Modified, ct).ConfigureAwait(false);
                }
                finally
                {
                    await read.DisposeAsync().ConfigureAwait(false);
                }

                // The copy counts only when it arrived whole: the size on the other side is the one that was read (spec §12.1).
                var arrived = await targetStore.StatAsync(copy.Target, ct).ConfigureAwait(false);
                if (arrived is null || arrived.Size != copy.Size)
                {
                    try
                    {
                        await targetStore.DeleteAsync(copy.Target, isFolder: false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Left for the user to see; the error below says what is wrong.
                    }

                    throw new VerifyFailedException($"{copy.Source.Name} arrived with {arrived?.Size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "no"} bytes instead of {copy.Size:N0}.");
                }

                if (move)
                {
                    await sourceStore.DeleteAsync(copy.Source, isFolder: false, ct).ConfigureAwait(false);
                }

                return copy.Size;
            default:
                return 0;
        }
    }

    private static async Task<bool> ShouldRetryAsync(JobContext context, JobRequest request, HashSet<ErrorKind> skipAll, string item, Exception error, CancellationToken ct)
    {
        var kind = error switch
        {
            UnauthorizedAccessException => ErrorKind.AccessDenied,
            VerifyFailedException => ErrorKind.VerifyFailed,
            LocationException { Error: LocationError.AccessDenied } => ErrorKind.AccessDenied,
            _ => ErrorKind.Other,
        };
        if (request.OnError == ErrorMode.SkipAndReport || skipAll.Contains(kind))
        {
            return false;
        }

        var decision = await context.AskAsync(kind, item, error.Message, ct).ConfigureAwait(false);
        if (decision == ErrorDecision.SkipAllLikeThis)
        {
            skipAll.Add(kind);
        }

        return decision == ErrorDecision.Retry;
    }

    // ---- delete ----

    private async Task<JobOutcome> DeleteAsync(JobContext context, JobRequest request, CancellationToken ct)
    {
        var failures = new List<string>();
        var done = 0;
        context.Report(0, -1, 0, request.Sources.Count);
        foreach (var text in request.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var source = LocationPath.Parse(text);
            try
            {
                await DeleteTreeAsync(StoreFor(source), source, ct).ConfigureAwait(false);
                context.AddResult(new ItemOutcome(text, null, ItemStatus.Done));
            }
            catch (LocationException ex) when (ex.IsTransient)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.AddResult(new ItemOutcome(text, null, ItemStatus.Failed, ex.Message));
                failures.Add($"{source.Name}: {ex.Message}");
            }

            context.Report(0, -1, ++done, request.Sources.Count, source.Name);
        }

        return failures.Count == 0 ? JobOutcome.Completed : new JobOutcome(JobState.Failed, $"{failures.Count} of {request.Sources.Count} item(s) failed. {string.Join(" ", failures.Take(3))}");
    }

    private static async Task DeleteTreeAsync(IStore store, LocationPath path, CancellationToken ct)
    {
        var item = await store.StatAsync(path, ct).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        if (!item.IsFolder)
        {
            await store.DeleteAsync(path, isFolder: false, ct).ConfigureAwait(false);
            return;
        }

        foreach (var child in await store.ListAsync(path, ct).ConfigureAwait(false))
        {
            await DeleteTreeAsync(store, path.Combine(child.Name), ct).ConfigureAwait(false);
        }

        await store.DeleteAsync(path, isFolder: true, ct).ConfigureAwait(false);
    }

    // ---- reading with a count and a speed limit ----

    private sealed class RateLimiter(long bytesPerSecond)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _bytes;

        public async ValueTask WaitAsync(int count, CancellationToken ct)
        {
            _bytes += count;
            var due = TimeSpan.FromSeconds((double)_bytes / bytesPerSecond) - _clock.Elapsed;
            if (due > TimeSpan.FromMilliseconds(5))
            {
                await Task.Delay(due, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class CountingStream(Stream inner, RateLimiter? limiter, Action<long> onBytes) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Count(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (limiter is not null && n > 0)
            {
                await limiter.WaitAsync(n, cancellationToken).ConfigureAwait(false);
            }

            Count(n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Count(int n)
        {
            if (n > 0)
            {
                onBytes(n);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Sends each job to the executor that can do it: a job that touches an SFTP/FTP/FTPS server goes to <see cref="RemoteTransferExecutor"/>, any other to the executor the
/// worker already had. The worker chooses an executor by the kind of job alone, so this stands in front of both.
/// </summary>
public sealed class RemoteAwareExecutor(RemoteTransferExecutor remote, IJobExecutor local) : IJobExecutor
{
    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => local.CanExecute(kind);

    /// <inheritdoc />
    public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RemoteTransferExecutor.Involves(context.Job.Request) && remote.CanExecute(context.Job.Request.Kind)
            ? remote.ExecuteAsync(context, cancellationToken)
            : local.ExecuteAsync(context, cancellationToken);
    }
}
