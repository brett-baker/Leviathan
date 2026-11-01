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
        return await ComputeFolderAsync(folder, null, isRoot: true, partial, cancellationToken).ConfigureAwait(false);
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
        LocationPath folder, DateTimeOffset? folderModified, bool isRoot, IProgress<FolderSize>? partial, CancellationToken cancellationToken)
    {
        long ownBytes = 0;
        int ownFiles = 0;
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

            if (isRoot && partial is not null)
            {
                partial.Report(new FolderSize(folder, ownBytes, ownFiles, totalBytes, totalFiles, totalFolders, FolderSizeState.Partial, _time.GetUtcNow(), folderModified));
            }
        }

        var result = new FolderSize(folder, ownBytes, ownFiles, totalBytes, totalFiles, totalFolders, state, _time.GetUtcNow(), folderModified);
        _store.Upsert(result);
        return result;
    }
}
