using System.IO.Abstractions;
using System.Text.Json;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.Transfers;

/// <summary>One line of a trash folder's manifest: where an item came from, where it is now, and when it was deleted (spec §12.5).</summary>
/// <param name="Original">Where the item was before it was deleted.</param>
/// <param name="Trashed">Where it is now.</param>
/// <param name="DeletedAt">When it was deleted.</param>
public sealed record BinEntry(string Original, string Trashed, DateTimeOffset DeletedAt);

/// <summary>
/// Reads and trims the <c>manifest.jsonl</c> that <see cref="DeleteExecutor"/> keeps in a <c>.leviathan-trash</c> folder. A line that
/// cannot be read is skipped, never fatal: the manifest sits on a share anyone can edit, and a bad line must not hide the rest.
/// </summary>
public static class BinManifest
{
    /// <summary>The entries of the manifest in <paramref name="trashRoot"/>; empty when there is none.</summary>
    public static IReadOnlyList<BinEntry> Read(IFileSystem fileSystem, LocationPath trashRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(trashRoot);
        var path = trashRoot.Combine(DeleteExecutor.ManifestFile).ToWin32Path();
        if (!fileSystem.File.Exists(path))
        {
            return [];
        }

        var entries = new List<BinEntry>();
        foreach (var line in fileSystem.File.ReadLines(path))
        {
            if (TryParse(line, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// Drops the lines for <paramref name="trashedPaths"/> from the manifest in <paramref name="trashRoot"/>, after the items were
    /// restored or purged, so the bin does not list ghosts. The file is rewritten beside itself and swapped in, so a crash never leaves
    /// half a manifest; a manifest left empty is removed.
    /// </summary>
    public static void Remove(IFileSystem fileSystem, LocationPath trashRoot, IReadOnlyCollection<string> trashedPaths)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(trashRoot);
        ArgumentNullException.ThrowIfNull(trashedPaths);
        var path = trashRoot.Combine(DeleteExecutor.ManifestFile).ToWin32Path();
        if (trashedPaths.Count == 0 || !fileSystem.File.Exists(path))
        {
            return;
        }

        var gone = new HashSet<string>(trashedPaths, StringComparer.OrdinalIgnoreCase);
        var keep = fileSystem.File.ReadLines(path)
            .Where(line => !(TryParse(line, out var entry) && gone.Contains(entry.Trashed)))
            .ToList();
        if (keep.Count == 0)
        {
            fileSystem.File.Delete(path);
            return;
        }

        var temp = path + ".tmp";
        fileSystem.File.WriteAllLines(temp, keep);
        fileSystem.File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The <c>.leviathan-trash</c> folder that holds <paramref name="path"/>, or null when it is not inside one (it may be in a NAS
    /// bin, whose manifest lives in a trash folder that is found through the registry instead).
    /// </summary>
    public static LocationPath? RootOf(LocationPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var index = -1;
        for (var i = 0; i < path.Segments.Count; i++)
        {
            if (path.Segments[i].Equals(RecycleFolders.TrashFolder, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        var root = LocationPath.Parse(path.Root);
        for (var i = 0; i <= index; i++)
        {
            root = root.Combine(path.Segments[i]);
        }

        return root;
    }

    private static bool TryParse(string line, out BinEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("original", out var original) || original.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("trashed", out var trashed) || trashed.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out var when))
            {
                return false;
            }

            entry = new BinEntry(original.GetString()!, trashed.GetString()!, when);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
