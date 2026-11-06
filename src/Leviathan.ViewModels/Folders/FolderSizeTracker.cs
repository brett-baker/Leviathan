using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>A folder's total size as far as it is known.</summary>
/// <param name="Bytes">Total bytes below the folder.</param>
/// <param name="IsLowerBound">True while counting is under way or part of the folder could not be read ("≥ 412 MB").</param>
/// <param name="IsStale">True when the folder changed since it was counted (shown in italic until recounted).</param>
/// <param name="Files">Files below the folder (0 when not known).</param>
/// <param name="Folders">Folders below the folder (0 when not known).</param>
/// <param name="ComputedAt">When the folder was counted; null when not known.</param>
/// <param name="IsNetwork">True for a folder on a share, where a size always shows its age because a change deeper down is not noticed (spec §12.7).</param>
/// <param name="OwnBytes">Bytes in the files directly inside the folder (the Own size column).</param>
/// <param name="OwnFiles">Files directly inside the folder.</param>
/// <param name="OwnFolders">Folders directly inside the folder; null when the folder was counted before this was kept (the Items column then stays blank).</param>
public readonly record struct FolderSizeInfo(
    long Bytes,
    bool IsLowerBound = false,
    bool IsStale = false,
    int Files = 0,
    int Folders = 0,
    DateTimeOffset? ComputedAt = null,
    bool IsNetwork = false,
    long OwnBytes = 0,
    int OwnFiles = 0,
    int? OwnFolders = null)
{
    /// <summary>The value of <paramref name="metric"/>, or null when it is not known.</summary>
    public long? Value(FolderMetric metric) => metric switch
    {
        FolderMetric.TotalBytes => Bytes,
        FolderMetric.OwnBytes => OwnBytes,
        FolderMetric.Items => OwnFolders is { } folders ? OwnFiles + folders : null,
        FolderMetric.TotalFiles => Files,
        FolderMetric.TotalFolders => Folders,
        _ => null,
    };
}

/// <summary>A number the folder-size service keeps for a folder, shown in a column of its own (spec §12.7).</summary>
public enum FolderMetric
{
    /// <summary>Everything below the folder, at any depth (the Size column).</summary>
    TotalBytes,

    /// <summary>Only the files directly inside.</summary>
    OwnBytes,

    /// <summary>Files and folders directly inside.</summary>
    Items,

    /// <summary>Files at every depth.</summary>
    TotalFiles,

    /// <summary>Folders at every depth.</summary>
    TotalFolders,
}

/// <summary>Where a folder is in the counting queue, for the 2 px bar of its Size cell.</summary>
/// <param name="IsRunning">False while waiting for its turn, true while it is being walked.</param>
/// <param name="Fraction">Subfolders finished of all of them, 0 to 1; null when not known (waiting, or no subfolders to count).</param>
public readonly record struct FolderProgress(bool IsRunning, double? Fraction);

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

    /// <summary>Where <paramref name="folder"/> is in the queue, or null when it is not waiting or being counted.</summary>
    FolderProgress? ProgressOf(LocationPath folder) => null;

    /// <summary>
    /// Says which folders are urgent: on screen, or selected (spec §12.7). Counting of those goes first; the rest keeps its place behind
    /// them and is not thrown away.
    /// </summary>
    void Prioritize(IReadOnlyCollection<LocationPath> urgent)
    {
    }
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
    private readonly TimeProvider _time;
    private readonly HashSet<LocationPath> _requested = [];
    private bool _disposed;

    /// <summary>Creates a tracker; with a null <paramref name="source"/> folders simply show no size.</summary>
    public FolderSizeTracker(IFolderSizeSource? source, IUiContext ui, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _source = source;
        _ui = ui;
        _time = time ?? TimeProvider.System;
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
            var text = (info.IsLowerBound ? "≥ " : string.Empty) + DisplayFormat.Size(info.Bytes);
            return info.IsNetwork && AgeOf(info) is { } age ? $"{text} · {ShortAge(age)}" : text;
        }

        return _source.CanCompute(entry.Location) && _requested.Contains(entry.Location) ? "…" : string.Empty;
    }

    /// <summary>
    /// The text of the Own size, Items, Total files or Total folders cell: blank for a file, a placeholder while the folder is
    /// being counted, and blank when the value is not known (a folder counted before Items was kept).
    /// </summary>
    public string TextFor(FileEntry entry, FolderMetric metric)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (metric == FolderMetric.TotalBytes)
        {
            return TextFor(entry);
        }

        if (!FileColumns.IsFolderLike(entry) || _source is null)
        {
            return string.Empty;
        }

        if (_source.TryGet(entry.Location) is { } info)
        {
            return info.Value(metric) is { } value
                ? (info.IsLowerBound && metric != FolderMetric.Items && metric != FolderMetric.OwnBytes ? "≥ " : string.Empty)
                    + (metric == FolderMetric.OwnBytes ? DisplayFormat.Size(value) : value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture))
                : string.Empty;
        }

        return _source.CanCompute(entry.Location) && _requested.Contains(entry.Location) ? "…" : string.Empty;
    }

    /// <summary>The number <paramref name="metric"/> sorts folders by, or null when unknown; <see cref="FolderItemsView"/> asks for it.</summary>
    public long? ValueOf(FileEntry entry, FolderMetric metric)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _source?.TryGet(entry.Location) is { } info ? info.Value(metric) : null;
    }

    /// <summary>The 2 px bar of the Size cell: null when the folder is neither waiting nor being counted.</summary>
    public FolderProgress? ProgressFor(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return FileColumns.IsFolderLike(entry) ? _source?.ProgressOf(entry.Location) : null;
    }

    /// <summary>
    /// The tooltip of a folder's Size cell and the lines of the drive card and preview pane: the total, what it holds and when it was
    /// counted. Empty when the folder has no stored size.
    /// </summary>
    public string DetailFor(LocationPath folder)
    {
        if (_source?.TryGet(folder) is not { } info)
        {
            return string.Empty;
        }

        var parts = new List<string> { (info.IsLowerBound ? "at least " : string.Empty) + DisplayFormat.Size(info.Bytes) };
        if (info.Files > 0 || info.Folders > 0)
        {
            parts.Add($"{info.Files:N0} {(info.Files == 1 ? "file" : "files")} in {info.Folders:N0} {(info.Folders == 1 ? "folder" : "folders")}");
        }

        if (AgeOf(info) is { } age)
        {
            parts.Add((info.IsStale ? "out of date, counted " : "counted ") + ListingNotice.Age(age));
        }
        else if (info.IsStale)
        {
            parts.Add("out of date");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"just now", "5 min", "3 h", "4 d": the age in the Size cell of a folder on a share.</summary>
    public static string ShortAge(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "now"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min"
        : age.TotalHours < 48 ? $"{(int)age.TotalHours} h"
        : $"{(int)age.TotalDays} d";

    private TimeSpan? AgeOf(FolderSizeInfo info) =>
        info.ComputedAt is { } at ? (_time.GetUtcNow() - at is { Ticks: > 0 } age ? age : TimeSpan.Zero) : null;

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

    /// <summary>
    /// Counts one folder that is selected, shown in the preview pane or heads a drive card, though it is not in the listing (spec §12.7:
    /// "is selected"). Does nothing when a fresh, complete size is known or the folder was already asked for.
    /// </summary>
    public void RequestNow(FileEntry folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (_source is null || !_source.CanCompute(folder.Location) || !_requested.Add(folder.Location))
        {
            return;
        }

        if (_source.TryGet(folder.Location) is { IsStale: false, IsLowerBound: false })
        {
            return;
        }

        _source.Request([folder]);
    }

    private IReadOnlyList<LocationPath> _visible = [];
    private IReadOnlyList<LocationPath> _selected = [];

    /// <summary>The folders on screen now; they are counted first (spec §12.7).</summary>
    public void SetVisible(IReadOnlyList<LocationPath> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _visible = folders;
        PushUrgent();
    }

    /// <summary>The folders selected now; they are counted right after the ones on screen.</summary>
    public void SetSelected(IReadOnlyList<LocationPath> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _selected = folders;
        PushUrgent();
    }

    private void PushUrgent() => _source?.Prioritize([.. _visible, .. _selected]);

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
