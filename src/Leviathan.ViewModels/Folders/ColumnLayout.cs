namespace Leviathan.ViewModels.Folders;

/// <summary>A visible Details column with its current width.</summary>
/// <param name="Column">The column.</param>
/// <param name="Width">Width in device-independent pixels.</param>
public sealed record ColumnSlot(FileColumn Column, double Width)
{
    /// <summary>True for columns whose values are numbers or sizes and read better right-aligned.</summary>
    public bool RightAligned => Column.Id == FileColumns.Size.Id || (Column.Media is { } media && MediaColumns.IsNumeric(media));
}

/// <summary>
/// Which Details columns are shown, in what order, and how wide (spec §8.2): resize, reorder, show or hide, auto-fit.
/// The Name column is always shown. Used from the UI thread.
/// </summary>
public sealed class ColumnLayout
{
    /// <summary>Narrowest a column can be.</summary>
    public const double MinWidth = 48;

    /// <summary>Widest a column can be.</summary>
    public const double MaxWidth = 900;

    /// <summary>Space around a cell's text that auto-fit adds to the measured width.</summary>
    public const double CellPadding = 24;

    private readonly List<ColumnSlot> _slots = [];
    private readonly Dictionary<string, double> _remembered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a layout of <paramref name="ids"/> (unknown ids are skipped; Name is added if missing).</summary>
    public ColumnLayout(IEnumerable<string> ids, IReadOnlyDictionary<string, double>? widths = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (widths is not null)
        {
            foreach (var (id, width) in widths)
            {
                _remembered[id] = Clamp(width);
            }
        }

        foreach (var id in ids)
        {
            if (FileColumns.TryGet(id, out var column) && _slots.All(s => s.Column != column))
            {
                _slots.Add(new ColumnSlot(column, WidthOf(column)));
            }
        }

        if (_slots.All(s => s.Column != FileColumns.Name))
        {
            _slots.Insert(0, new ColumnSlot(FileColumns.Name, WidthOf(FileColumns.Name)));
        }
    }

    /// <summary>Raised after any change. <c>true</c> means the set or order of columns changed, <c>false</c> that only widths did.</summary>
    public event EventHandler<bool>? Changed;

    /// <summary>The visible columns, left to right.</summary>
    public IReadOnlyList<ColumnSlot> Slots => _slots;

    /// <summary>Ids of the visible columns, left to right.</summary>
    public IReadOnlyList<string> Ids => [.. _slots.Select(s => s.Column.Id)];

    /// <summary>Widths of every column ever sized, including hidden ones, so a hidden column comes back as it was.</summary>
    public IReadOnlyDictionary<string, double> Widths
    {
        get
        {
            var all = new Dictionary<string, double>(_remembered, StringComparer.OrdinalIgnoreCase);
            foreach (var slot in _slots)
            {
                all[slot.Column.Id] = slot.Width;
            }

            return all;
        }
    }

    /// <summary>Sum of the visible widths.</summary>
    public double TotalWidth => _slots.Sum(s => s.Width);

    /// <summary>Width a column starts with.</summary>
    public static double DefaultWidth(FileColumn column) => column.Id switch
    {
        "name" => 380,
        "size" or "ownsize" or "items" or "totalfiles" or "totalfolders" => 100,
        "modified" or "created" => 160,
        "type" => 130,
        "extension" => 90,
        "attributes" => 90,
        "tags" => 160,

        // The media columns: short values get short columns, the two that hold lists get room for "eng, jpn, fre" and "3 (eng, fre)".
        "duration" or "framerate" or "channels" or "hdr" => 100,
        "audiolanguages" or "subtitles" => 150,
        "resolution" or "bitrate" or "container" or "videocodec" or "audiocodec" => 110,
        _ => 120,
    };

    /// <summary>True when <paramref name="id"/> is shown.</summary>
    public bool IsVisible(string id) => _slots.Any(s => s.Column.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Left edge of a column.</summary>
    public double OffsetOf(string id)
    {
        var offset = 0.0;
        foreach (var slot in _slots)
        {
            if (slot.Column.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return offset;
            }

            offset += slot.Width;
        }

        return -1;
    }

    /// <summary>Index of the column under horizontal position <paramref name="x"/>, or the last index when x is past the right edge.</summary>
    public int IndexAt(double x)
    {
        var edge = 0.0;
        for (var i = 0; i < _slots.Count; i++)
        {
            edge += _slots[i].Width;
            if (x < edge)
            {
                return i;
            }
        }

        return _slots.Count - 1;
    }

    /// <summary>Sets a column's width, within <see cref="MinWidth"/> and <see cref="MaxWidth"/>.</summary>
    public void Resize(string id, double width)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        var clamped = Clamp(width);
        if (_slots[index].Width == clamped)
        {
            return;
        }

        _slots[index] = _slots[index] with { Width = clamped };
        Changed?.Invoke(this, false);
    }

    /// <summary>Moves a column to <paramref name="newIndex"/> (clamped to the valid range).</summary>
    public void Move(string id, int newIndex)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return;
        }

        var target = Math.Clamp(newIndex, 0, _slots.Count - 1);
        if (target == index)
        {
            return;
        }

        var slot = _slots[index];
        _slots.RemoveAt(index);
        _slots.Insert(target, slot);
        Changed?.Invoke(this, true);
    }

    /// <summary>
    /// Replaces the set and order of columns (the column chooser, spec §8.2). Columns that stay keep their width, columns that come
    /// back get the width they had, unknown ids are skipped and Name is added at the front if missing.
    /// </summary>
    public void SetColumns(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var slot in _slots)
        {
            _remembered[slot.Column.Id] = slot.Width;
        }

        var before = Ids;
        _slots.Clear();
        foreach (var id in ids)
        {
            if (FileColumns.TryGet(id, out var column) && _slots.All(s => s.Column != column))
            {
                _slots.Add(new ColumnSlot(column, WidthOf(column)));
            }
        }

        if (_slots.All(s => s.Column != FileColumns.Name))
        {
            _slots.Insert(0, new ColumnSlot(FileColumns.Name, WidthOf(FileColumns.Name)));
        }

        if (!before.SequenceEqual(Ids))
        {
            Changed?.Invoke(this, true);
        }
    }

    /// <summary>Shows or hides a column. Name cannot be hidden. A column shown again goes to the end with its remembered width.</summary>
    public void SetVisible(string id, bool visible)
    {
        if (!FileColumns.TryGet(id, out var column) || column == FileColumns.Name)
        {
            return;
        }

        var index = IndexOf(id);
        if (visible && index < 0)
        {
            _slots.Add(new ColumnSlot(column, WidthOf(column)));
            Changed?.Invoke(this, true);
        }
        else if (!visible && index >= 0)
        {
            _remembered[column.Id] = _slots[index].Width;
            _slots.RemoveAt(index);
            Changed?.Invoke(this, true);
        }
    }

    /// <summary>Sizes a column to its widest cell (double-click on the header edge).</summary>
    /// <param name="id">Column id.</param>
    /// <param name="texts">The cell texts, including the header.</param>
    /// <param name="measure">Width of a text in the cell font.</param>
    /// <param name="extra">Extra width every cell needs, such as the icon in the Name column.</param>
    public void AutoFit(string id, IEnumerable<string> texts, Func<string, double> measure, double extra = 0)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(measure);
        var widest = 0.0;
        foreach (var text in texts)
        {
            if (!string.IsNullOrEmpty(text))
            {
                widest = Math.Max(widest, measure(text));
            }
        }

        Resize(id, widest + extra + CellPadding);
    }

    private static double Clamp(double width) => Math.Clamp(width, MinWidth, MaxWidth);

    private int IndexOf(string id) => _slots.FindIndex(s => s.Column.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private double WidthOf(FileColumn column) =>
        _remembered.TryGetValue(column.Id, out var width) ? width : DefaultWidth(column);
}
