using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Folders;

/// <summary>What a cached listing is a listing of: the folder and which items the listing included.</summary>
/// <param name="Location">The folder.</param>
/// <param name="IncludeHidden">True when hidden items were listed.</param>
/// <param name="IncludeSystem">True when system items were listed.</param>
public readonly record struct ListingKey(LocationPath Location, bool IncludeHidden, bool IncludeSystem);

/// <summary>Where a cached listing came from.</summary>
public enum CacheSource
{
    /// <summary>Held in memory from a listing made in this run.</summary>
    Memory,

    /// <summary>Read from the saved listings on disk (from an earlier run, or evicted from memory).</summary>
    Disk,
}

/// <summary>A folder's listing as it was at <see cref="ListedAt"/>. The list is never changed after it is stored.</summary>
/// <param name="Entries">The items, in the order the provider gave them.</param>
/// <param name="ListedAt">When the folder was listed.</param>
/// <param name="Kind">The folder type detected from the items, so the view does not flip when the real listing confirms it.</param>
/// <param name="Source">Memory or disk.</param>
public sealed record CachedListing(IReadOnlyList<FileEntry> Entries, DateTimeOffset ListedAt, FolderKind Kind, CacheSource Source = CacheSource.Memory);

/// <summary>
/// The saved listings on disk, behind the in-memory cache (spec §3.1 budgets, §12 offline cache). It is only asked when memory has
/// nothing, and told about every finished listing; a failure here must never fail a listing, so implementations throw
/// <see cref="IOException"/> at most and the cache absorbs it.
/// </summary>
public interface IListingStore
{
    /// <summary>The saved listing, or null.</summary>
    Task<CachedListing?> LoadAsync(ListingKey key, CancellationToken cancellationToken);

    /// <summary>Saves a finished listing.</summary>
    Task SaveAsync(ListingKey key, CachedListing listing, CancellationToken cancellationToken);

    /// <summary>Records that a saved listing was checked at <paramref name="listedAt"/> and found unchanged, without writing it again.</summary>
    Task TouchAsync(ListingKey key, DateTimeOffset listedAt, CancellationToken cancellationToken);

    /// <summary>Forgets the folder's saved listings, and those of everything below it when <paramref name="includeDescendants"/> is set.</summary>
    Task RemoveAsync(LocationPath folder, bool includeDescendants, CancellationToken cancellationToken);
}

/// <summary>
/// Remembers folder listings so a folder opens at once and is checked in the background (the cache is only ever a first
/// impression; the listing that follows is the truth). Memory first, bounded by the number of entries held (a folder of 50,000
/// files is about 10 to 15 MB), least recently used out first, shared by all windows; an optional <see cref="IListingStore"/> behind it
/// keeps listings between runs. Thread-safe.
/// </summary>
public sealed class ListingCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ListingKey, LinkedListNode<(ListingKey Key, CachedListing Listing)>> _index = [];
    private readonly LinkedList<(ListingKey Key, CachedListing Listing)> _recent = new();
    private int _entryCount;

    /// <summary>Creates the cache.</summary>
    /// <param name="maxEntries">Most entries held in memory, over all folders.</param>
    /// <param name="store">The saved listings behind memory, or null for memory only.</param>
    public ListingCache(int maxEntries = DefaultMaxEntries, IListingStore? store = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        MaxEntries = maxEntries;
        Store = store;
    }

    /// <summary>Entries held when nobody says otherwise: about 50 MB, within the idle-memory budget of spec §3.1.</summary>
    public const int DefaultMaxEntries = 200_000;

    /// <summary>The memory budget in entries.</summary>
    public int MaxEntries { get; }

    /// <summary>The listings kept between runs, or null.</summary>
    public IListingStore? Store { get; }

    /// <summary>Entries held in memory now.</summary>
    public int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entryCount;
            }
        }
    }

    /// <summary>Folders held in memory now.</summary>
    public int FolderCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>The listing of <paramref name="key"/>: from memory, else from the store (and then kept in memory). Null when neither has it.</summary>
    public async ValueTask<CachedListing?> TryGetAsync(ListingKey key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Listing;
            }
        }

        if (Store is null)
        {
            return null;
        }

        CachedListing? saved;
        try
        {
            saved = await Store.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A saved listing that cannot be read is no saved listing.
            return null;
        }

        if (saved is null)
        {
            return null;
        }

        var fromDisk = saved with { Source = CacheSource.Disk };
        Remember(key, fromDisk);
        return fromDisk;
    }

    /// <summary>Stores a finished listing, in memory and, when there is one, in the store (in the background).</summary>
    /// <param name="key">What was listed.</param>
    /// <param name="entries">The items.</param>
    /// <param name="listedAt">When.</param>
    /// <param name="kind">The detected folder type.</param>
    /// <param name="changed">False when the listing is the same as the one the cache already holds, so the store only notes the new time instead of writing megabytes again.</param>
    public void Put(ListingKey key, IReadOnlyList<FileEntry> entries, DateTimeOffset listedAt, FolderKind kind, bool changed = true)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var listing = new CachedListing(entries, listedAt, kind);
        Remember(key, listing);
        if (Store is { } store)
        {
            _ = changed ? SaveQuietlyAsync(store, key, listing) : TouchQuietlyAsync(store, key, listedAt);
        }
    }

    /// <summary>
    /// Forgets what is known about <paramref name="folder"/> (it changed), in memory and in the store; with
    /// <paramref name="includeDescendants"/> everything below it too (it was moved, renamed or deleted).
    /// </summary>
    public void Invalidate(LocationPath folder, bool includeDescendants = false)
    {
        ArgumentNullException.ThrowIfNull(folder);
        lock (_gate)
        {
            foreach (var key in _index.Keys.Where(k => k.Location == folder || (includeDescendants && folder.Contains(k.Location))).ToList())
            {
                Remove(key);
            }
        }

        if (Store is { } store)
        {
            _ = RemoveQuietlyAsync(store, folder, includeDescendants);
        }
    }

    /// <summary>Forgets everything held in memory (the store is kept).</summary>
    public void ClearMemory()
    {
        lock (_gate)
        {
            _index.Clear();
            _recent.Clear();
            _entryCount = 0;
        }
    }

    private void Remember(ListingKey key, CachedListing listing)
    {
        if (listing.Entries.Count > MaxEntries)
        {
            // Bigger than the whole budget: not worth holding, and it would push out everything else.
            lock (_gate)
            {
                Remove(key);
            }

            return;
        }

        lock (_gate)
        {
            Remove(key);
            _index[key] = _recent.AddFirst((key, listing));
            _entryCount += listing.Entries.Count;
            while (_entryCount > MaxEntries && _recent.Last is { } oldest)
            {
                Remove(oldest.Value.Key);
            }
        }
    }

    private void Remove(ListingKey key)
    {
        if (_index.Remove(key, out var node))
        {
            _recent.Remove(node);
            _entryCount -= node.Value.Listing.Entries.Count;
        }
    }

    private static async Task SaveQuietlyAsync(IListingStore store, ListingKey key, CachedListing listing)
    {
        try
        {
            await store.SaveAsync(key, listing, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved this time; the memory copy still works.
        }
    }

    private static async Task TouchQuietlyAsync(IListingStore store, ListingKey key, DateTimeOffset listedAt)
    {
        try
        {
            await store.TouchAsync(key, listedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The saved time stays old; the listing is checked on the next visit anyway.
        }
    }

    private static async Task RemoveQuietlyAsync(IListingStore store, LocationPath folder, bool includeDescendants)
    {
        try
        {
            await store.RemoveAsync(folder, includeDescendants, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The listing it would have replaced is checked on the next visit anyway.
        }
    }
}

/// <summary>The difference between what is on screen (an old listing) and a fresh listing of the same folder.</summary>
/// <param name="Remove">Old entries to take out: gone from the folder, or changed (their new version is in <paramref name="Add"/>).</param>
/// <param name="Add">New entries to put in: new in the folder, or the new version of a changed one.</param>
/// <param name="Added">How many items are new.</param>
/// <param name="Removed">How many items are gone.</param>
/// <param name="Changed">How many items changed (size, date, attributes, kind).</param>
public sealed record ListingDiff(IReadOnlyList<FileEntry> Remove, IReadOnlyList<FileEntry> Add, int Added, int Removed, int Changed)
{
    /// <summary>True when the two listings were the same.</summary>
    public bool IsEmpty => Added == 0 && Removed == 0 && Changed == 0;

    /// <summary>Compares <paramref name="shown"/> with <paramref name="fresh"/>, matching items by location.</summary>
    public static ListingDiff Compute(IReadOnlyList<FileEntry> shown, IReadOnlyList<FileEntry> fresh)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(fresh);
        var old = new Dictionary<LocationPath, FileEntry>(shown.Count);
        foreach (var entry in shown)
        {
            old[entry.Location] = entry;
        }

        var remove = new List<FileEntry>();
        var add = new List<FileEntry>();
        var added = 0;
        var changed = 0;
        var seen = new HashSet<LocationPath>(fresh.Count);
        foreach (var entry in fresh)
        {
            seen.Add(entry.Location);
            if (!old.TryGetValue(entry.Location, out var before))
            {
                add.Add(entry);
                added++;
            }
            else if (Differs(before, entry))
            {
                remove.Add(before);
                add.Add(entry);
                changed++;
            }
        }

        var removed = 0;
        foreach (var entry in shown)
        {
            if (!seen.Contains(entry.Location))
            {
                remove.Add(entry);
                removed++;
            }
        }

        return new ListingDiff(remove, add, added, removed, changed);
    }

    /// <summary>A short summary for the status bar: "3 added, 1 removed, 2 changed".</summary>
    public string Describe()
    {
        var parts = new List<string>(3);
        if (Added > 0)
        {
            parts.Add($"{Added:N0} added");
        }

        if (Removed > 0)
        {
            parts.Add($"{Removed:N0} removed");
        }

        if (Changed > 0)
        {
            parts.Add($"{Changed:N0} changed");
        }

        return string.Join(", ", parts);
    }

    private static bool Differs(FileEntry a, FileEntry b) =>
        a.Kind != b.Kind || a.Size != b.Size || a.Modified != b.Modified || a.Created != b.Created || a.Attributes != b.Attributes
        || !string.Equals(a.Name, b.Name, StringComparison.Ordinal);
}
