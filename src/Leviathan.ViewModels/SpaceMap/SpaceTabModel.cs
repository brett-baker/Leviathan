using System.Globalization;
using Leviathan.Core.Model;
using Leviathan.SpaceMap;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.SpaceMap;

/// <summary>One of the largest items listed beside the compact map.</summary>
/// <param name="Entry">The file or folder.</param>
/// <param name="SizeText">Its size, or "not counted yet" for a folder whose size is not known.</param>
/// <param name="Fraction">Its share of the folder, 0 to 1.</param>
public sealed record SpaceTabItem(FileEntry Entry, string SizeText, double Fraction);

/// <summary>What the Space tab of the status hub shows for the folder in the file view (spec §14, §16.1).</summary>
/// <param name="Tiles">The rectangles of the compact map, one for each item big enough to see.</param>
/// <param name="Items">The largest items, biggest first.</param>
/// <param name="Summary">One line: how much the folder holds and how many folders are not counted yet.</param>
/// <param name="Entries">Which item each tile stands for.</param>
public sealed record SpaceTabData(IReadOnlyList<MapTile> Tiles, IReadOnlyList<SpaceTabItem> Items, string Summary, IReadOnlyDictionary<SpaceNode, FileEntry> Entries);

/// <summary>
/// The compact space map of the status hub's Space tab (spec §16.1): the items directly in the current folder, each as big as its bytes, with folder sizes from
/// the folder-size store. It needs no scan of its own, so it appears with the listing and fills in as folder sizes are counted; the full map (§14) is one click away.
/// </summary>
public static class SpaceTabModel
{
    /// <summary>The most items listed beside the map.</summary>
    public const int ItemsListed = 12;

    /// <summary>Builds the tab for <paramref name="entries"/> laid out in <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    /// <param name="entries">What the file view lists.</param>
    /// <param name="folderBytes">The counted total of a folder, or null when it is not counted yet.</param>
    /// <param name="width">Map width in pixels.</param>
    /// <param name="height">Map height in pixels.</param>
    /// <param name="now">The time, for colouring by age.</param>
    public static SpaceTabData Build(IReadOnlyList<FileEntry> entries, Func<FileEntry, long?> folderBytes, double width, double height, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(folderBytes);
        var root = new SpaceNode("folder", true, 0, DateTimeOffset.UnixEpoch);
        var byNode = new Dictionary<SpaceNode, FileEntry>();
        var uncounted = new List<FileEntry>();
        foreach (var entry in entries)
        {
            long size;
            if (entry.Kind == FileEntryKind.Folder)
            {
                if (folderBytes(entry) is not { } total)
                {
                    uncounted.Add(entry);
                    continue;
                }

                size = total;
            }
            else if (entry.Kind == FileEntryKind.File)
            {
                size = entry.Size;
            }
            else
            {
                continue;
            }

            if (size <= 0)
            {
                continue;
            }

            var node = new SpaceNode(entry.Name, entry.Kind == FileEntryKind.Folder, size, entry.Modified, root);
            root.Add(node);
            byNode[node] = entry;
        }

        var tiles = Treemap.Layout(root, new TileRect(0, 0, width, height), new TreemapOptions { FolderPadding = 0 })
            .Select(t => new MapTile(t, SpaceColors.CategoryOf(t.Node, ColorMode.Type, now)))
            .ToList();
        var largest = root.Children.OrderByDescending(c => c.Size).Take(ItemsListed)
            .Select(c => new SpaceTabItem(byNode[c], DisplayFormat.Size(c.Size), root.Size > 0 ? (double)c.Size / root.Size : 0))
            .ToList();
        largest.AddRange(uncounted.Take(Math.Max(0, ItemsListed - largest.Count)).Select(e => new SpaceTabItem(e, "not counted yet", 0)));
        var summary = root.Size == 0
            ? "Nothing here takes up space yet."
            : string.Create(CultureInfo.InvariantCulture, $"{DisplayFormat.Size(root.Size)} in {root.Children.Count:N0} items") + (uncounted.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {uncounted.Count:N0} folders not counted yet") : string.Empty);
        return new SpaceTabData(tiles, largest, summary, byNode);
    }

    /// <summary>The item a click at (<paramref name="x"/>, <paramref name="y"/>) means, or null. Small items drawn together stand for none in particular.</summary>
    public static FileEntry? EntryAt(SpaceTabData data, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(data);
        for (var i = data.Tiles.Count - 1; i >= 0; i--)
        {
            var tile = data.Tiles[i].Tile;
            if (tile.Bounds.Contains(x, y))
            {
                return !tile.IsRemainder && data.Entries.TryGetValue(tile.Node, out var entry) ? entry : null;
            }
        }

        return null;
    }
}
