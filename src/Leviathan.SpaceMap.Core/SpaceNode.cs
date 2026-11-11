namespace Leviathan.SpaceMap;

/// <summary>
/// A folder or file in a space map (spec §14): what the tree on the left of the view lists and what a rectangle of the treemap stands for. A folder's
/// <see cref="Size"/> is the total of everything below it, kept up to date as children are added, so a map can be drawn from a scan that is still running.
/// Built by one scanner thread and read by the view; call <see cref="Add"/> only from the builder, and read through a snapshot taken by the scanner.
/// </summary>
public sealed class SpaceNode
{
    private readonly List<SpaceNode> _children = [];

    /// <summary>Creates a node.</summary>
    public SpaceNode(string name, bool isFolder, long size, DateTimeOffset modified, SpaceNode? parent = null)
    {
        Name = name;
        IsFolder = isFolder;
        Size = size;
        Modified = modified;
        Parent = parent;
        Files = isFolder ? 0 : 1;
    }

    /// <summary>File or folder name; for the root, its full path.</summary>
    public string Name { get; }

    /// <summary>True for a folder.</summary>
    public bool IsFolder { get; }

    /// <summary>Bytes: a file's size, or the total of everything below a folder.</summary>
    public long Size { get; private set; }

    /// <summary>Files below a folder, or 1 for a file.</summary>
    public int Files { get; private set; }

    /// <summary>Last write time; for a folder, the newest file time below it (so "age" colouring finds stale folders).</summary>
    public DateTimeOffset Modified { get; private set; }

    /// <summary>The folder this node is in, or null for the root.</summary>
    public SpaceNode? Parent { get; }

    /// <summary>The nodes directly inside (empty for a file).</summary>
    public IReadOnlyList<SpaceNode> Children => _children;

    /// <summary>
    /// True for a cloud placeholder (OneDrive and the like): the file shows its full size in Explorer but is not on this disk, so the map counts it as 0 bytes
    /// and the tree says so (spec §14: "cloud placeholders separately so space isn't double-counted").
    /// </summary>
    public bool IsPlaceholder { get; set; }

    /// <summary>For a folder: true once the scan has listed it and added everything in it. A folder that is not listed is listed again when a scan is resumed.</summary>
    public bool Listed { get; set; }

    /// <summary>True when the scan could not read this folder (access denied, share gone), so its size is a lower bound.</summary>
    public bool Unreadable { get; set; }

    /// <summary>The extension without the dot, lower case; empty for folders and files without one.</summary>
    public string Extension
    {
        get
        {
            if (IsFolder)
            {
                return string.Empty;
            }

            var dot = Name.LastIndexOf('.');
            return dot > 0 && dot < Name.Length - 1 ? Name[(dot + 1)..].ToLowerInvariant() : string.Empty;
        }
    }

    /// <summary>The full path of the node, joined from the names above it.</summary>
    public string FullPath
    {
        get
        {
            if (Parent is null)
            {
                return Name;
            }

            var parent = Parent.FullPath;
            return (parent.EndsWith('\\') ? parent : parent + "\\") + Name;
        }
    }

    /// <summary>Adds <paramref name="child"/> and its size to this folder and every folder above it.</summary>
    public void Add(SpaceNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.Parent != this)
        {
            throw new ArgumentException("The child was made for a different parent.", nameof(child));
        }

        _children.Add(child);
        AddUp(child.Size, child.Files, child.Modified);
    }

    /// <summary>Adds bytes and files to this folder and every folder above it (a child grew, or was found to hold more).</summary>
    public void AddUp(long bytes, int files, DateTimeOffset newestModified)
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            node.Size += bytes;
            node.Files += files;
            if (newestModified > node.Modified)
            {
                node.Modified = newestModified;
            }
        }
    }

    /// <summary>Removes <paramref name="child"/> and takes its size off this folder and every folder above it (the item was deleted).</summary>
    public bool Remove(SpaceNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!_children.Remove(child))
        {
            return false;
        }

        for (var node = this; node is not null; node = node.Parent)
        {
            node.Size = Math.Max(0, node.Size - child.Size);
            node.Files = Math.Max(0, node.Files - child.Files);
        }

        return true;
    }

    /// <summary>Puts the children in order of size, largest first. Done once a folder is complete; the treemap needs it and the tree view shows it.</summary>
    public void SortBySize() => _children.Sort(static (a, b) => b.Size.CompareTo(a.Size));

    /// <summary>True when <paramref name="other"/> is this node or lies below it.</summary>
    public bool Contains(SpaceNode other)
    {
        for (var node = other; node is not null; node = node.Parent)
        {
            if (node == this)
            {
                return true;
            }
        }

        return false;
    }
}
