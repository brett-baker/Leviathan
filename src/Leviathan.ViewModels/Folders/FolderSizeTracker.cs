using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>A folder's total size as far as it is known.</summary>
/// <param name="Bytes">Total bytes below the folder.</param>
/// <param name="IsLowerBound">True while counting is under way or part of the folder could not be read ("≥ 412 MB").</param>
/// <param name="IsStale">True when the folder changed since it was counted (shown in italic until recounted).</param>
public readonly record struct FolderSizeInfo(long Bytes, bool IsLowerBound = false, bool IsStale = false);

/// <summary>Where folder totals come from. The app implements it on top of the worker's folder-size jobs and the shared size store (spec §12.7).</summary>
public interface IFolderSizeSource
{
    /// <summary>Raised on any thread when sizes may have changed for the given folders.</summary>
    event EventHandler<IReadOnlyList<LocationPath>>? Computed;

    /// <summary>True when sizes can be counted for <paramref name="folder"/> (local folders; shares are not counted unasked).</summary>
    bool CanCompute(LocationPath folder);

    /// <summary>The stored size of a folder, or null when it was never counted. Cheap; called for visible rows.</summary>
    FolderSizeInfo? TryGet(LocationPath folder);

    /// <summary>
    /// Asks for the folders to be counted in the background, at low priority. Returns without waiting. The entries carry the
    /// folders' last-write times, so a stored size that no longer matches can be recounted.
    /// </summary>
    void Request(IReadOnlyList<FileEntry> folders);
}

/// <summary>
/// Feeds the Size column of folders: shows what is stored, asks the source to count the folders that are missing or out of
/// date once the listing has finished, and tells the view when a value arrives (spec §8.2). Used from the UI thread.
/// </summary>
public sealed class FolderSizeTracker : IDisposable
{
    /// <summary>The most folders requested for one listing, so a folder of thousands of subfolders does not queue thousands of jobs.</summary>
    public const int MaxRequested = 64;

    private readonly IFolderSizeSource? _source;
    private readonly IUiContext _ui;
    private readonly HashSet<LocationPath> _requested = [];
    private bool _disposed;

    /// <summary>Creates a tracker; with a null <paramref name="source"/> folders simply show no size.</summary>
    public FolderSizeTracker(IFolderSizeSource? source, IUiContext ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _source = source;
        _ui = ui;
        if (source is not null)
        {
            source.Computed += OnComputed;
        }
    }

    /// <summary>Raised on the UI thread when the sizes of these folders changed.</summary>
    public event EventHandler<IReadOnlyList<LocationPath>>? Changed;

    /// <summary>The text for the Size cell of <paramref name="entry"/>: empty for files that are not shown here, a size or a placeholder for folders.</summary>
    public string TextFor(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind == FileEntryKind.File)
        {
            return DisplayFormat.Size(entry.Size);
        }

        if (!FileColumns.IsFolderLike(entry) || _source is null)
        {
            return string.Empty;
        }

        if (_source.TryGet(entry.Location) is { } info)
        {
            return (info.IsLowerBound ? "≥ " : string.Empty) + DisplayFormat.Size(info.Bytes);
        }

        return _source.CanCompute(entry.Location) && _requested.Contains(entry.Location) ? "…" : string.Empty;
    }

    /// <summary>True when the folder's value is out of date and should be drawn in italic.</summary>
    public bool IsStale(FileEntry entry) => _source?.TryGet(entry.Location) is { IsStale: true };

    /// <summary>Starts counting the folders of a finished listing whose size is unknown or stale. Call after each load.</summary>
    public void Begin(IReadOnlyList<FileEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _requested.Clear();
        if (_source is null)
        {
            return;
        }

        var wanted = new List<FileEntry>();
        foreach (var row in rows)
        {
            if (wanted.Count >= MaxRequested)
            {
                break;
            }

            if (FileColumns.IsFolderLike(row) && row.Kind == FileEntryKind.Folder && _source.CanCompute(row.Location)
                && _source.TryGet(row.Location) is not { IsStale: false, IsLowerBound: false })
            {
                wanted.Add(row);
                _requested.Add(row.Location);
            }
        }

        if (wanted.Count > 0)
        {
            _source.Request(wanted);
        }
    }

    /// <summary>Forgets what was requested (the folder changed).</summary>
    public void Reset() => _requested.Clear();

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        if (_source is not null)
        {
            _source.Computed -= OnComputed;
        }
    }

    private void OnComputed(object? sender, IReadOnlyList<LocationPath> folders) =>
        _ui.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var mine = folders.Where(_requested.Contains).ToList();
            if (mine.Count > 0)
            {
                Changed?.Invoke(this, mine);
            }
        });
}
