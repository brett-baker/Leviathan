using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>What a row of the sidebar is.</summary>
public enum SidebarNodeKind
{
    /// <summary>A section header such as Favorites or Local.</summary>
    Section,

    /// <summary>A top-level entry of a section: a favorite, a known folder, a drive, a share, a cloud drive.</summary>
    Place,

    /// <summary>A folder below a place.</summary>
    Folder,

    /// <summary>The spinner row shown while children are on their way (spec §7.2).</summary>
    Loading,

    /// <summary>A grey line of text: an empty section or "Not responding". When it carries a retry, clicking it retries.</summary>
    Message,

    /// <summary>A collapsible heading inside a section that holds places directly, such as "Nearby" (spec §7.1). It has no location and nothing to list.</summary>
    Group,

    /// <summary>A line that does something when clicked instead of going somewhere, such as "Add connection…" (spec §7.1).</summary>
    Action,
}

/// <summary>The icon a row shows; the UI maps it to a glyph.</summary>
public enum SidebarIcon
{
    /// <summary>A plain folder.</summary>
    Folder,

    /// <summary>The user profile folder.</summary>
    Home,

    /// <summary>Desktop.</summary>
    Desktop,

    /// <summary>Documents.</summary>
    Documents,

    /// <summary>Downloads.</summary>
    Downloads,

    /// <summary>Pictures.</summary>
    Pictures,

    /// <summary>Videos.</summary>
    Videos,

    /// <summary>Music.</summary>
    Music,

    /// <summary>A local disk.</summary>
    Drive,

    /// <summary>A removable disk.</summary>
    Removable,

    /// <summary>A mapped network drive or share.</summary>
    NetworkDrive,

    /// <summary>A cloud sync folder.</summary>
    Cloud,

    /// <summary>A pinned folder.</summary>
    Favorite,

    /// <summary>A server (NAS, PC) whose children are its shares.</summary>
    Server,
}

/// <summary>An entry the sidebar is built from.</summary>
/// <param name="Title">Main text.</param>
/// <param name="Location">Where it goes.</param>
/// <param name="Icon">Icon.</param>
/// <param name="Detail">Second line, such as free space, or empty.</param>
/// <param name="UsedFraction">Share of a drive in use for the usage bar, or null for no bar.</param>
/// <param name="IsLowOnSpace">True when the bar should warn.</param>
public sealed record SidebarPlace(string Title, LocationPath Location, SidebarIcon Icon, string Detail = "", double? UsedFraction = null, bool IsLowOnSpace = false)
{
    /// <summary>Icon character that replaces the standard one (a favorite the user gave an icon), or null.</summary>
    public string? Glyph { get; init; }

    /// <summary>Icon colour as <c>#RRGGBB</c>, or null for the theme's.</summary>
    public string? Accent { get; init; }

    /// <summary>For a server: the host name (<c>diskstation</c>), which makes the row show a health dot and offer Remove. Null for everything else.</summary>
    public string? Server { get; init; }

    /// <summary>True for a server the user saved (it can be removed); false for one that is only listed under Nearby.</summary>
    public bool IsSaved { get; init; }
}

/// <summary>A collapsible heading inside a section, such as "Nearby" (spec §7.1), with its places and what to say when it has none.</summary>
/// <param name="Title">Heading.</param>
/// <param name="Places">Entries; may change later through <see cref="SidebarTree.UpdateGroupAsync"/>.</param>
/// <param name="EmptyText">Shown when there are no entries.</param>
public sealed record SidebarGroup(string Title, IReadOnlyList<SidebarPlace> Places, string EmptyText);

/// <summary>A section and its entries.</summary>
/// <param name="Title">Header text.</param>
/// <param name="Places">Entries.</param>
/// <param name="EmptyText">Shown when there are no entries, or null to hide the section when empty.</param>
public sealed record SidebarSection(string Title, IReadOnlyList<SidebarPlace> Places, string? EmptyText = null)
{
    /// <summary>Collapsible headings after the places, such as Nearby under Network.</summary>
    public IReadOnlyList<SidebarGroup> Groups { get; init; } = [];

    /// <summary>Text of a last line that does something when clicked ("Add connection…"), or null for none.</summary>
    public string? ActionTitle { get; init; }
}

/// <summary>One row of the sidebar tree.</summary>
public sealed partial class SidebarNode : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _canExpand;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The server's health for the dot beside its icon, or null for rows that are not servers (spec §7.1).</summary>
    [ObservableProperty]
    private ShareHealth? _health;

    /// <summary>Creates a node.</summary>
    public SidebarNode(SidebarNodeKind kind, string title, LocationPath? location = null, SidebarIcon icon = SidebarIcon.Folder)
    {
        Kind = kind;
        Title = title;
        Location = location;
        Icon = icon;
    }

    /// <summary>What the row is.</summary>
    public SidebarNodeKind Kind { get; }

    /// <summary>Main text.</summary>
    public string Title { get; }

    /// <summary>Second line, or empty.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Where the row goes, or null for headers and messages.</summary>
    public LocationPath? Location { get; }

    /// <summary>Icon.</summary>
    public SidebarIcon Icon { get; }

    /// <summary>Icon character that replaces <see cref="Icon"/>'s standard one, or null.</summary>
    public string? Glyph { get; init; }

    /// <summary>Icon colour as <c>#RRGGBB</c>, or null for the theme's.</summary>
    public string? Accent { get; init; }

    /// <summary>Usage bar fill, or null for no bar.</summary>
    public double? UsedFraction { get; init; }

    /// <summary>True when the usage bar warns.</summary>
    public bool IsLowOnSpace { get; init; }

    /// <summary>Indent level: 0 for sections and 1 for places.</summary>
    public int Depth { get; internal set; }

    /// <summary>The row this one hangs below.</summary>
    public SidebarNode? Parent { get; internal set; }

    /// <summary>Loaded children, in order; empty until expanded.</summary>
    public List<SidebarNode> Children { get; } = [];

    /// <summary>True once the folder's children have been listed.</summary>
    public bool IsLoaded { get; internal set; }

    /// <summary>True for a message row that retries its parent when clicked.</summary>
    public bool IsRetry { get; init; }

    /// <summary>For a server row: the host name. Null for everything else.</summary>
    public string? Server { get; init; }

    /// <summary>True for a server the user saved, which can be removed.</summary>
    public bool IsSaved { get; init; }

    internal CancellationTokenSource? Loading { get; set; }

    /// <summary>Bumped when the node's rows are inserted or removed, so an insert in progress can tell it was overtaken.</summary>
    internal int RowGeneration { get; set; }

    /// <summary>The node's children a click on the chevron would show or hide.</summary>
    public bool IsFolderLike => Kind is SidebarNodeKind.Place or SidebarNodeKind.Folder;
}

/// <summary>Lists the folders below a location for the tree. Throws <see cref="LocationException"/> when it cannot (the tree shows why).</summary>
public interface ISubfolderSource
{
    /// <summary>Folders directly below <paramref name="location"/>.</summary>
    Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken);
}

/// <summary>
/// The sidebar (spec §7): one flat, virtualizable list of rows built from sections that expand lazily. Expanding shows a spinner
/// row at once and lists children with a 5 s timeout (spec §7.2 rule 2); folders show an expand arrow without being checked for
/// children, which is removed when they turn out to be empty (rule 3); network nodes never expand by themselves, and following
/// the current folder never lists network paths (rules 1 and 4). Used from the UI thread.
/// </summary>
public sealed class SidebarTree
{
    /// <summary>How long a listing may take before the node says it is not responding.</summary>
    public static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(5);

    private const int RowChunk = 40;

    private readonly ISubfolderSource _source;
    private readonly TimeProvider _time;
    private readonly List<SidebarNode> _roots = [];
    private SidebarNode? _selected;

    /// <summary>Creates a tree that lists children through <paramref name="source"/>.</summary>
    public SidebarTree(ISubfolderSource source, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The visible rows, top to bottom.</summary>
    public RangeObservableCollection<SidebarNode> Rows { get; } = [];

    /// <summary>The row that matches the current folder, or null.</summary>
    public SidebarNode? Selected => _selected;

    /// <summary>Raised when <see cref="Selected"/> changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Replaces the whole tree with <paramref name="sections"/>. Sections without entries and without empty text are left out.
    /// <paramref name="healthOf"/> gives the health dot of each server row (by host name); null shows no dots until <see cref="RefreshHealth"/>.
    /// </summary>
    public void SetSections(IEnumerable<SidebarSection> sections, Func<string, ShareHealth>? healthOf = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        foreach (var node in _roots)
        {
            CancelLoading(node);
        }

        _roots.Clear();
        Rows.Clear();
        _selected = null;
        foreach (var section in sections)
        {
            if (section.Places.Count == 0 && section.Groups.Count == 0 && section.EmptyText is null)
            {
                continue;
            }

            var header = new SidebarNode(SidebarNodeKind.Section, section.Title) { IsExpanded = true, CanExpand = true };
            if (section.Places.Count == 0 && section.Groups.Count == 0)
            {
                AddChild(header, new SidebarNode(SidebarNodeKind.Message, section.EmptyText!));
            }

            foreach (var place in section.Places)
            {
                AddChild(header, PlaceNode(place, healthOf));
            }

            foreach (var group in section.Groups)
            {
                var node = new SidebarNode(SidebarNodeKind.Group, group.Title) { CanExpand = true, IsLoaded = true };
                AddChild(header, node);
                FillGroup(node, group, healthOf);
            }

            if (section.ActionTitle is not null)
            {
                AddChild(header, new SidebarNode(SidebarNodeKind.Action, section.ActionTitle));
            }

            _roots.Add(header);
            Rows.Add(header);
            foreach (var row in Flatten(header.Children))
            {
                Rows.Add(row);
            }
        }
    }

    private static SidebarNode PlaceNode(SidebarPlace place, Func<string, ShareHealth>? healthOf) =>
        new(SidebarNodeKind.Place, place.Title, place.Location, place.Icon)
        {
            Detail = place.Detail,
            UsedFraction = place.UsedFraction,
            IsLowOnSpace = place.IsLowOnSpace,
            Glyph = place.Glyph,
            Accent = place.Accent,
            Server = place.Server,
            IsSaved = place.IsSaved,
            CanExpand = true,
            Health = place.Server is null ? null : healthOf?.Invoke(place.Server) ?? ShareHealth.Unknown,
        };

    private static void FillGroup(SidebarNode group, SidebarGroup source, Func<string, ShareHealth>? healthOf)
    {
        group.Children.Clear();
        if (source.Places.Count == 0)
        {
            AddChild(group, new SidebarNode(SidebarNodeKind.Message, source.EmptyText));
            return;
        }

        foreach (var place in source.Places)
        {
            AddChild(group, PlaceNode(place, healthOf));
        }
    }

    /// <summary>
    /// Replaces the places of the group called <paramref name="title"/> (such as Nearby) while the tree stays as it is: the group keeps
    /// its expanded state and only its own rows change, so a device that turns up never moves what the user is looking at.
    /// </summary>
    public async Task UpdateGroupAsync(string title, SidebarGroup source, Func<string, ShareHealth>? healthOf = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(source);
        var group = Flatten(_roots, expandedOnly: false).FirstOrDefault(n => n.Kind == SidebarNodeKind.Group && n.Title == title);
        if (group is null)
        {
            return;
        }

        var wasExpanded = group.IsExpanded;
        if (wasExpanded)
        {
            group.RowGeneration++;
            RemoveDescendantRows(group);
        }

        foreach (var gone in group.Children)
        {
            CancelLoading(gone);
        }

        FillGroup(group, source, healthOf);
        if (wasExpanded)
        {
            await InsertDescendantRowsAsync(group).ConfigureAwait(true);
        }

        ReapplySelection();
    }

    /// <summary>Sets the health dot of every server row from <paramref name="healthOf"/> (by host name). Cheap: no rows move.</summary>
    public void RefreshHealth(Func<string, ShareHealth> healthOf)
    {
        ArgumentNullException.ThrowIfNull(healthOf);
        foreach (var node in Flatten(_roots, expandedOnly: false).Where(n => n.Server is not null))
        {
            var health = healthOf(node.Server!);
            if (node.Health != health)
            {
                node.Health = health;
            }
        }
    }

    /// <summary>Shows or hides the children of <paramref name="node"/>; listing them first when needed.</summary>
    public Task ToggleAsync(SidebarNode node, CancellationToken cancellationToken)
    {
        if (!node.IsExpanded)
        {
            return ExpandAsync(node, cancellationToken);
        }

        Collapse(node);
        return Task.CompletedTask;
    }

    /// <summary>Hides the children of <paramref name="node"/> and stops a listing in progress.</summary>
    public void Collapse(SidebarNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsExpanded)
        {
            return;
        }

        CancelLoading(node);
        node.RowGeneration++;
        RemoveDescendantRows(node);
        node.IsExpanded = false;
        if (node.Children.Count == 1 && node.Children[0].Kind == SidebarNodeKind.Loading)
        {
            node.Children.Clear();
        }
    }

    /// <summary>
    /// Shows the children of <paramref name="node"/>. A spinner row appears at once; the folders replace it when listed, or a
    /// "Not responding" row with a retry replaces it after <see cref="ListingTimeout"/> or on failure.
    /// </summary>
    public async Task ExpandAsync(SidebarNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsExpanded || !node.CanExpand)
        {
            return;
        }

        node.IsExpanded = true;
        if (node.Kind == SidebarNodeKind.Section || node.IsLoaded)
        {
            await InsertDescendantRowsAsync(node).ConfigureAwait(true);
            return;
        }

        node.Children.Clear();
        var spinner = new SidebarNode(SidebarNodeKind.Loading, "Loading…");
        AddChild(node, spinner);
        await InsertDescendantRowsAsync(node).ConfigureAwait(true);

        using var timeout = new CancellationTokenSource(ListingTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        CancelLoading(node);
        node.Loading = linked;
        IReadOnlyList<LocationPath>? folders = null;
        string? failure = null;
        try
        {
            folders = await _source.ListFoldersAsync(node.Location!, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            failure = "Not responding";
        }
        catch (OperationCanceledException)
        {
            // Collapsed or closing: whoever cancelled has already tidied the rows.
            return;
        }
        catch (LocationException ex)
        {
            failure = ex.Error == LocationError.AccessDenied ? "Access denied" : "Not responding";
        }
        finally
        {
            if (ReferenceEquals(node.Loading, linked))
            {
                node.Loading = null;
            }
        }

        if (!node.IsExpanded || !node.Children.Contains(spinner))
        {
            return;
        }

        RemoveDescendantRows(node);
        node.Children.Clear();
        if (failure is not null)
        {
            AddChild(node, new SidebarNode(SidebarNodeKind.Message, $"{failure} — Retry") { IsRetry = true });
        }
        else if (folders!.Count == 0)
        {
            // The expand arrow was shown without looking; with nothing below it goes away (spec §7.2 rule 3).
            node.IsLoaded = true;
            node.CanExpand = false;
            node.IsExpanded = false;
            return;
        }
        else
        {
            node.IsLoaded = true;
            foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                AddChild(node, new SidebarNode(SidebarNodeKind.Folder, folder.Name, folder) { CanExpand = true });
            }
        }

        await InsertDescendantRowsAsync(node).ConfigureAwait(true);
        ReapplySelection();
    }

    /// <summary>
    /// Brings the tree in line with a folder that was just listed or changed (a folder was created, renamed, deleted or moved in it):
    /// every row for <paramref name="parent"/> whose children were already listed gets exactly <paramref name="folders"/> as children,
    /// without listing anything itself. Rows that stay keep their state (expanded, selected); a row that was empty gets its arrow back.
    /// A row never expanded just learns whether it has an arrow. The caller passes the listing the file view already has, so
    /// this costs the network nothing (spec §7.2 rule 1).
    /// </summary>
    /// <param name="parent">The folder whose subfolders are given.</param>
    /// <param name="folders">Its visible subfolders.</param>
    public async Task UpdateChildrenAsync(LocationPath parent, IReadOnlyList<LocationPath> folders)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(folders);
        var nodes = Flatten(_roots, expandedOnly: false).Where(n => n.IsFolderLike && n.Location == parent).ToList();
        foreach (var node in nodes)
        {
            await UpdateNodeAsync(node, folders).ConfigureAwait(true);
        }
    }

    private async Task UpdateNodeAsync(SidebarNode node, IReadOnlyList<LocationPath> folders)
    {
        // An expand in progress lists the folder itself a moment from now.
        if (node.Loading is not null || (node.IsExpanded && !node.IsLoaded))
        {
            return;
        }

        if (!node.IsLoaded)
        {
            if (folders.Count > 0 && !node.CanExpand)
            {
                node.CanExpand = true;
            }

            return;
        }

        var wanted = folders.DistinctBy(f => f).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var known = new Dictionary<LocationPath, SidebarNode>();
        foreach (var child in node.Children.Where(c => c.Kind == SidebarNodeKind.Folder && c.Location is not null))
        {
            known.TryAdd(child.Location!, child);
        }

        if (wanted.Count == known.Count && node.Children.Count == known.Count && wanted.Select((f, i) => node.Children[i].Location == f).All(same => same))
        {
            return;
        }

        var wasExpanded = node.IsExpanded;
        if (wasExpanded)
        {
            node.RowGeneration++;
            RemoveDescendantRows(node);
        }

        var rebuilt = wanted.Select(f => known.GetValueOrDefault(f) ?? new SidebarNode(SidebarNodeKind.Folder, f.Name, f) { CanExpand = true }).ToList();
        foreach (var gone in node.Children.Except(rebuilt))
        {
            CancelLoading(gone);
        }

        node.Children.Clear();
        foreach (var child in rebuilt)
        {
            AddChild(node, child);
        }

        if (rebuilt.Count == 0)
        {
            // Nothing below any more: the arrow goes away, like a folder found empty when first opened (spec §7.2 rule 3).
            node.CanExpand = false;
            node.IsExpanded = false;
            return;
        }

        node.CanExpand = true;
        if (wasExpanded)
        {
            await InsertDescendantRowsAsync(node).ConfigureAwait(true);
        }

        ReapplySelection();
    }

    /// <summary>Clicking a retry row lists its parent again.</summary>
    public async Task RetryAsync(SidebarNode retryRow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retryRow);
        if (retryRow.Parent is { } parent && retryRow.IsRetry)
        {
            Collapse(parent);
            parent.Children.Clear();
            parent.IsLoaded = false;
            await ExpandAsync(parent, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Makes <paramref name="node"/> the selected row (null clears it).</summary>
    public void Select(SidebarNode? node)
    {
        if (ReferenceEquals(_selected, node))
        {
            return;
        }

        _selected?.IsSelected = false;
        _selected = node;
        node?.IsSelected = true;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Follows the current folder (spec §7.2 rule 4): selects the entry for <paramref name="location"/>, expanding local folders
    /// on the way down. Network paths are followed only through nodes that are already loaded: following the view must never
    /// start listing a share.
    /// </summary>
    public async Task SyncAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var places = _roots.SelectMany(r => r.Children).Where(c => c.Kind == SidebarNodeKind.Place && c.Location is not null).ToList();

        // A folder that has its own entry (Documents, a favorite) is selected there rather than under its drive.
        var exact = places.FirstOrDefault(p => p.Location == location && p.Parent?.Title is "Favorites" or "Common folders");
        if (exact is not null)
        {
            Select(exact);
            return;
        }

        var root = places
            .Where(p => p.Location!.Contains(location))
            .OrderByDescending(p => p.Location!.Value.Length)
            .FirstOrDefault(p => p.Parent?.Title is not ("Favorites" or "Common folders"))
            ?? places.Where(p => p.Location!.Contains(location)).OrderByDescending(p => p.Location!.Value.Length).FirstOrDefault();
        if (root is null)
        {
            Select(null);
            return;
        }

        var node = root;
        while (node.Location != location)
        {
            if (!node.IsLoaded)
            {
                if (location.IsNetwork || !node.CanExpand)
                {
                    break;
                }

                await ExpandAsync(node, cancellationToken).ConfigureAwait(true);
                if (!node.IsLoaded)
                {
                    break;
                }
            }
            else if (!node.IsExpanded)
            {
                await ExpandAsync(node, cancellationToken).ConfigureAwait(true);
            }

            var next = node.Children.FirstOrDefault(c => c.Location is { } l && l.Contains(location));
            if (next is null)
            {
                break;
            }

            node = next;
        }

        Select(node);
    }

    // Rows -------------------------------------------------------------------------------------------------------------

    private static void AddChild(SidebarNode parent, SidebarNode child)
    {
        child.Parent = parent;
        child.Depth = parent.Depth + 1;
        parent.Children.Add(child);
    }

    private static IEnumerable<SidebarNode> Flatten(IEnumerable<SidebarNode> nodes, bool expandedOnly = true)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (!expandedOnly || node.IsExpanded)
            {
                foreach (var child in Flatten(node.Children, expandedOnly))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// Inserts the rows below <paramref name="node"/> in chunks, yielding between them, so a folder with hundreds of subfolders
    /// never holds the UI thread for more than a few milliseconds (spec §3.2). One list insert at a time is also much cheaper for
    /// the list control than one big range. A collapse (or a newer expand) during the insert stops it.
    /// </summary>
    private async Task InsertDescendantRowsAsync(SidebarNode node)
    {
        var generation = ++node.RowGeneration;
        var pending = Flatten(node.Children).ToList();
        for (var done = 0; done < pending.Count; done += RowChunk)
        {
            if (done > 0)
            {
                await Task.Yield();
            }

            var at = Rows.IndexOf(node);
            if (at < 0 || node.RowGeneration != generation || !node.IsExpanded)
            {
                return;
            }

            // Append after whatever is already below the node: rows inserted by earlier chunks, and anything a child expanded since.
            var next = at + 1 + CountDescendantRows(at, node.Depth);
            foreach (var row in pending.Skip(done).Take(RowChunk))
            {
                Rows.Insert(next++, row);
            }
        }
    }

    private void RemoveDescendantRows(SidebarNode node)
    {
        var at = Rows.IndexOf(node);
        if (at >= 0)
        {
            for (var count = CountDescendantRows(at, node.Depth); count > 0; count--)
            {
                Rows.RemoveAt(at + 1);
            }
        }
    }

    /// <summary>The rows right below row <paramref name="at"/> that are deeper than <paramref name="depth"/>: its subtree.</summary>
    private int CountDescendantRows(int at, int depth)
    {
        var count = 0;
        while (at + 1 + count < Rows.Count && Rows[at + 1 + count].Depth > depth)
        {
            count++;
        }

        return count;
    }

    private void ReapplySelection()
    {
        // Children that were just listed replace rows that may have been selected; the selected node object stays valid.
        if (_selected is not null && !Rows.Contains(_selected))
        {
            _selected.IsSelected = false;
        }
    }

    private static void CancelLoading(SidebarNode node)
    {
        node.Loading?.Cancel();
        node.Loading = null;
        foreach (var child in node.Children)
        {
            CancelLoading(child);
        }
    }
}
