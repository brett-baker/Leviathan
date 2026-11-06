using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Engine.FolderSizes;

/// <summary>
/// Calculates folder sizes bottom-up, stores every subfolder on the way, and keeps them current by applying exact deltas
/// when Leviathan itself changes a folder (spec §12.7). Junctions and symbolic links are never followed.
/// </summary>
public sealed class FolderSizeService
{
    private readonly LocationProviderRegistry _providers;
    private readonly IFolderSizeStore _store;
    private readonly TimeProvider _time;
    private readonly ListingOptions _listing = ListingOptions.Default with { IncludeHidden = true, IncludeSystem = true, BatchSize = 1024 };

    /// <summary>Creates the service.</summary>
    public FolderSizeService(LocationProviderRegistry providers, IFolderSizeStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(store);
        _providers = providers;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The store sizes are kept in.</summary>
    public IFolderSizeStore Store => _store;

    /// <summary>
    /// Counts <paramref name="folder"/> and everything below it. Each finished subfolder is stored immediately, and
    /// <paramref name="partial"/> receives the root's running (lower-bound) totals as subfolders complete.
    /// Folders that cannot be read are stored with <see cref="FolderSizeState.Error"/> and counting continues.
    /// </summary>
    public async Task<FolderSize> ComputeAsync(LocationPath folder, IProgress<FolderSize>? partial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return await ComputeFolderAsync(folder, null, isRoot: true, partial is null ? null : new Adapter(partial), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="ComputeAsync"/>, and <paramref name="progress"/> also says how many of the root's subfolders are finished
    /// of how many there are, which is the one honest fraction a walk of unknown size has (the cell's 2 px bar, spec §12.7).
    /// </summary>
    public async Task<FolderSize> ComputeWithProgressAsync(LocationPath folder, IProgress<FolderSizeProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(progress);
        return await ComputeFolderAsync(folder, null, isRoot: true, progress, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Adapter(IProgress<FolderSize> inner) : IProgress<FolderSizeProgress>
    {
        // The opening "0 of N" report is for the bar; this overload keeps its old promise of one report per finished subfolder.
        public void Report(FolderSizeProgress value)
        {
            if (value.ChildrenDone > 0)
            {
                inner.Report(value.Totals);
            }
        }
    }

    /// <summary>
    /// Applies a change Leviathan made itself (copy, move, delete) without rescanning: <paramref name="folder"/>'s own
    /// totals change by the given amounts and every stored ancestor's recursive totals change by the same amounts.
    /// </summary>
    public void ApplyDelta(LocationPath folder, long bytes, int files, int folders = 0)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var now = _time.GetUtcNow();
        for (LocationPath? current = folder; current is not null; current = current.Parent)
        {
            if (_store.Get(current) is not { } size)
            {
                continue;
            }

            var isTarget = current == folder;
            _store.Upsert(size with
            {
                OwnBytes = isTarget ? Math.Max(0, size.OwnBytes + bytes) : size.OwnBytes,
                OwnFiles = isTarget ? Math.Max(0, size.OwnFiles + files) : size.OwnFiles,

                // How many folders sit directly inside is not in a recursive delta, so the count is unknown until the next recount.
                OwnFolders = isTarget && folders != 0 ? null : size.OwnFolders,
                TotalBytes = Math.Max(0, size.TotalBytes + bytes),
                TotalFiles = Math.Max(0, size.TotalFiles + files),
                TotalFolders = Math.Max(0, size.TotalFolders + folders),
                ComputedAt = now,
            });
        }
    }

    /// <summary>Marks a folder stale so its value is shown in italic until recounted.</summary>
    public void MarkStale(LocationPath folder)
    {
        if (_store.Get(folder) is { State: not FolderSizeState.Stale } size)
        {
            _store.Upsert(size with { State = FolderSizeState.Stale });
        }
    }

    /// <summary>
    /// True when a stored size no longer matches the folder's last-write time (a direct child was added, removed or renamed).
    /// </summary>
    public bool IsOutdated(LocationPath folder, DateTimeOffset folderModified) =>
        _store.Get(folder) is not { } size || size.State == FolderSizeState.Stale || size.FolderModified != folderModified;

    private async Task<FolderSize> ComputeFolderAsync(
        LocationPath folder, DateTimeOffset? folderModified, bool isRoot, IProgress<FolderSizeProgress>? partial, CancellationToken cancellationToken)
    {
        long ownBytes = 0;
        int ownFiles = 0;
        var ownOthers = 0;
        var subfolders = new List<FileEntry>();
        var state = FolderSizeState.Fresh;

        try
        {
            var provider = _providers.Resolve(folder);
            await foreach (var batch in provider.ListAsync(folder, _listing, cancellationToken).ConfigureAwait(false))
            {
                foreach (var entry in batch.Items)
                {
                    switch (entry.Kind)
                    {
                        case FileEntryKind.File:
                            ownBytes += entry.Size;
                            ownFiles++;
                            break;
                        case FileEntryKind.Folder:
                            subfolders.Add(entry);
                            break;
                        case FileEntryKind.Link:
                            // Junctions and symlinks are not followed: prevents loops and double counting.
                            ownOthers++;
                            break;
                    }
                }
            }
        }
        catch (LocationException ex) when (!ex.IsTransient)
        {
            state = FolderSizeState.Error;
        }

        long totalBytes = ownBytes;
        int totalFiles = ownFiles;
        int totalFolders = 0;

        var ownFolders = subfolders.Count + ownOthers;
        var done = 0;
        if (isRoot && partial is not null)
        {
            // Counting starts: the bar shows 0 of N before the first subfolder is finished.
            partial.Report(new FolderSizeProgress(
                new FolderSize(folder, ownBytes, ownFiles, totalBytes, totalFiles, totalFolders, FolderSizeState.Partial, _time.GetUtcNow(), folderModified, ownFolders), 0, subfolders.Count));
        }

        foreach (var sub in subfolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = await ComputeFolderAsync(sub.Location, sub.Modified, isRoot: false, partial: null, cancellationToken).ConfigureAwait(false);
            totalBytes += child.TotalBytes;
            totalFiles += child.TotalFiles;
            totalFolders += child.TotalFolders + 1;
            if (child.State == FolderSizeState.Error)
            {
                state = FolderSizeState.Error;
            }

            done++;
            if (isRoot && partial is not null)
            {
                partial.Report(new FolderSizeProgress(
                    new FolderSize(folder, ownBytes, ownFiles, totalBytes, totalFiles, totalFolders, FolderSizeState.Partial, _time.GetUtcNow(), folderModified, ownFolders), done, subfolders.Count));
            }
        }

        var result = new FolderSize(folder, ownBytes, ownFiles, totalBytes, totalFiles, totalFolders, state, _time.GetUtcNow(), folderModified, ownFolders);
        _store.Upsert(result);
        return result;
    }
}
