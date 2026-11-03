using System.Collections.ObjectModel;

namespace Leviathan.ViewModels.Folders;

/// <summary>What the column chooser hands back.</summary>
/// <param name="Ids">The chosen columns in order.</param>
/// <param name="ForAllOfType">True to remember the choice for every folder of the same type, not only this folder.</param>
public sealed record ColumnChoice(IReadOnlyList<string> Ids, bool ForAllOfType);

/// <summary>One column the chooser lists.</summary>
/// <param name="Id">Column id; the ids of <see cref="FileColumns"/> are the ones that exist today.</param>
/// <param name="Title">Name shown.</param>
/// <param name="Group">Group heading.</param>
/// <param name="Available">False for a column whose data arrives in a later update: listed, greyed, not selectable.</param>
public sealed record ColumnInfo(string Id, string Title, string Group, bool Available);

/// <summary>A group heading with its columns, after the search filter.</summary>
/// <param name="Title">Heading.</param>
/// <param name="Columns">The columns in the group that match the search.</param>
public sealed record ColumnGroup(string Title, IReadOnlyList<ColumnInfo> Columns);

/// <summary>Every column the Details view will offer, grouped as spec §8.2 lists them (spec §8.2).</summary>
public static class ColumnCatalog
{
    /// <summary>The note shown beside a column whose data does not exist yet.</summary>
    public const string LaterUpdate = "Available in a later update";

    /// <summary>All columns in the order of spec §8.2.</summary>
    public static IReadOnlyList<ColumnInfo> All { get; } = Build();

    private static List<ColumnInfo> Build()
    {
        var result = new List<ColumnInfo>();

        void Group(string group, params (string Id, string Title)[] columns)
        {
            foreach (var (id, title) in columns)
            {
                result.Add(new ColumnInfo(id, title, group, FileColumns.TryGet(id, out _)));
            }
        }

        Group("File system", ("name", "Name"), ("extension", "Extension"), ("type", "Type"), ("size", "Size"), ("sizeondisk", "Size on disk"),
            ("modified", "Date modified"), ("created", "Date created"), ("accessed", "Date accessed"), ("attributes", "Attributes"),
            ("owner", "Owner"), ("path", "Path"), ("linktarget", "Link target"), ("itemcount", "Item count"));
        Group("Leviathan", ("tags", "Tags"), ("deleted", "Soft-deleted date"), ("checksum", "Checksum"), ("duplicates", "Duplicate group"));
        Group("Media", ("duration", "Duration"), ("resolution", "Resolution"), ("framerate", "Frame rate"), ("videocodec", "Video codec"),
            ("audiocodec", "Audio codec"), ("bitrate", "Bitrate"), ("container", "Container"), ("hdr", "HDR"), ("channels", "Audio channels"),
            ("audiolanguages", "Audio language(s)"), ("subtitles", "Subtitle tracks"));
        Group("Image", ("dimensions", "Dimensions"), ("datetaken", "Date taken"), ("camera", "Camera"), ("lens", "Lens"), ("iso", "ISO"), ("gps", "GPS present"));
        Group("Audio", ("artist", "Artist"), ("album", "Album"), ("audiotitle", "Title"), ("track", "Track"), ("year", "Year"), ("genre", "Genre"));
        Group("Document", ("doctitle", "Title"), ("authors", "Authors"), ("pages", "Page count"));
        return result;
    }
}

/// <summary>
/// The state of the column chooser dialog (spec §8.2): the grouped list with a search box and tick boxes, the order of the chosen
/// columns, reset to the default, and the result the dialog hands back. Pure, so it is tested without a window; the dialog only
/// draws it.
/// </summary>
public sealed class ColumnChooserModel
{
    private readonly List<string> _order;
    private string _search = string.Empty;

    /// <summary>Starts from the columns shown now.</summary>
    public ColumnChooserModel(IEnumerable<string> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        _order = [.. Normalize(visible)];
        Order = new ObservableCollection<string>(_order);
    }

    /// <summary>The chosen columns, left to right. Changes as ticks and moves happen.</summary>
    public ObservableCollection<string> Order { get; }

    /// <summary>Raised after the ticks or the order changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The search text. Matches column titles and group names, ignoring case; empty shows everything.</summary>
    public string Search
    {
        get => _search;
        set => _search = value?.Trim() ?? string.Empty;
    }

    /// <summary>The groups and columns that match <see cref="Search"/>; groups with nothing left are dropped.</summary>
    public IReadOnlyList<ColumnGroup> Groups()
    {
        var matches = ColumnCatalog.All.Where(c =>
            _search.Length == 0
            || c.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || c.Group.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();
        return [.. matches.GroupBy(c => c.Group).Select(g => new ColumnGroup(g.Key, [.. g]))];
    }

    /// <summary>True when <paramref name="id"/> is ticked.</summary>
    public bool IsChecked(string id) => _order.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ticks or unticks a column. A newly ticked column goes to the end. Name cannot be unticked and a column whose data does not
    /// exist yet cannot be ticked. Returns false when nothing changed.
    /// </summary>
    public bool SetChecked(string id, bool on)
    {
        var info = ColumnCatalog.All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (info is null || !info.Available || (!on && info.Id == FileColumns.Name.Id) || IsChecked(info.Id) == on)
        {
            return false;
        }

        if (on)
        {
            _order.Add(info.Id);
        }
        else
        {
            _order.RemoveAll(c => c.Equals(info.Id, StringComparison.OrdinalIgnoreCase));
        }

        Refresh();
        return true;
    }

    /// <summary>Moves a chosen column to <paramref name="index"/> in the order (clamped).</summary>
    public bool Move(string id, int index)
    {
        var from = _order.FindIndex(c => c.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (from < 0)
        {
            return false;
        }

        var to = Math.Clamp(index, 0, _order.Count - 1);
        if (to == from)
        {
            return false;
        }

        var moved = _order[from];
        _order.RemoveAt(from);
        _order.Insert(to, moved);
        Refresh();
        return true;
    }

    /// <summary>Goes back to the default set and order.</summary>
    public void Reset()
    {
        _order.Clear();
        _order.AddRange(Normalize(Views.FolderViewSettings.DefaultColumns));
        Refresh();
    }

    /// <summary>The columns to apply, in order.</summary>
    public IReadOnlyList<string> Result => [.. _order];

    /// <summary>Known ids only, once each, with Name added at the front when it is missing (it can never be hidden).</summary>
    private static List<string> Normalize(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (FileColumns.TryGet(id, out var column) && seen.Add(column.Id))
            {
                result.Add(column.Id);
            }
        }

        if (!seen.Contains(FileColumns.Name.Id))
        {
            result.Insert(0, FileColumns.Name.Id);
        }

        return result;
    }

    private void Refresh()
    {
        Order.Clear();
        foreach (var id in _order)
        {
            Order.Add(id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
