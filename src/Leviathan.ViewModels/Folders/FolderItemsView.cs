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
    private IReadOnlyList<SortKey> _sort = [new SortKey("name")];
    private FolderPlacement _folders = FolderPlacement.First;
    private string? _groupBy;
    private FilterQuery _filter = FilterQuery.Empty;
    private IEntryMetadata _metadata = NoEntryMetadata.Instance;
    private Comparison<FileEntry> _comparison;

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
            Recompute();
        }
    }

    /// <summary>The visible rows in display order (group by group when grouped).</summary>
    public SnapshotList<FileEntry> Rows { get; } = new();

    /// <summary>Groups, empty unless <see cref="GroupBy"/> is set.</summary>
    public IReadOnlyList<FileGroup> Groups { get; private set; } = [];

    /// <summary>Entries in the folder before filtering.</summary>
    public int TotalCount => _all.Count;

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
        _sort = settings.Sort.Count > 0 ? settings.Sort : [new SortKey("name")];
        _folders = settings.Folders;
        _groupBy = settings.GroupBy is not null && FileColumns.TryGet(settings.GroupBy, out var column) && column.Group is not null ? column.Id : null;
        _comparison = BuildComparison();
        if (publish)
        {
            Recompute();
            OnPropertyChanged(nameof(Sort));
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                var added = e.NewItems.Cast<FileEntry>().ToList();
                _all.AddRange(added);
                MergeBatch(added);
                break;
            case NotifyCollectionChangedAction.Reset:
                _all.Clear();
                _sorted = [];
                Publish();
                break;
            default:
                _all.Clear();
                _all.AddRange(((RangeObservableCollection<FileEntry>)sender!));
                Recompute();
                break;
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
        var list = _all.Where(Passes).ToList();
        list.Sort(_comparison);
        _sorted = list;
        Publish();
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
                var result = column.Compare(a, b);
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
