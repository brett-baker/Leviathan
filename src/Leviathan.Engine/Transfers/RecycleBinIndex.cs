using System.IO.Abstractions;
using System.Text;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// Finds where Windows put an item it just recycled (spec §12.5). Each recycled item is a pair in <c>X:\$Recycle.Bin\&lt;user SID&gt;</c>:
/// <c>$I…</c> records the original path and the deletion time, <c>$R…</c> is the item itself. <c>IFileOperation</c> does not hand
/// back that place, but undo needs it, so it is read from the <c>$I</c> files. The layout has been the same since Windows Vista
/// (version 1) and Windows 10 (version 2), and tools such as Explorer's Restore and forensic readers rely on it.
/// </summary>
public static class RecycleBinIndex
{
    /// <summary>The Recycle Bin folder at the root of a drive.</summary>
    public const string FolderName = "$Recycle.Bin";

    private const string InfoPrefix = "$I";
    private const string DataPrefix = "$R";

    /// <summary>
    /// Returns the recycled copy of <paramref name="original"/> (the <c>$R</c> item) deleted at or after <paramref name="notBefore"/>,
    /// or null when there is none, for example on a network drive that deletes for good. The newest match wins, so an older item
    /// of the same name is never mistaken for this one.
    /// </summary>
    public static LocationPath? FindRecycled(IFileSystem fileSystem, LocationPath original, DateTimeOffset notBefore)
    {
        ArgumentNullException.ThrowIfNull(original);
        return FindRecycledMany(fileSystem, [original], notBefore).TryGetValue(original.Value, out var found) ? found : null;
    }

    /// <summary>
    /// <see cref="FindRecycled"/> for many items with one reading of each bin, which matters when tens of thousands of files were just
    /// recycled: reading every <c>$I</c> record once per file would read the bin tens of thousands of times. The result is keyed by the
    /// original path (case does not matter); an item with no recycled copy is not in it.
    /// </summary>
    public static IReadOnlyDictionary<string, LocationPath> FindRecycledMany(IFileSystem fileSystem, IEnumerable<LocationPath> originals, DateTimeOffset notBefore)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(originals);
        var wanted = originals.Where(o => o.Kind == LocationKind.Drive).ToList();
        var names = wanted.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var best = new Dictionary<string, (string Data, DateTimeOffset Deleted)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in wanted.Select(o => o.Root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = fileSystem.Path.Combine(root, FolderName);
            try
            {
                foreach (var user in fileSystem.Directory.EnumerateDirectories(bin))
                {
                    foreach (var info in Infos(fileSystem, user))
                    {
                        if (TryRead(fileSystem, info, out var path, out var deleted)
                            && deleted >= notBefore
                            && names.Contains(path)
                            && (!best.TryGetValue(path, out var current) || deleted >= current.Deleted))
                        {
                            var data = fileSystem.Path.Combine(user, DataPrefix + fileSystem.Path.GetFileName(info)[InfoPrefix.Length..]);
                            if (fileSystem.File.Exists(data) || fileSystem.Directory.Exists(data))
                            {
                                best[path] = (data, deleted);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No bin folder, or other users' folders we may not read: nothing to find, so the delete just cannot be undone.
            }
        }

        return best.ToDictionary(e => e.Key, e => LocationPath.Parse(e.Value.Data), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="item"/> is a <c>$R</c> entry of a Recycle Bin; <paramref name="info"/> is then the path of its
    /// <c>$I</c> companion, which goes away together with the item when it is restored.
    /// </summary>
    public static bool IsBinItem(IFileSystem fileSystem, LocationPath item, out string info)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(item);
        info = string.Empty;
        var name = item.Name;
        if (!name.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase) || item.Parent?.Parent?.Name is not { } bin
            || !string.Equals(bin, FolderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        info = fileSystem.Path.Combine(item.Parent.Value, InfoPrefix + name[DataPrefix.Length..]);
        return true;
    }

    private static IEnumerable<string> Infos(IFileSystem fileSystem, string userFolder)
    {
        IEnumerable<string> files;
        try
        {
            files = fileSystem.Directory.EnumerateFiles(userFolder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files.Where(f => fileSystem.Path.GetFileName(f).StartsWith(InfoPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryRead(IFileSystem fileSystem, string info, out string path, out DateTimeOffset deleted)
    {
        path = string.Empty;
        deleted = default;
        byte[] bytes;
        try
        {
            bytes = fileSystem.File.ReadAllBytes(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (bytes.Length < 28)
        {
            return false;
        }

        var version = BitConverter.ToInt64(bytes, 0);
        var ticks = BitConverter.ToInt64(bytes, 16);
        try
        {
            deleted = DateTimeOffset.FromFileTime(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        string text;
        switch (version)
        {
            case 2:
                // Windows 10: the length of the path in characters (including the closing NUL), then the path.
                var characters = BitConverter.ToInt32(bytes, 24);
                if (characters < 1 || 28 + (characters * 2L) > bytes.Length)
                {
                    return false;
                }

                text = Encoding.Unicode.GetString(bytes, 28, (characters - 1) * 2);
                break;
            case 1:
                // Windows Vista to 8: a fixed 520-byte field holding the path and a NUL.
                text = Encoding.Unicode.GetString(bytes, 24, bytes.Length - 24);
                var end = text.IndexOf('\0', StringComparison.Ordinal);
                text = end >= 0 ? text[..end] : text;
                break;
            default:
                return false;
        }

        path = text;
        return text.Length > 0;
    }
}
