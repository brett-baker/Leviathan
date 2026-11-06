using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Folders;

/// <summary>A heading and its rows in a grouped view. Collapsing is a view state; the rows stay in <see cref="Items"/>.</summary>
public sealed partial class FileGroup : ObservableObject
{
    [ObservableProperty]
    private bool _isCollapsed;

    /// <summary>Creates a group.</summary>
    public FileGroup(GroupBucket bucket, IReadOnlyList<FileEntry> items)
    {
        Bucket = bucket;
        Items = items;
    }

    /// <summary>The bucket this group represents.</summary>
    public GroupBucket Bucket { get; }

    /// <summary>Heading text.</summary>
    public string Title => Bucket.Title;

    /// <summary>The rows, in sort order.</summary>
    public IReadOnlyList<FileEntry> Items { get; }
}

/// <summary>
/// What the file view shows: the folder's entries filtered, sorted and optionally grouped (spec §8.3). It follows the
/// <see cref="RangeObservableCollection{T}"/> of a <see cref="FolderViewModel"/>, merging each arriving batch into the sorted
/// list instead of re-sorting everything, so a 100,000-item folder stays responsive while it streams in (spec §3.1).
/// Always used from the UI thread.
/// </summary>
public sealed partial class FolderItemsView : ObservableObject
{
    private readonly List<FileEntry> _all = [];
    private readonly HashSet<string> _collapsed = [];
    private readonly TimeProvider _time;
    private List<FileEntry> _sorted = [];

    // Every entry in sort order, unfiltered, kept while neither the entries nor the sort changed: a filter change (hidden items, the
    // filter box) then only filters this list, where sorting 40,000 rows afresh took 300 to 500 ms on the UI thread.
    private List<FileEntry>? _allSorted;
    private Comparison<FileEntry>? _allSortedBy;
    private int _version;
    private int _allSortedVersion = -1;
    private IReadOnlyList<SortKey> _sort = [new SortKey("name")];
    private FolderPlacement _folders = FolderPlacement.First;
    private string? _groupBy;
    private FilterQuery _filter = FilterQuery.Empty;
    private IEntryMetadata _metadata = NoEntryMetadata.Instance;
    private Comparison<FileEntry> _comparison;
    private Func<FileEntry, FolderMetric, long?>? _metrics;
    private Func<FileEntry, MediaColumnKind, MediaSortKey>? _mediaKeys;

    /// <summary>Creates a view over <paramref name="source"/>, applying <paramref name="settings"/>.</summary>
    public FolderItemsView(RangeObservableCollection<FileEntry> source, FolderViewSettings? settings = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _time = time ?? TimeProvider.System;
        _comparison = BuildComparison();
        Apply(settings ?? FolderViewSettings.Default, publish: false);
        source.CollectionChanged += OnSourceChanged;
        if (source.Count > 0)
        {
            _all.AddRange(source);
            Count(_all, 1);
            Recompute();
        }
    }

    /// <summary>The visible rows in display order (group by group when grouped).</summary>
    public SnapshotList<FileEntry> Rows { get; } = new();

    /// <summary>Groups, empty unless <see cref="GroupBy"/> is set.</summary>
    public IReadOnlyList<FileGroup> Groups { get; private set; } = [];

    /// <summary>Entries in the folder before filtering.</summary>
    public int TotalCount => _all.Count;

    /// <summary>How many of <see cref="TotalCount"/> are folders (status bar, spec §16).</summary>
    public int FolderCount { get; private set; }

    /// <summary>How many of <see cref="TotalCount"/> are hidden. Hidden items the listing left out are not counted.</summary>
    public int HiddenCount { get; private set; }

    /// <summary>Entries after filtering ("312 of 1,284" in the status bar, spec §16).</summary>
    public int ShownCount => _sorted.Count;

    /// <summary>Sort criteria, primary first.</summary>
    public IReadOnlyList<SortKey> Sort => _sort;

    /// <summary>Where folders sort.</summary>
    public FolderPlacement Folders => _folders;

    /// <summary>Column id rows are grouped by, or null.</summary>
    public string? GroupBy => _groupBy;

    /// <summary>The active filter.</summary>
    public FilterQuery Filter => _filter;

    /// <summary>Applies layout-independent settings (sort, folder placement, grouping) from the folder's resolved view.</summary>
    public void Apply(FolderViewSettings settings) => Apply(settings, publish: true);

    /// <summary>
    /// Header click: sorts by <paramref name="columnId"/>. A repeat click flips the direction. With <paramref name="additive"/>
    /// (Shift+click) the column becomes a secondary criterion, or flips if it already is one (spec §8.3).
    /// </summary>
    public void SortBy(string columnId, bool additive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnId);
        var keys = _sort.ToList();
        var index = keys.FindIndex(k => k.Column.Equals(columnId, StringComparison.OrdinalIgnoreCase));
        if (additive)
        {
            if (index >= 0)
            {
                keys[index] = keys[index] with { Direction = Flip(keys[index].Direction) };
            }
            else
            {
                keys.Add(new SortKey(columnId));
            }
        }
        else
        {
            var direction = index == 0 ? Flip(keys[0].Direction) : SortDirection.Ascending;
            keys = [new SortKey(columnId, direction)];
        }

        _sort = keys;
        _comparison = BuildComparison();
        Recompute();
        OnPropertyChanged(nameof(Sort));
    }

    /// <summary>Replaces the sort criteria (the Sort menu, which picks a column and a direction separately).</summary>
    public void SetSort(IReadOnlyList<SortKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _sort = keys.Count > 0 ? keys : [new SortKey("name")];
        _comparison = BuildComparison();
        Recompute();
        OnPropertyChanged(nameof(Sort));
    }

    /// <summary>Sets where folders sort.</summary>
    public void SetFolderPlacement(FolderPlacement placement)
    {
        _folders = placement;
        _comparison = BuildComparison();
        Recompute();
        OnPropertyChanged(nameof(Folders));
    }

    /// <summary>Groups by a column, or ungroups with null. Columns that cannot group are ignored.</summary>
    public void SetGroupBy(string? columnId)
    {
        _groupBy = columnId is not null && FileColumns.TryGet(columnId, out var column) && column.Group is not null ? column.Id : null;
        Recompute();
        OnPropertyChanged(nameof(GroupBy));
    }

    /// <summary>Sets the filter text; an empty string shows everything.</summary>
    public void SetFilter(string? text)
    {
        _filter = string.IsNullOrWhiteSpace(text) ? FilterQuery.Empty : FilterQuery.Parse(text);
        Recompute();
        OnPropertyChanged(nameof(Filter));
    }

    /// <summary>
    /// Supplies the folder-size numbers the Size, Own size, Items, Total files and Total folders columns sort folders by (spec §12.7);
    /// a folder whose number is not known yet sorts before the ones that are.
    /// </summary>
    public void SetFolderMetrics(Func<FileEntry, FolderMetric, long?>? metrics)
    {
        _metrics = metrics;
        _comparison = BuildComparison();
        if (SortsByMetric)
        {
            Recompute();
        }
    }

    /// <summary>
    /// Supplies what the media columns sort files by (spec §8.2); a file that has not been read yet sorts before the ones that have.
    /// </summary>
    public void SetMediaKeys(Func<FileEntry, MediaColumnKind, MediaSortKey>? keys)
    {
        _mediaKeys = keys;
        _comparison = BuildComparison();
        if (SortsByMedia)
        {
            Recompute();
        }
    }

    /// <summary>True when the sort uses a media column, so information that arrives can move a row.</summary>
    public bool SortsByMedia => _sort.Any(k => FileColumns.TryGet(k.Column, out var c) && c.Media is not null);

    /// <summary>Media information arrived: sorts again when the sort depends on it and a row moved. Does nothing otherwise.</summary>
    public void ResortByMedia()
    {
        if (!SortsByMedia)
        {
            return;
        }

        _comparison = BuildComparison();
        if (IsSorted(_sorted, _comparison))
        {
            return;
        }

        Recompute();
    }

    /// <summary>True when the sort uses a folder-size number, so a number that arrives can move a row.</summary>
    public bool SortsByMetric => _sort.Any(k => FileColumns.TryGet(k.Column, out var c) && c.Metric is not null);

    /// <summary>A folder-size number arrived: sorts again when the sort depends on one. Does nothing otherwise.</summary>
    public void ResortByMetrics()
    {
        if (!SortsByMetric)
        {
            return;
        }

        // The comparison object changes so the sorted copy kept for filter changes is rebuilt. Rows that are still in order are left
        // alone: most arrivals (a progress tick, a number that does not move the folder) change nothing on screen.
        _comparison = BuildComparison();
        if (IsSorted(_sorted, _comparison))
        {
            return;
        }

        Recompute();
    }

    /// <summary>Supplies tags and durations for filter terms that need them.</summary>
    public void SetMetadata(IEntryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        Recompute();
    }

    private static SortDirection Flip(SortDirection direction) =>
        direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;

    private void Apply(FolderViewSettings settings, bool publish)
    {
        var sort = settings.Sort.Count > 0 ? settings.Sort : [new SortKey("name")];
        var groupBy = settings.GroupBy is not null && FileColumns.TryGet(settings.GroupBy, out var column) && column.Group is not null ? column.Id : null;

        // A view that did not change is not worth sorting every row again. This runs at the end of every listing (the folder type
        // is detected and the view resolved again), when 50,000 rows sorted afresh on the UI thread is a stall for nothing.
        var unchanged = _comparison is not null && _sort.SequenceEqual(sort) && _folders == settings.Folders && _groupBy == groupBy;
        _sort = sort;
        _folders = settings.Folders;
        _groupBy = groupBy;
        if (unchanged)
        {
            return;
        }

        _comparison = BuildComparison();
        if (publish)
        {
            Recompute();
            OnPropertyChanged(nameof(Sort));
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _version++;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                var added = e.NewItems.Cast<FileEntry>().ToList();
                _all.AddRange(added);
                Count(added, 1);
                MergeBatch(added);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                var gone = new HashSet<FileEntry>(e.OldItems.Cast<FileEntry>());
                _all.RemoveAll(gone.Contains);
                Count(gone, -1);
                _sorted = [.. _sorted.Where(entry => !gone.Contains(entry))];
                Publish();
                break;
            case NotifyCollectionChangedAction.Reset:
                _all.Clear();
                FolderCount = 0;
                HiddenCount = 0;
                _sorted = [];
                Publish();
                break;
            default:
                _all.Clear();
                _all.AddRange(((RangeObservableCollection<FileEntry>)sender!));
                FolderCount = 0;
                HiddenCount = 0;
                Count(_all, 1);
                Recompute();
                break;
        }
    }

    private void Count(IEnumerable<FileEntry> entries, int sign)
    {
        foreach (var entry in entries)
        {
            FolderCount += entry.Kind == FileEntryKind.Folder ? sign : 0;
            HiddenCount += entry.IsHidden ? sign : 0;
        }
    }

    private void MergeBatch(List<FileEntry> added)
    {
        var batch = added.Where(Passes).ToList();
        if (batch.Count > 0)
        {
            batch.Sort(_comparison);
            _sorted = Merge(_sorted, batch, _comparison);
        }

        Publish();
    }

    private void Recompute()
    {
        if (_allSorted is null || _allSortedVersion != _version || !ReferenceEquals(_allSortedBy, _comparison))
        {
            var all = new List<FileEntry>(_all);

            // A folder usually arrives nearly in order, and checking costs one pass; sorting costs far more.
            if (!IsSorted(all, _comparison))
            {
                all.Sort(_comparison);
            }

            _allSorted = all;
            _allSortedVersion = _version;
            _allSortedBy = _comparison;
        }

        // Filtering a sorted list keeps it sorted.
        _sorted = _filter.IsEmpty ? new List<FileEntry>(_allSorted) : _allSorted.Where(Passes).ToList();
        Publish();
    }

    private static bool IsSorted(List<FileEntry> list, Comparison<FileEntry> comparison)
    {
        for (var i = 1; i < list.Count; i++)
        {
            if (comparison(list[i - 1], list[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool Passes(FileEntry entry) => _filter.IsEmpty || _filter.Matches(entry, _metadata);

    /// <summary>
    /// Merges a sorted batch into the sorted list. Each batch item is placed by binary search and the runs of the big list
    /// between items are block-copied, so a 256-item batch into 100,000 rows does about 4,000 comparisons, not 100,000.
    /// </summary>
    private static List<FileEntry> Merge(List<FileEntry> left, List<FileEntry> right, Comparison<FileEntry> comparison)
    {
        var merged = new List<FileEntry>(left.Count + right.Count);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(left);
        var start = 0;
        foreach (var item in right)
        {
            // First index at or after 'start' whose element sorts after the item (keeps arrival order for equal items).
            int low = start, high = left.Count;
            while (low < high)
            {
                var mid = low + ((high - low) / 2);
                if (comparison(left[mid], item) <= 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            merged.AddRange(span[start..low]);
            merged.Add(item);
            start = low;
        }

        merged.AddRange(span[start..]);
        return merged;
    }

    private Comparison<FileEntry> BuildComparison()
    {
        var keys = _sort.Select(k => (Column: FileColumns.TryGet(k.Column, out var c) ? c : FileColumns.Name, k.Direction)).ToArray();
        var folders = _folders;
        var metrics = _metrics;
        var mediaKeys = _mediaKeys;
        return (a, b) =>
        {
            if (folders != FolderPlacement.Mixed)
            {
                var aFolder = FileColumns.IsFolderLike(a);
                if (aFolder != FileColumns.IsFolderLike(b))
                {
                    return (aFolder ? -1 : 1) * (folders == FolderPlacement.First ? 1 : -1);
                }
            }

            foreach (var (column, direction) in keys)
            {
                var result = column.Metric is { } metric && metrics is not null
                    ? CompareMetric(a, b, metric, metrics)
                    : column.Media is { } media && mediaKeys is not null
                        ? MediaSortKey.Compare(mediaKeys(a, media), mediaKeys(b, media))
                        : column.Compare(a, b);
                if (result != 0)
                {
                    return direction == SortDirection.Ascending ? result : -result;
                }
            }

            // A total order keeps the incremental merge identical to a full sort.
            var byName = FileColumns.Name.Compare(a, b);
            return byName != 0 ? byName : string.CompareOrdinal(a.Location.Value, b.Location.Value);
        };
    }

    // A file is its own size; a folder is what the folder-size service has. A number not known yet sorts before every known one.
    private static int CompareMetric(FileEntry a, FileEntry b, FolderMetric metric, Func<FileEntry, FolderMetric, long?> metrics) =>
        (ValueOf(a, metric, metrics) ?? -1).CompareTo(ValueOf(b, metric, metrics) ?? -1);

    private static long? ValueOf(FileEntry entry, FolderMetric metric, Func<FileEntry, FolderMetric, long?> metrics) =>
        FileColumns.IsFolderLike(entry)
            ? metrics(entry, metric)
            : metric is FolderMetric.TotalBytes or FolderMetric.OwnBytes ? entry.Size : null;

    private void Publish()
    {
        Groups = _groupBy is null ? [] : BuildGroups();
        Rows.Replace(Groups.Count > 0 ? [.. Groups.SelectMany(g => g.Items)] : _sorted);
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(ShownCount));
        OnPropertyChanged(nameof(TotalCount));
    }

    private FileGroup NewGroup(GroupBucket bucket, List<FileEntry> items)
    {
        // Collapsed headings stay collapsed when rows arrive or the sort changes.
        var group = new FileGroup(bucket, items) { IsCollapsed = _collapsed.Contains(bucket.Key) };
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileGroup.IsCollapsed))
            {
                if (group.IsCollapsed)
                {
                    _collapsed.Add(bucket.Key);
                }
                else
                {
                    _collapsed.Remove(bucket.Key);
                }
            }
        };
        return group;
    }

    private List<FileGroup> BuildGroups()
    {
        FileColumns.TryGet(_groupBy!, out var column);
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var buckets = new Dictionary<GroupBucket, List<FileEntry>>();
        foreach (var entry in _sorted)
        {
            var bucket = column.Group!(entry, now, zone);
            if (!buckets.TryGetValue(bucket, out var items))
            {
                buckets[bucket] = items = [];
            }

            items.Add(entry);
        }

        var ordered = buckets.Keys
            .OrderBy(b => b.Order)
            .ThenBy(b => b.Key, Core.Text.NaturalStringComparer.Instance);
        var descending = _sort.Count > 0 && _sort[0].Column.Equals(column.Id, StringComparison.OrdinalIgnoreCase) && _sort[0].Direction == SortDirection.Descending;
        var list = ordered.Select(b => NewGroup(b, buckets[b])).ToList();
        if (descending)
        {
            list.Reverse();
        }

        return list;
    }
}
