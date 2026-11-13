using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Archives;

/// <summary>
/// Turns the flat list of an archive's entries into the folders and files of one level, the way a folder listing needs it
/// (spec §15.4). Archives often store only files and leave their folders implied, so folders are made up from the paths. Pure
/// logic: no I/O, so it is tested without any archive.
/// </summary>
public static class ArchiveFolder
{
    /// <summary>
    /// The items directly inside <paramref name="inner"/> (segments below the archive). Returns null when no such folder exists in the
    /// archive, so the caller can say "not found" instead of showing an empty folder.
    /// </summary>
    /// <param name="archiveLocation">The archive file's own location; item locations are built below it.</param>
    /// <param name="inner">Segments of the folder being listed; empty for the top level.</param>
    /// <param name="entries">All entries of the archive.</param>
    /// <param name="fallbackTime">Shown for items whose time the archive does not store (the archive file's own time).</param>
    public static IReadOnlyList<FileEntry>? Children(LocationPath archiveLocation, IReadOnlyList<string> inner, IReadOnlyList<ArchiveEntryInfo> entries, DateTimeOffset fallbackTime)
    {
        ArgumentNullException.ThrowIfNull(archiveLocation);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(entries);

        var prefix = inner.Count == 0 ? string.Empty : string.Join('/', inner) + "/";
        var found = inner.Count == 0;
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = entry.Path;
            if (path.Length == 0)
            {
                continue;
            }

            if (prefix.Length > 0)
            {
                if (entry.IsDirectory && path.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    continue;
                }

                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
            }

            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                // Something deeper: the first segment is a folder here, whether or not it has an entry of its own.
                var name = rest[..slash];
                if (name.Length > 0 && !items.ContainsKey(name))
                {
                    items[name] = new Item(name, true, 0, null, null);
                }

                continue;
            }

            if (rest.Length == 0)
            {
                continue;
            }

            items[rest] = entry.IsDirectory
                ? new Item(rest, true, 0, entry.Modified, entry.Created)
                : new Item(rest, false, entry.Size, entry.Modified, entry.Created);
        }

        if (!found)
        {
            return null;
        }

        var parent = inner.Aggregate(archiveLocation, (location, segment) => location.Combine(segment));
        var result = new List<FileEntry>(items.Count);
        foreach (var item in items.Values)
        {
            // An archive made on Linux can hold names Windows cannot spell (a colon, a trailing space). They cannot be addressed here,
            // so they are left out rather than failing the whole folder; extracting the archive with 7-Zip still gets them.
            if (!TryCombine(parent, item.Name, out var location))
            {
                continue;
            }

            var modified = item.Modified ?? fallbackTime;
            result.Add(new FileEntry(
                item.Name,
                location,
                item.IsFolder ? FileEntryKind.Folder : FileEntryKind.File,
                item.Size,
                item.Created ?? modified,
                modified,
                item.IsFolder ? FileAttributes.Directory : FileAttributes.Normal));
        }

        return result;
    }

    /// <summary>The file entry at <paramref name="innerPath"/> (forward slashes), or null.</summary>
    public static ArchiveEntryInfo? Find(IReadOnlyList<ArchiveEntryInfo> entries, string innerPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.FirstOrDefault(e => !e.IsDirectory && e.Path.Equals(innerPath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryCombine(LocationPath parent, string name, out LocationPath location)
    {
        try
        {
            location = parent.Combine(name);
            return true;
        }
        catch (ArgumentException)
        {
            location = parent;
            return false;
        }
    }

    private sealed record Item(string Name, bool IsFolder, long Size, DateTimeOffset? Modified, DateTimeOffset? Created);
}
