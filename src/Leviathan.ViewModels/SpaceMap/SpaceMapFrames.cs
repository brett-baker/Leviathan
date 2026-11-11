using System.Globalization;
using Leviathan.SpaceMap;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.SpaceMap;

/// <summary>What the space map window is showing, as the user left it (spec §14).</summary>
/// <param name="Zoom">The folder the map is zoomed into (the root to start).</param>
/// <param name="Mode">Colour by type or by age.</param>
/// <param name="MinBytes">Show only files of at least this size; 0 for all.</param>
/// <param name="Selected">The selected file or folder, or null.</param>
/// <param name="Expanded">The folders open in the tree.</param>
/// <param name="LegendByExtension">True to list the legend by extension instead of by kind.</param>
public sealed record SpaceMapState(SpaceNode Zoom, ColorMode Mode, long MinBytes, SpaceNode? Selected, IReadOnlySet<SpaceNode> Expanded, bool LegendByExtension = false);

/// <summary>One rectangle of the drawn map with the colour group it belongs to.</summary>
/// <param name="Tile">The rectangle and what it shows.</param>
/// <param name="Category">The colour group (see <see cref="SpaceColors"/>).</param>
public sealed record MapTile(TreemapTile Tile, int Category);

/// <summary>One line of the folder tree on the left of the window.</summary>
/// <param name="Node">The file or folder.</param>
/// <param name="Depth">Indent level, 0 for the zoomed folder's own children.</param>
/// <param name="Name">Its name.</param>
/// <param name="SizeText">Its size as text.</param>
/// <param name="Fraction">Its share of the folder it is in, 0 to 1, for the bar.</param>
/// <param name="FilesText">How many files it holds (blank for a file).</param>
/// <param name="ModifiedText">Its last change, as a date.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="IsExpanded">True when the folder is open.</param>
/// <param name="IsSelected">True when it is the selected one.</param>
/// <param name="Note">"Could not be read" and the like, or blank.</param>
public sealed record SpaceRow(SpaceNode Node, int Depth, string Name, string SizeText, double Fraction, string FilesText, string ModifiedText, bool IsFolder, bool IsExpanded, bool IsSelected, string Note);

/// <summary>Everything the window draws at one moment: the tiles, the tree, the legend and the words.</summary>
/// <param name="Tiles">The rectangles of the map.</param>
/// <param name="Rows">The lines of the tree.</param>
/// <param name="Legend">What the colours mean.</param>
/// <param name="Progress">How far the scan has got.</param>
/// <param name="Title">The window title.</param>
/// <param name="Status">One line about the scan and the selection.</param>
public sealed record SpaceMapFrame(IReadOnlyList<MapTile> Tiles, IReadOnlyList<SpaceRow> Rows, IReadOnlyList<LegendEntry> Legend, SpaceScanProgress Progress, string Title, string Status);

/// <summary>Builds a <see cref="SpaceMapFrame"/> from a scan (spec §14). A pure function of the scan and the state, so it can run on a pool thread and be tested.</summary>
public static class SpaceMapFrames
{
    /// <summary>The most lines the tree shows for one folder; the rest are one line saying how many are left.</summary>
    public const int RowsPerFolder = 500;

    /// <summary>Builds the frame for <paramref name="width"/> by <paramref name="height"/> pixels. Holds the scan still only while it reads the tree.</summary>
    public static SpaceMapFrame Build(SpaceScanSession session, SpaceMapState state, double width, double height, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);
        var progress = session.Progress;
        return session.Read(root =>
        {
            var zoom = root.Contains(state.Zoom) ? state.Zoom : root;
            var tiles = Treemap.Layout(zoom, new TileRect(0, 0, width, height), new TreemapOptions { MinBytes = state.MinBytes })
                .Select(t => new MapTile(t, SpaceColors.CategoryOf(t.Node, state.Mode, now)))
                .ToList();
            var rows = new List<SpaceRow>();
            AddRows(zoom, 0, state, rows);
            var legend = SpaceColors.Legend(zoom, state.Mode, now, state.LegendByExtension);
            return new SpaceMapFrame(tiles, rows, legend, progress, TitleOf(zoom), StatusOf(zoom, progress, state.Selected));
        });
    }

    private static void AddRows(SpaceNode folder, int depth, SpaceMapState state, List<SpaceRow> rows)
    {
        var shown = 0;
        foreach (var child in folder.Children.OrderByDescending(c => c.Size))
        {
            if (shown++ >= RowsPerFolder)
            {
                rows.Add(new SpaceRow(folder, depth, string.Create(CultureInfo.InvariantCulture, $"… {folder.Children.Count - RowsPerFolder:N0} smaller items"), string.Empty, 0, string.Empty, string.Empty, false, false, false, string.Empty));
                break;
            }

            var expanded = child.IsFolder && state.Expanded.Contains(child);
            rows.Add(new SpaceRow(
                child,
                depth,
                child.Name,
                DisplayFormat.Size(child.Size),
                folder.Size > 0 ? (double)child.Size / folder.Size : 0,
                child.IsFolder ? string.Create(CultureInfo.InvariantCulture, $"{child.Files:N0}") : string.Empty,
                child.Modified > DateTimeOffset.UnixEpoch ? child.Modified.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
                child.IsFolder,
                expanded,
                child == state.Selected,
                child.Unreadable ? "could not be read" : child.IsPlaceholder ? "cloud file, not on this disk" : string.Empty));
            if (expanded)
            {
                AddRows(child, depth + 1, state, rows);
            }
        }
    }

    private static string TitleOf(SpaceNode zoom) => $"Space map · {zoom.FullPath}";

    private static string StatusOf(SpaceNode zoom, SpaceScanProgress progress, SpaceNode? selected)
    {
        var scan = progress.IsComplete
            ? string.Create(CultureInfo.InvariantCulture, $"{DisplayFormat.Size(zoom.Size)} in {zoom.Files:N0} files")
            : progress.IsPaused
                ? string.Create(CultureInfo.InvariantCulture, $"Paused · {DisplayFormat.Size(zoom.Size)} in {zoom.Files:N0} files so far, {progress.FoldersPending:N0} folders to go")
                : string.Create(CultureInfo.InvariantCulture, $"Scanning… {DisplayFormat.Size(zoom.Size)} in {zoom.Files:N0} files so far, {progress.FoldersListed:N0} folders listed, {progress.FoldersPending:N0} to go");
        if (progress.Errors > 0)
        {
            scan += string.Create(CultureInfo.InvariantCulture, $" · {progress.Errors:N0} folders could not be read, so their sizes are missing");
        }

        return selected is null ? scan : $"{scan}  |  {selected.FullPath}: {DisplayFormat.Size(selected.Size)}" + (selected.IsFolder ? string.Create(CultureInfo.InvariantCulture, $" in {selected.Files:N0} files") : string.Empty);
    }

    /// <summary>The node a click at (<paramref name="x"/>, <paramref name="y"/>) means: the file under it, or the folder whose small items are drawn there.</summary>
    public static SpaceNode? NodeAt(SpaceMapFrame frame, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(frame);
        for (var i = frame.Tiles.Count - 1; i >= 0; i--)
        {
            if (frame.Tiles[i].Tile.Bounds.Contains(x, y))
            {
                return frame.Tiles[i].Tile.Node;
            }
        }

        return null;
    }

    /// <summary>The tile that shows <paramref name="node"/> (its own, or its folder's remainder), for drawing the selection.</summary>
    public static TreemapTile? TileOf(SpaceMapFrame frame, SpaceNode node)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(node);
        return Treemap.TileOf([.. frame.Tiles.Select(t => t.Tile)], node);
    }
}
