using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>An axis-aligned rectangle in view coordinates, used for rubber-band hit testing.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct SelectionRect(double X, double Y, double Width, double Height)
{
    /// <summary>True when the rectangles overlap (touching edges do not count).</summary>
    public bool Intersects(SelectionRect other) =>
        X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;

    /// <summary>The rectangle spanned by two corner points, whichever way the drag went.</summary>
    public static SelectionRect FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
}

/// <summary>
/// Selection for the file view (spec §8.4): click, Ctrl-click, Shift-click ranges, Space, arrow keys with Shift and Ctrl,
/// select all / none / invert / by pattern, rubber-band and type-ahead. Items are tracked by location, not row index, so
/// the selection survives re-sorting, filtering and rows arriving from a streaming listing. The index of rows is rebuilt
/// lazily on the next user action, never per arriving batch. Used from the UI thread.
/// </summary>
public sealed class SelectionModel
{
    /// <summary>How long a pause ends a type-ahead word.</summary>
    public static readonly TimeSpan TypeAheadTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<FileEntry> _rows;
    private readonly HashSet<LocationPath> _selected = [];
    private readonly TimeProvider _time;
    private Dictionary<LocationPath, int>? _index;
    private LocationPath? _anchor;
    private string _typed = string.Empty;
    private long _lastTyped;

    /// <summary>Creates a model over <paramref name="rows"/>; if the list raises change events the index is refreshed.</summary>
    public SelectionModel(IReadOnlyList<FileEntry> rows, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = rows;
        _time = time ?? TimeProvider.System;
        if (rows is INotifyCollectionChanged notifying)
        {
            notifying.CollectionChanged += (_, _) => _index = null;
        }
    }

    /// <summary>Raised whenever the selection or focus changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The item with the keyboard focus rectangle, or null.</summary>
    public LocationPath? Focus { get; private set; }

    /// <summary>Number of selected items that are currently in the view.</summary>
    public int Count => SelectedEntries.Count;

    /// <summary>Selected items in display order.</summary>
    public IReadOnlyList<FileEntry> SelectedEntries
    {
        get
        {
            Prune();
            return [.. _rows.Where(r => _selected.Contains(r.Location))];
        }
    }

    /// <summary>Total bytes of the selected files (folder totals come from the folder-size service).</summary>
    public long SelectedBytes => SelectedEntries.Where(e => e.Kind == FileEntryKind.File).Sum(e => e.Size);

    /// <summary>True when <paramref name="entry"/> is selected.</summary>
    public bool IsSelected(FileEntry entry) => entry is not null && _selected.Contains(entry.Location);

    /// <summary>Mouse click on a row. Ctrl toggles, Shift selects a range from the anchor, Ctrl+Shift adds the range.</summary>
    public void Click(FileEntry entry, bool ctrl = false, bool shift = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (shift && _anchor is not null && IndexOf(_anchor) >= 0)
        {
            SelectRangeTo(entry, keepExisting: ctrl);
        }
        else if (ctrl)
        {
            Toggle(entry);
            return;
        }
        else
        {
            _selected.Clear();
            _selected.Add(entry.Location);
            _anchor = entry.Location;
        }

        Focus = entry.Location;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Space in keyboard mode, or a checkbox: toggles one item and makes it the anchor.</summary>
    public void Toggle(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_selected.Remove(entry.Location))
        {
            _selected.Add(entry.Location);
        }

        _anchor = entry.Location;
        Focus = entry.Location;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects every row (Ctrl+A).</summary>
    public void SelectAll()
    {
        _selected.Clear();
        foreach (var row in _rows)
        {
            _selected.Add(row.Location);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the selection (Ctrl+Shift+A). Focus stays.</summary>
    public void Clear()
    {
        _selected.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects what is not selected and deselects the rest (Ctrl+I).</summary>
    public void Invert()
    {
        var next = _rows.Select(r => r.Location).Where(l => !_selected.Contains(l)).ToList();
        _selected.Clear();
        foreach (var location in next)
        {
            _selected.Add(location);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects rows whose name matches a wildcard pattern such as <c>*.mkv</c> (spec §6.2). Returns how many matched.</summary>
    public int SelectByPattern(string pattern, bool add = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!add)
        {
            _selected.Clear();
        }

        var matched = 0;
        foreach (var row in _rows.Where(r => regex.IsMatch(r.Name)))
        {
            _selected.Add(row.Location);
            matched++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return matched;
    }

    /// <summary>
    /// Arrow, Home, End, PageUp and PageDown. <paramref name="delta"/> is the signed row distance (int.MinValue / int.MaxValue
    /// for Home and End). Shift extends the range from the anchor; Ctrl moves only the focus; neither replaces the selection.
    /// </summary>
    public void Move(int delta, bool shift = false, bool ctrl = false)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var current = Focus is null ? -1 : IndexOf(Focus);
        var target = current < 0 ? (delta >= 0 ? 0 : _rows.Count - 1) : (int)Math.Clamp((long)current + delta, 0, _rows.Count - 1);
        var entry = _rows[target];
        if (ctrl && !shift)
        {
            Focus = entry.Location;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (shift)
        {
            _anchor ??= Focus ?? entry.Location;
            SelectRangeTo(entry, keepExisting: false);
            Focus = entry.Location;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Click(entry);
    }

    /// <summary>
    /// Type-ahead: a letter or digit jumps to the first row whose name starts with the characters typed within the last
    /// second (spec §5.3). Returns the row jumped to, or null when nothing matches.
    /// </summary>
    public FileEntry? TypeAhead(char character)
    {
        var now = _time.GetTimestamp();
        if (_typed.Length > 0 && _time.GetElapsedTime(_lastTyped, now) > TypeAheadTimeout)
        {
            _typed = string.Empty;
        }

        _typed += character;
        _lastTyped = now;

        var start = Focus is null ? 0 : Math.Max(0, IndexOf(Focus));
        // Typing the same letter repeatedly cycles through matches; a longer word searches from the focus first.
        var cycling = _typed.Length > 1 && _typed.All(c => c == _typed[0]);
        var prefix = cycling ? _typed[..1] : _typed;
        for (var step = cycling ? 1 : 0; step < _rows.Count + (cycling ? 1 : 0); step++)
        {
            var row = _rows[(start + step) % _rows.Count];
            if (row.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Click(row);
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Rubber-band selection: selects the rows whose rectangles intersect <paramref name="band"/>. With
    /// <paramref name="selectedBeforeDrag"/> (Ctrl held) the rows selected before the drag started stay selected.
    /// </summary>
    public void ApplyRubberBand(IReadOnlyList<SelectionRect> rowRects, SelectionRect band, IReadOnlySet<LocationPath>? selectedBeforeDrag = null)
    {
        ArgumentNullException.ThrowIfNull(rowRects);
        _selected.Clear();
        if (selectedBeforeDrag is not null)
        {
            foreach (var location in selectedBeforeDrag)
            {
                _selected.Add(location);
            }
        }

        for (var i = 0; i < Math.Min(rowRects.Count, _rows.Count); i++)
        {
            if (rowRects[i].Intersects(band))
            {
                _selected.Add(_rows[i].Location);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A copy of the current selection, to pass as the "before drag" set for an additive rubber band.</summary>
    public IReadOnlySet<LocationPath> Snapshot() => new HashSet<LocationPath>(_selected);

    private void SelectRangeTo(FileEntry entry, bool keepExisting)
    {
        var from = _anchor is null ? -1 : IndexOf(_anchor);
        var to = IndexOf(entry.Location);
        if (to < 0)
        {
            return;
        }

        if (from < 0)
        {
            from = to;
            _anchor = entry.Location;
        }

        if (!keepExisting)
        {
            _selected.Clear();
        }

        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _selected.Add(_rows[i].Location);
        }
    }

    private int IndexOf(LocationPath location)
    {
        if (_index is null)
        {
            _index = new Dictionary<LocationPath, int>(_rows.Count);
            for (var i = 0; i < _rows.Count; i++)
            {
                _index[_rows[i].Location] = i;
            }
        }

        return _index.TryGetValue(location, out var found) ? found : -1;
    }

    /// <summary>Drops selected items that are no longer in the view (filtered out, deleted) and a focus that vanished.</summary>
    private void Prune()
    {
        if (_selected.Count > 0)
        {
            _selected.RemoveWhere(l => IndexOf(l) < 0);
        }

        if (Focus is not null && IndexOf(Focus) < 0)
        {
            Focus = null;
        }
    }
}
