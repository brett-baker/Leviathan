using System.Collections.Concurrent;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.FolderSizes;

/// <summary>Freshness of a stored folder size (spec §12.7).</summary>
public enum FolderSizeState
{
    /// <summary>Measured and believed current.</summary>
    Fresh,

    /// <summary>Known to be out of date; still shown (in italic) until recounted.</summary>
    Stale,

    /// <summary>Being counted now.</summary>
    Computing,

    /// <summary>Counting is under way; totals so far are a lower bound ("≥ 412 GB").</summary>
    Partial,

    /// <summary>Some of the folder could not be read; totals cover what could.</summary>
    Error,
}

/// <summary>The stored size of one folder.</summary>
/// <param name="Location">The folder.</param>
/// <param name="OwnBytes">Bytes in files directly in the folder.</param>
/// <param name="OwnFiles">Files directly in the folder.</param>
/// <param name="TotalBytes">Recursive total bytes.</param>
/// <param name="TotalFiles">Recursive file count.</param>
/// <param name="TotalFolders">Recursive folder count (not including the folder itself).</param>
/// <param name="State">Freshness.</param>
/// <param name="ComputedAt">When it was last measured.</param>
/// <param name="FolderModified">The folder's own last-write time when measured; a change marks it stale.</param>
/// <param name="OwnFolders">Folders (and links) directly inside, so files plus this is the Items column; null when counted before it was kept.</param>
public sealed record FolderSize(
    LocationPath Location,
    long OwnBytes,
    int OwnFiles,
    long TotalBytes,
    int TotalFiles,
    int TotalFolders,
    FolderSizeState State,
    DateTimeOffset ComputedAt,
    DateTimeOffset? FolderModified,
    int? OwnFolders = null)
{
    /// <summary>True when the totals are a lower bound.</summary>
    public bool IsLowerBound => State is FolderSizeState.Partial or FolderSizeState.Computing or FolderSizeState.Error;
}

/// <summary>What one file or folder weighs, measured before a job moves, copies or deletes it.</summary>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="Bytes">Bytes in the file, or in everything below the folder.</param>
/// <param name="Files">Files below the folder (0 for a file).</param>
/// <param name="Folders">Folders below the folder, not counting itself (0 for a file).</param>
public sealed record ItemSize(bool IsFolder, long Bytes, int Files = 0, int Folders = 0);

/// <summary>A count under way: the running totals and how many of the folder's own subfolders are finished.</summary>
/// <param name="Totals">The running (lower-bound) totals of the folder.</param>
/// <param name="ChildrenDone">Subfolders directly inside the folder that are finished.</param>
/// <param name="ChildrenTotal">Subfolders directly inside the folder.</param>
public readonly record struct FolderSizeProgress(FolderSize Totals, int ChildrenDone, int ChildrenTotal);

/// <summary>Persistent store of folder sizes. The SQLite implementation lives in Leviathan.Data.</summary>
public interface IFolderSizeStore
{
    /// <summary>Gets the stored size of a folder.</summary>
    FolderSize? Get(LocationPath location);

    /// <summary>Inserts or replaces a folder's size.</summary>
    void Upsert(FolderSize size);

    /// <summary>Removes a folder and everything stored below it.</summary>
    void RemoveTree(LocationPath location);
}

/// <summary>Thread-safe in-memory store for tests and as a cache.</summary>
public sealed class InMemoryFolderSizeStore : IFolderSizeStore
{
    private readonly ConcurrentDictionary<LocationPath, FolderSize> _sizes = new();

    /// <summary>Number of stored folders.</summary>
    public int Count => _sizes.Count;

    /// <inheritdoc />
    public FolderSize? Get(LocationPath location) => _sizes.TryGetValue(location, out var size) ? size : null;

    /// <inheritdoc />
    public void Upsert(FolderSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        _sizes[size.Location] = size;
    }

    /// <inheritdoc />
    public void RemoveTree(LocationPath location)
    {
        foreach (var key in _sizes.Keys.Where(location.Contains).ToList())
        {
            _sizes.TryRemove(key, out _);
        }
    }
}
