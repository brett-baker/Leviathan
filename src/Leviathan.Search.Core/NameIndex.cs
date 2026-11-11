using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Search;

/// <summary>One item to put in the <see cref="NameIndex"/>: what a directory listing tells (spec §13 "NAS index").</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="Size">Size in bytes (0 for folders).</param>
/// <param name="ModifiedTicks">Last write time as UTC ticks.</param>
/// <param name="Attributes">File-system attributes.</param>
public readonly record struct IndexedChild(string Name, bool IsFolder, long Size, long ModifiedTicks, FileAttributes Attributes);

/// <summary>
/// An in-memory index of file and folder names (spec §13): all names sit in one block of text, so finding a word in a million names is one
/// vectorised scan of memory, not a million string comparisons. Entries have stable ids, a parent, a size and a time; a path is rebuilt from the parent
/// chain when a hit is shown. The index is filled by a crawler (<see cref="IndexCrawler"/>), one folder at a time through
/// <see cref="ReplaceChildren"/>, so the same structure serves local disks without an indexer service and network shares. Thread-safe: one writer or
/// many readers at a time; a reader holds the lock only for the scan, never while it waits on anything else.
/// </summary>
public sealed class NameIndex : IDisposable
{
    private const byte Folder = 1;
    private const byte Dead = 2;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private char[] _names = new char[1 << 16];
    private int _namesLength;
    private int[] _nameStart = new int[1 << 12];
    private int[] _parent = new int[1 << 12];
    private int[] _firstChild = new int[1 << 12];
    private int[] _nextSibling = new int[1 << 12];
    private long[] _size = new long[1 << 12];
    private long[] _modified = new long[1 << 12];
    private int[] _attributes = new int[1 << 12];
    private byte[] _flags = new byte[1 << 12];
    private int _count;
    private int _dead;
    private readonly List<int> _roots = [];

    /// <summary>Entries in the index, folders included, roots excluded.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _count - _dead - _roots.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>The ids of the roots (the drives and shares added with <see cref="AddRoot"/>).</summary>
    public IReadOnlyList<int> Roots
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return [.. _roots];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Adds a root folder (a drive or a share) and returns its id; adding the same root again returns the existing id.</summary>
    public int AddRoot(LocationPath root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var text = root.ToString();
        _lock.EnterWriteLock();
        try
        {
            foreach (var existing in _roots)
            {
                if (string.Equals(NameOf(existing), text, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }

            var id = NewEntry(text, -1, Folder, 0, 0, (int)FileAttributes.Directory);
            _roots.Add(id);
            return id;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Finds the id of a folder or file that is in the index, or -1.</summary>
    public int Find(LocationPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _lock.EnterReadLock();
        try
        {
            return FindLocked(path);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>The full path of an entry.</summary>
    public LocationPath PathOf(int id)
    {
        _lock.EnterReadLock();
        try
        {
            return LocationPath.Parse(PathTextOf(id));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Makes the children of <paramref name="folder"/> exactly <paramref name="children"/>: new ones are added, ones that are still there keep their id and
    /// get the new size and time, ones that are gone are removed with everything below them. Returns the id of every child, in the same order, so a crawler
    /// can go on into the folders.
    /// </summary>
    public int[] ReplaceChildren(int folder, IReadOnlyList<IndexedChild> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var ids = new int[children.Count];
        _lock.EnterWriteLock();
        try
        {
            var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var c = _firstChild[folder]; c >= 0; c = _nextSibling[c])
            {
                existing[NameOf(c)] = c;
            }

            var kept = new HashSet<int>();
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var flags = child.IsFolder ? Folder : (byte)0;
                // Same name (exactly, so a rename that only changes case makes a new entry) and same kind: keep the entry and its id.
                if (existing.TryGetValue(child.Name, out var id) && (_flags[id] & Folder) == (flags & Folder) && string.Equals(NameOf(id), child.Name, StringComparison.Ordinal))
                {
                    _size[id] = child.Size;
                    _modified[id] = child.ModifiedTicks;
                    _attributes[id] = (int)child.Attributes;
                }
                else
                {
                    id = NewEntry(child.Name, folder, flags, child.Size, child.ModifiedTicks, (int)child.Attributes);
                }

                kept.Add(id);
                ids[i] = id;
            }

            foreach (var old in existing.Values.Where(v => !kept.Contains(v)))
            {
                Kill(old);
            }

            Relink(folder, ids);
            return ids;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Removes an entry and everything below it (a root is removed with its whole tree).</summary>
    public void Remove(int id)
    {
        _lock.EnterWriteLock();
        try
        {
            var parent = _parent[id];
            Kill(id);
            if (parent >= 0)
            {
                Relink(parent, [.. Children(parent).Where(c => (_flags[c] & Dead) == 0)]);
            }
            else
            {
                _roots.Remove(id);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>The live children of a folder.</summary>
    public IReadOnlyList<int> ChildrenOf(int folder)
    {
        _lock.EnterReadLock();
        try
        {
            return [.. Children(folder).Where(c => (_flags[c] & Dead) == 0)];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Name, kind and size of an entry, without building its path.</summary>
    public IndexedChild Describe(int id)
    {
        _lock.EnterReadLock();
        try
        {
            return new IndexedChild(NameOf(id), (_flags[id] & Folder) != 0, _size[id], _modified[id], (FileAttributes)_attributes[id]);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Looks for entries under <paramref name="roots"/> (or anywhere when empty) whose name contains <paramref name="literal"/>, ignoring case, and builds them
    /// as <see cref="FileEntry"/> values, at most <paramref name="limit"/> of them. See <see cref="CandidatePage"/> for the version that hands them over a page at a time.
    /// </summary>
    public List<FileEntry> Candidates(string? literal, IReadOnlyList<LocationPath> roots, Func<IndexedChild, bool>? keep, int limit, CancellationToken cancellationToken)
    {
        var all = new List<FileEntry>();
        var cursor = new IndexCursor();
        while (all.Count < limit && !cursor.IsDone)
        {
            all.AddRange(CandidatePage(literal, roots, keep, ref cursor, Math.Min(4096, limit - all.Count), cancellationToken));
        }

        return all;
    }

    /// <summary>
    /// The next page of candidates: up to <paramref name="pageSize"/> entries under <paramref name="roots"/> (or anywhere when empty) whose name contains
    /// <paramref name="literal"/>, ignoring case, built as <see cref="FileEntry"/> values. With no literal every entry is a candidate; <paramref name="keep"/> drops
    /// candidates by name, kind and size before the cost of building them (it must keep a superset of what the query keeps). The lock is held for one page only, so a
    /// search can start answering after the first page and the index can be updated between pages; entries never move, so the cursor stays valid.
    /// </summary>
    public List<FileEntry> CandidatePage(string? literal, IReadOnlyList<LocationPath> roots, Func<IndexedChild, bool>? keep, ref IndexCursor cursor, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var found = new List<FileEntry>();
        _lock.EnterReadLock();
        try
        {
            var rootIds = new HashSet<int>();
            foreach (var r in roots)
            {
                var rid = FindLocked(r);
                if (rid >= 0)
                {
                    rootIds.Add(rid);
                }
            }

            if (roots.Count > 0 && rootIds.Count == 0)
            {
                cursor = cursor with { IsDone = true };
                return found;
            }

            var folderPaths = new Dictionary<int, LocationPath>();

            bool Under(int id)
            {
                if (rootIds.Count == 0)
                {
                    return true;
                }

                for (var p = _parent[id]; p >= 0; p = _parent[p])
                {
                    if (rootIds.Contains(p))
                    {
                        return true;
                    }
                }

                return false;
            }

            void Consider(int id)
            {
                if ((_flags[id] & Dead) != 0 || _parent[id] < 0 || !Under(id))
                {
                    return;
                }

                var child = new IndexedChild(NameOf(id), (_flags[id] & Folder) != 0, _size[id], _modified[id], (FileAttributes)_attributes[id]);
                if (keep is null || keep(child))
                {
                    found.Add(Build(id, child, folderPaths));
                }
            }

            if (string.IsNullOrEmpty(literal))
            {
                var id = cursor.Next;
                for (; id < _count && found.Count < pageSize; id++)
                {
                    if ((id & 0x3FFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    Consider(id);
                }

                cursor = new IndexCursor(id, id >= _count);
                return found;
            }

            var from = cursor.Next;
            var lastId = cursor.LastId;
            while (from < _namesLength && found.Count < pageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var at = _names.AsSpan(from, _namesLength - from).IndexOf(literal, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    from = _namesLength;
                    break;
                }

                var id = EntryAt(from + at);
                from = id >= 0 && id + 1 < _count ? _nameStart[id + 1] : _namesLength;
                if (id >= 0 && id != lastId)
                {
                    lastId = id;
                    Consider(id);
                }
            }

            cursor = new IndexCursor(from, from >= _namesLength, lastId);
            return found;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    // ---- internals (callers hold the lock) ----

    private int FindLocked(LocationPath path)
    {
        foreach (var root in _roots)
        {
            var rootPath = LocationPath.Parse(NameOf(root));
            if (rootPath == path)
            {
                return root;
            }

            if (rootPath.Contains(path))
            {
                var id = root;
                for (var i = rootPath.Segments.Count; i < path.Segments.Count && id >= 0; i++)
                {
                    id = FindChild(id, path.Segments[i]);
                }

                return id;
            }
        }

        return -1;
    }

    private int FindChild(int folder, string name)
    {
        for (var c = _firstChild[folder]; c >= 0; c = _nextSibling[c])
        {
            if ((_flags[c] & Dead) == 0 && NameOf(c).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return -1;
    }

    private IEnumerable<int> Children(int folder)
    {
        for (var c = _firstChild[folder]; c >= 0; c = _nextSibling[c])
        {
            yield return c;
        }
    }

    private string NameOf(int id)
    {
        var start = _nameStart[id];
        var end = Array.IndexOf(_names, '\0', start, _namesLength - start);
        return new string(_names, start, (end < 0 ? _namesLength : end) - start);
    }

    private string PathTextOf(int id)
    {
        var parts = new List<string>();
        for (var p = id; p >= 0; p = _parent[p])
        {
            parts.Add(NameOf(p));
        }

        parts.Reverse();
        var root = parts[0];
        return parts.Count == 1 ? root : (root.EndsWith('\\') ? root : root + "\\") + string.Join('\\', parts.Skip(1));
    }

    private FileEntry Build(int id, IndexedChild child, Dictionary<int, LocationPath> folderPaths)
    {
        // Entries of one folder arrive together, so the folder's path is built once and each name is added to it.
        var parent = _parent[id];
        if (!folderPaths.TryGetValue(parent, out var folder))
        {
            folder = LocationPath.Parse(PathTextOf(parent));
            folderPaths[parent] = folder;
        }

        var location = folder.Combine(child.Name);
        var modified = new DateTimeOffset(Math.Clamp(child.ModifiedTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), TimeSpan.Zero);
        var kind = child.IsFolder ? FileEntryKind.Folder : FileEntryKind.File;
        return new FileEntry(child.Name, location, kind, child.IsFolder ? 0 : child.Size, modified, modified, child.Attributes);
    }

    private int EntryAt(int position)
    {
        var low = 0;
        var high = _count - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            if (_nameStart[mid] <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        // A match inside the separator after a name, or in a name that has since been replaced, belongs to no live entry.
        return result;
    }

    private int AppendName(string name)
    {
        var need = name.Length + 1;
        if (_namesLength + need > _names.Length)
        {
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesLength + need));
        }

        var start = _namesLength;
        name.CopyTo(0, _names, start, name.Length);
        _names[start + name.Length] = '\0';
        _namesLength += need;
        return start;
    }

    private int NewEntry(string name, int parent, byte flags, long size, long modified, int attributes)
    {
        if (_count == _nameStart.Length)
        {
            var length = _count * 2;
            Array.Resize(ref _nameStart, length);
            Array.Resize(ref _parent, length);
            Array.Resize(ref _firstChild, length);
            Array.Resize(ref _nextSibling, length);
            Array.Resize(ref _size, length);
            Array.Resize(ref _modified, length);
            Array.Resize(ref _attributes, length);
            Array.Resize(ref _flags, length);
        }

        var id = _count++;
        _nameStart[id] = AppendName(name);
        _parent[id] = parent;
        _firstChild[id] = -1;
        _nextSibling[id] = -1;
        _size[id] = size;
        _modified[id] = modified;
        _attributes[id] = attributes;
        _flags[id] = flags;
        return id;
    }

    private void Kill(int id)
    {
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if ((_flags[current] & Dead) != 0)
            {
                continue;
            }

            _flags[current] |= Dead;
            _dead++;
            for (var c = _firstChild[current]; c >= 0; c = _nextSibling[c])
            {
                stack.Push(c);
            }
        }
    }

    // Makes the sibling chain of a folder follow `ids`, new entries included.
    private void Relink(int folder, IReadOnlyList<int> ids)
    {
        var previous = -1;
        _firstChild[folder] = -1;
        foreach (var id in ids)
        {
            _parent[id] = folder;
            _nextSibling[id] = -1;
            if (previous < 0)
            {
                _firstChild[folder] = id;
            }
            else
            {
                _nextSibling[previous] = id;
            }

            previous = id;
        }
    }
}

/// <summary>Where a paged scan of the <see cref="NameIndex"/> stands. Start with <c>new IndexCursor()</c>; <see cref="IsDone"/> says when the scan has seen everything.</summary>
/// <param name="Next">The next position to look at.</param>
/// <param name="IsDone">True when nothing is left.</param>
/// <param name="LastId">The last entry returned for a word search, so one name is not returned twice.</param>
public readonly record struct IndexCursor(int Next = 0, bool IsDone = false, int LastId = -1);
