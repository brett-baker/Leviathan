using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Leviathan.App;

/// <summary>
/// One row of the Details grid: an icon and name plus one text cell per visible column, laid out on the grid's column widths.
/// Rows are recycled while scrolling, so <see cref="Bind"/> only sets texts and brushes; the cells are rebuilt only when the
/// set or order of columns changes (spec §3.1: scrolling 100,000 rows must stay smooth).
/// </summary>
internal sealed class GridRow : Grid
{
    /// <summary>Row height in device-independent pixels. The same for every row, so the grid can compute scroll positions without measuring; it follows the density and icon size of the Appearance settings.</summary>
    public static double RowHeight { get; set; } = 30;

    /// <summary>Size of the icon in front of a name (Appearance: icon size).</summary>
    public static double IconSize { get; set; } = 16;

    private const double IconWidth = 24;

    private static Brush? s_selected;
    private static Brush? s_hover;
    private static Brush? s_drop;
    private static Brush? s_secondary;

    private readonly DetailsGrid _owner;
    private readonly List<Cell> _cells = [];
    private readonly Rectangle _focus = new()
    {
        Stroke = new SolidColorBrush(Colors.Transparent),
        StrokeThickness = 1,
        RadiusX = 3,
        RadiusY = 3,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private string _signature = string.Empty;
    private RenameBox? _editor;
    private FontIcon? _nameIcon;
    private Image? _nameImage;
    private TextBlock? _nameText;
    private bool _hover;
    private bool _dropTarget;

    /// <summary>Takes the row height and icon size again (the density or icon size was changed).</summary>
    public void ApplyMetrics()
    {
        Height = RowHeight;
        if (_nameImage is not null)
        {
            _nameImage.Width = IconSize;
            _nameImage.Height = IconSize;
        }

        if (_nameIcon is not null)
        {
            _nameIcon.FontSize = IconSize * 0.875;
        }
    }

    /// <summary>Creates an empty row for <paramref name="owner"/>.</summary>
    public GridRow(DetailsGrid owner)
    {
        _owner = owner;
        Height = RowHeight;
        Background = new SolidColorBrush(Colors.Transparent);
        CornerRadius = new CornerRadius(4);
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        FileDragSource.Attach(this);
    }

    /// <summary>Marks the row as the folder a drag would drop into (spec §8.4).</summary>
    public void SetDropTarget(bool on)
    {
        if (_dropTarget != on)
        {
            _dropTarget = on;
            UpdateVisuals();
        }
    }

    /// <summary>The entry shown, or null while the row sits in the recycle pool.</summary>
    public FileEntry? Entry { get; private set; }

    private static Brush SelectedBrush => s_selected ??= new SolidColorBrush(Color.FromArgb(0x3A, Accent.R, Accent.G, Accent.B));

    private static Brush DropBrush => s_drop ??= new SolidColorBrush(Color.FromArgb(0x70, Accent.R, Accent.G, Accent.B));

    private static Brush HoverBrush => s_hover ??= (Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush) ?? new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80));

    private static Brush SecondaryBrush => s_secondary ??= (Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush) ?? new SolidColorBrush(Colors.Gray);

    private static Color Accent => Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color ? color : Color.FromArgb(255, 0, 120, 212);

    /// <summary>What the cell of <paramref name="columnId"/> shows for <paramref name="entry"/>.</summary>
    public static string TextFor(string columnId, FileEntry entry, FolderSizeTracker sizes) => columnId switch
    {
        "name" => entry.Name,
        "size" => sizes.TextFor(entry),
        "modified" => DisplayFormat.Date(entry.Modified),
        "created" => DisplayFormat.Date(entry.Created),
        "type" => FileColumns.TypeName(entry),
        "extension" => entry.Extension.TrimStart('.'),
        "attributes" => AttributeText(entry.Attributes),
        _ => string.Empty,
    };

    /// <summary>Width the icon of the Name column takes in front of the text.</summary>
    public static double NameExtra => IconWidth;

    /// <summary>Shows <paramref name="entry"/>.</summary>
    public void Bind(FileEntry entry)
    {
        Entry = entry;

        // A copy a running job has not finished creating here (the "UI updates: Immediate" setting).
        Opacity = entry.IsPending ? 0.45 : 1;
        EnsureCells();
        Fill();
        UpdateVisuals();
        UpdateEditor();
    }

    /// <summary>
    /// True when <paramref name="point"/> (in this row's coordinates) is on the icon or the text of the name. A drag from there
    /// is for drag and drop (D9); a drag from the rest of the row draws the rubber band (spec §8.4).
    /// </summary>
    public bool HitsName(Windows.Foundation.Point point)
    {
        if (_nameIcon is null || _nameText is null)
        {
            return false;
        }

        foreach (var part in new FrameworkElement[] { _nameIcon, _nameText })
        {
            var origin = part.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            if (point.X >= origin.X && point.X <= origin.X + part.ActualWidth && point.Y >= origin.Y && point.Y <= origin.Y + part.ActualHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Releases the entry so a recycled row holds nothing.</summary>
    public void Unbind()
    {
        _editor?.Hide();
        _dropTarget = false;
        Entry = null;
    }

    /// <summary>
    /// Shows or hides the inline rename box to match the tab's rename state. Returns true when it closed a box that had the
    /// keyboard focus, so the grid can take the focus back.
    /// </summary>
    public bool UpdateEditor()
    {
        if (_editor is null || Entry is not { } entry)
        {
            return false;
        }

        var wanted = _owner.Renaming == entry.Location;
        if (wanted && !_editor.IsOpen)
        {
            _editor.Begin(entry);
        }
        else if (!wanted && _editor.IsOpen)
        {
            return _editor.Hide();
        }

        return false;
    }

    /// <summary>Applies the current column widths and the row width.</summary>
    public void ApplyWidths()
    {
        var slots = _owner.Layout?.Slots;
        if (slots is null || ColumnDefinitions.Count != slots.Count)
        {
            return;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            ColumnDefinitions[i].Width = new GridLength(slots[i].Width);
        }

        Width = _owner.RowWidth;
    }

    /// <summary>Sets the background and focus outline from the selection model.</summary>
    public void UpdateVisuals()
    {
        if (Entry is not { } entry || _owner.Selection is not { } selection)
        {
            return;
        }

        var selected = selection.IsSelected(entry);
        Background = _dropTarget ? DropBrush : selected ? SelectedBrush : _hover ? HoverBrush : new SolidColorBrush(Colors.Transparent);
        var focused = selection.Focus == entry.Location && _owner.HasKeyboardFocus;
        _focus.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        ((SolidColorBrush)_focus.Stroke).Color = focused ? Accent : Colors.Transparent;
    }

    /// <summary>Refreshes the Size cell (a folder total arrived).</summary>
    public void RefreshSize()
    {
        if (Entry is { } entry)
        {
            foreach (var cell in _cells.Where(c => c.Metric is not null))
            {
                RefreshMetricCell(cell, entry);
            }
        }
    }

    /// <summary>Refreshes the media cells (information about this file arrived).</summary>
    public void RefreshMedia()
    {
        if (Entry is { } entry)
        {
            foreach (var cell in _cells.Where(c => c.Media is not null))
            {
                RefreshMediaCell(cell, entry);
            }
        }
    }

    /// <summary>Refreshes the Tags cell (the tags of this file were read or changed).</summary>
    public void RefreshTags()
    {
        if (Entry is { } entry)
        {
            foreach (var cell in _cells.Where(c => c.ColumnId == FileColumns.Tags.Id))
            {
                RefreshTagCell(cell, entry);
            }
        }
    }

    // The Tags cell: one coloured dot per tag in the user's order, the names as the tip (spec §11.1). Empty for a folder or a file without tags.
    private void RefreshTagCell(Cell cell, FileEntry entry)
    {
        var tags = _owner.FileTags?.TagsOf(entry) ?? [];
        cell.Text.Inlines.Clear();
        foreach (var tag in tags)
        {
            cell.Text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "● ", Foreground = new SolidColorBrush(TagColour(tag.Colour)) });
        }

        ToolTipService.SetToolTip(cell.Text, tags.Count == 0 ? null : string.Join(", ", tags.Select(t => t.Name)));
    }

    private static Color TagColour(string hex) =>
        hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Colors.Gray;

    // One cell of Duration, Resolution and the other media columns: the value, a dim "…" while the file waits to be read, "—" with the reason as the tip when it could not be.
    private void RefreshMediaCell(Cell cell, FileEntry entry)
    {
        var media = _owner.Media;
        var text = media is null ? string.Empty : media.TextFor(entry, cell.Media!.Value);
        cell.Text.Text = text;
        cell.Text.Opacity = text == "…" ? 0.5 : 1;
        var tip = media?.DetailFor(entry) ?? string.Empty;
        ToolTipService.SetToolTip(cell.Text, tip.Length == 0 ? null : tip);
    }

    // One cell of Size, Own size, Items, Total files or Total folders: text, italic when out of date, tip, and the bar.
    private void RefreshMetricCell(Cell cell, FileEntry entry)
    {
        var sizes = _owner.Sizes;
        cell.Text.Text = sizes is null ? string.Empty : sizes.TextFor(entry, cell.Metric!.Value);
        cell.Text.FontStyle = sizes?.IsStale(entry) == true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        var progress = sizes?.ProgressFor(entry);

        // Waiting for its turn: the placeholder is dimmed and the tip says so; no bar until it is being walked.
        cell.Text.Opacity = progress is { IsRunning: false } ? 0.5 : 1;
        if (cell.Bar is { } bar)
        {
            SetSizeTip(cell.Text, entry);
            if (progress is { IsRunning: false })
            {
                ToolTipService.SetToolTip(cell.Text, "Waiting to be counted");
            }

            if (progress is { IsRunning: true } running)
            {
                bar.IsIndeterminate = running.Fraction is null;
                bar.Value = running.Fraction ?? 0;
                bar.Visibility = Visibility.Visible;
            }
            else
            {
                bar.IsIndeterminate = false;
                bar.Visibility = Visibility.Collapsed;
            }
        }
    }

    // What the number holds and how old it is (spec §12.7); files keep no tip.
    private void SetSizeTip(TextBlock text, FileEntry entry)
    {
        var tip = entry.Kind == Leviathan.Core.Model.FileEntryKind.File ? string.Empty : _owner.Sizes?.DetailFor(entry.Location) ?? string.Empty;
        ToolTipService.SetToolTip(text, tip.Length == 0 ? null : tip);
    }

    private static string AttributeText(FileAttributes attributes)
    {
        Span<char> text = stackalloc char[4];
        text[0] = attributes.HasFlag(FileAttributes.ReadOnly) ? 'R' : '-';
        text[1] = attributes.HasFlag(FileAttributes.Hidden) ? 'H' : '-';
        text[2] = attributes.HasFlag(FileAttributes.System) ? 'S' : '-';
        text[3] = attributes.HasFlag(FileAttributes.Archive) ? 'A' : '-';
        return new string(text);
    }

    private void SetHover(bool hover)
    {
        _hover = hover;
        UpdateVisuals();
    }

    private void EnsureCells()
    {
        var slots = _owner.Layout?.Slots ?? [];
        var signature = string.Join(',', slots.Select(s => s.Column.Id));
        if (signature == _signature)
        {
            ApplyWidths();
            return;
        }

        _signature = signature;
        Children.Clear();
        ColumnDefinitions.Clear();
        _cells.Clear();
        _editor = null;
        _nameIcon = null;
        _nameImage = null;
        _nameText = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(slot.Width) });
            var text = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = slot.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                IsHitTestVisible = false,
            };
            FontIcon? icon = null;
            FrameworkElement element = text;
            if (slot.Column == FileColumns.Name)
            {
                icon = new FontIcon { FontSize = IconSize * 0.875, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false };
                _nameImage = new Image { Width = IconSize, Height = IconSize, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                var panel = new Grid { IsHitTestVisible = false };
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.Children.Add(icon);
                panel.Children.Add(_nameImage);
                SetColumn(text, 1);
                text.Margin = new Thickness(8, 0, 8, 0);
                panel.Children.Add(text);
                _nameIcon = icon;
                _nameText = text;
                _editor = new RenameBox { Margin = new Thickness(4, 0, 8, 0) };
                _editor.Finished += (_, result) => _owner.RaiseRenameFinished(result);
                SetColumn(_editor, 1);
                panel.Children.Add(_editor);
                panel.IsHitTestVisible = true;
                element = panel;
            }
            else
            {
                text.Foreground = SecondaryBrush;
            }

            // The Size cell carries the 2 px bar of a folder being counted (spec §12.7); other cells are just text.
            ProgressBar? bar = null;
            if (slot.Column == FileColumns.Size)
            {
                bar = new ProgressBar
                {
                    Height = 2,
                    MinHeight = 2,
                    Padding = new Thickness(0),
                    Maximum = 1,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(8, 0, 8, 1),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                };
                var holder = new Grid { IsHitTestVisible = false };
                holder.Children.Add(text);
                holder.Children.Add(bar);
                element = holder;
            }

            SetColumn(element, i);
            Children.Add(element);
            _cells.Add(new Cell(slot.Column.Id, text, icon, slot.Column.Metric, bar, slot.Column.Media));
        }

        SetColumnSpan(_focus, Math.Max(1, slots.Count));
        Children.Add(_focus);
        ApplyWidths();
    }

    private void Fill()
    {
        var entry = Entry!;
        var sizes = _owner.Sizes;
        foreach (var cell in _cells)
        {
            if (cell.Metric is not null)
            {
                RefreshMetricCell(cell, entry);
                continue;
            }

            if (cell.Media is not null)
            {
                RefreshMediaCell(cell, entry);
                continue;
            }

            if (cell.ColumnId == FileColumns.Tags.Id)
            {
                RefreshTagCell(cell, entry);
                continue;
            }

            cell.Text.Text = TextFor(cell.ColumnId, entry, sizes!);
            if (cell.Icon is { } icon)
            {
                // The icon pack draws the kind of item (spec 4.3); the system glyph stays for a pack without it and for high contrast.
                var picture = Icons.Shared(Formatters.IconKey(entry.Kind, entry.Attributes), IconSize);
                if (picture is not null && _nameImage is not null)
                {
                    _nameImage.Source = picture;
                    _nameImage.Opacity = entry.IsHidden ? 0.55 : 1;
                    _nameImage.Visibility = Visibility.Visible;
                    icon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (_nameImage is not null)
                    {
                        _nameImage.Visibility = Visibility.Collapsed;
                    }

                    icon.Visibility = Visibility.Visible;
                    icon.Glyph = Formatters.Glyph(entry.Kind, entry.Attributes);
                    icon.Opacity = entry.IsHidden ? 0.55 : 1;
                }

                cell.Text.Opacity = entry.IsHidden ? 0.65 : 1;
                if (entry.Kind == Leviathan.Core.Model.FileEntryKind.File && _nameImage is not null)
                {
                    _ = ShowTypeIconAsync(entry, icon);
                }
            }
        }
    }

    // The icon of the program that opens the file replaces the generic one as soon as the shell has answered for this file type.
    private async Task ShowTypeIconAsync(FileEntry entry, FontIcon glyph)
    {
        if (!TypeIcons.TryGet(entry, TypeIcons.RowSize, out var icon))
        {
            icon = await TypeIcons.GetAsync(entry, TypeIcons.RowSize);
        }

        // The row may have been recycled for another item meanwhile.
        if (icon is not null && ReferenceEquals(Entry, entry) && _nameImage is not null)
        {
            _nameImage.Source = icon;
            _nameImage.Visibility = Visibility.Visible;
            glyph.Visibility = Visibility.Collapsed;
        }
    }

    private sealed record Cell(string ColumnId, TextBlock Text, FontIcon? Icon, FolderMetric? Metric = null, ProgressBar? Bar = null, MediaColumnKind? Media = null);
}
