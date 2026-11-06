using System.IO.Abstractions;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.FolderSizes;

/// <summary>
/// Keeps stored folder sizes current when Leviathan itself copies, moves, deletes or restores (spec §12.7): the job's sources are
/// weighed before it runs, and afterwards the exact bytes and counts that arrived or left are applied to the folder and every stored
/// ancestor at once, with no rescan. When a size is not known (a folder that was never counted, or a name clash that may have replaced
/// something) the folders involved are marked stale instead, so they are recounted when visited. Never throws: a size that cannot be
/// kept current must not fail the job.
/// </summary>
public sealed class FolderSizeDeltas
{
    private readonly FolderSizeService _service;
    private readonly IFileSystem _fs;

    /// <summary>Creates the tracker.</summary>
    public FolderSizeDeltas(FolderSizeService service, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _service = service;
        _fs = fileSystem;
    }

    /// <summary>What each source weighs now, by source path; an item whose size is not known is missing.</summary>
    public IReadOnlyDictionary<string, ItemSize> Measure(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sizes = new Dictionary<string, ItemSize>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in request.Sources)
        {
            try
            {
                if (Weigh(LocationPath.Parse(source)) is { } size)
                {
                    sizes[source] = size;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                // Not weighed: the folders it touches are marked stale afterwards.
            }
        }

        return sizes;
    }

    /// <summary>Applies what <paramref name="results"/> say was done. <paramref name="before"/> comes from <see cref="Measure"/>.</summary>
    public void Apply(JobRequest request, IReadOnlyDictionary<string, ItemSize> before, IReadOnlyList<ItemOutcome> results)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(results);

        // Only KeepBoth and Skip can never replace something, so only they leave the numbers exact.
        var mayReplace = request.Conflict is not (ConflictPolicy.KeepBoth or ConflictPolicy.Skip);
        foreach (var outcome in results.Where(r => r.Status == ItemStatus.Done))
        {
            try
            {
                ApplyOne(request.Kind, mayReplace, outcome, before.TryGetValue(outcome.Source, out var size) ? size : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                // A size that cannot be kept current is recounted later.
            }
        }
    }

    private void ApplyOne(JobKind kind, bool mayReplace, ItemOutcome outcome, ItemSize? size)
    {
        var source = LocationPath.Parse(outcome.Source);
        var result = outcome.Result is { } text ? LocationPath.Parse(text) : null;
        switch (kind)
        {
            case JobKind.Copy when result?.Parent is { } copyParent:
                Arrive(copyParent, size, mayReplace);
                break;
            case JobKind.Move when result?.Parent is { } moveParent:
                Leave(source, size);
                Arrive(moveParent, size, mayReplace);
                break;
            case JobKind.Delete:
                Leave(source, size);
                break;
            case JobKind.Restore when result?.Parent is { } restoreParent:
                // The bin is outside every total, so a restore only adds.
                Arrive(restoreParent, size, mayReplace: false);
                break;
        }
    }

    private void Arrive(LocationPath parent, ItemSize? size, bool mayReplace)
    {
        if (size is null || mayReplace)
        {
            _service.MarkChainStale(parent);
            return;
        }

        _service.ApplyItem(parent, size, +1, ModifiedOf(parent));
    }

    private void Leave(LocationPath source, ItemSize? size)
    {
        if (source.Parent is not { } parent)
        {
            return;
        }

        if (size is null)
        {
            _service.MarkChainStale(parent);
        }
        else
        {
            _service.ApplyItem(parent, size, -1, ModifiedOf(parent));
        }

        if (size?.IsFolder != false)
        {
            // A folder that left takes the sizes stored below it along: those paths do not exist any more.
            _service.Forget(source);
        }
    }

    private ItemSize? Weigh(LocationPath path)
    {
        var io = path.ToWin32Path();
        if (_fs.File.Exists(io))
        {
            return new ItemSize(IsFolder: false, _fs.FileInfo.New(io).Length);
        }

        // A folder is known only from a count that finished; a partial or stale one would carry its error into every ancestor.
        return _fs.Directory.Exists(io) && _service.Store.Get(path) is { State: FolderSizeState.Fresh } stored
            ? new ItemSize(IsFolder: true, stored.TotalBytes, stored.TotalFiles, stored.TotalFolders)
            : null;
    }

    private DateTimeOffset? ModifiedOf(LocationPath folder)
    {
        try
        {
            var io = folder.ToWin32Path();
            return _fs.Directory.Exists(io) ? new DateTimeOffset(_fs.DirectoryInfo.New(io).LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Wraps an executor of copies, moves, deletes or restores so that finished work updates stored folder sizes (spec §12.7).</summary>
public sealed class DeltaTrackingExecutor : IJobExecutor
{
    private readonly IJobExecutor _inner;
    private readonly FolderSizeDeltas _deltas;

    /// <summary>Creates the wrapper.</summary>
    public DeltaTrackingExecutor(IJobExecutor inner, FolderSizeDeltas deltas)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(deltas);
        _inner = inner;
        _deltas = deltas;
    }

    /// <inheritdoc />
    public bool CanExecute(JobKind kind) => _inner.CanExecute(kind);

    /// <inheritdoc />
    public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Job.Request;
        var before = _deltas.Measure(request);
        try
        {
            return await _inner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also for a job that was cancelled or failed half way: what it did is on disk, and the results list says what.
            _deltas.Apply(request, before, context.Results);
        }
    }
}
