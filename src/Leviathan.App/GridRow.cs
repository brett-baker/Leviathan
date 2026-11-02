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
    /// <summary>Row height in device-independent pixels. Fixed, so the grid can compute scroll positions without measuring.</summary>
    public const double RowHeight = 30;

    private const double IconWidth = 24;

    private static Brush? s_selected;
    private static Brush? s_hover;
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
    private bool _hover;

    /// <summary>Creates an empty row for <paramref name="owner"/>.</summary>
    public GridRow(DetailsGrid owner)
    {
        _owner = owner;
        Height = RowHeight;
        Background = new SolidColorBrush(Colors.Transparent);
        CornerRadius = new CornerRadius(4);
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
    }

    /// <summary>The entry shown, or null while the row sits in the recycle pool.</summary>
    public FileEntry? Entry { get; private set; }

    private static Brush SelectedBrush => s_selected ??= new SolidColorBrush(Color.FromArgb(0x3A, Accent.R, Accent.G, Accent.B));

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
        EnsureCells();
        Fill();
        UpdateVisuals();
    }

    /// <summary>Releases the entry so a recycled row holds nothing.</summary>
    public void Unbind() => Entry = null;

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
        Background = selected ? SelectedBrush : _hover ? HoverBrush : new SolidColorBrush(Colors.Transparent);
        var focused = selection.Focus == entry.Location && _owner.HasKeyboardFocus;
        _focus.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        ((SolidColorBrush)_focus.Stroke).Color = focused ? Accent : Colors.Transparent;
    }

    /// <summary>Refreshes the Size cell (a folder total arrived).</summary>
    public void RefreshSize()
    {
        if (Entry is { } entry)
        {
            foreach (var cell in _cells.Where(c => c.ColumnId == "size"))
            {
                cell.Text.Text = _owner.Sizes is { } sizes ? sizes.TextFor(entry) : string.Empty;
                cell.Text.FontStyle = _owner.Sizes?.IsStale(entry) == true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            }
        }
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
                icon = new FontIcon { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsHitTestVisible = false };
                var panel = new Grid { IsHitTestVisible = false };
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                panel.Children.Add(icon);
                SetColumn(text, 1);
                text.Margin = new Thickness(8, 0, 8, 0);
                panel.Children.Add(text);
                element = panel;
            }
            else
            {
                text.Foreground = SecondaryBrush;
            }

            SetColumn(element, i);
            Children.Add(element);
            _cells.Add(new Cell(slot.Column.Id, text, icon));
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
            cell.Text.Text = sizes is null && cell.ColumnId == "size" ? string.Empty : TextFor(cell.ColumnId, entry, sizes!);
            if (cell.Icon is { } icon)
            {
                icon.Glyph = Formatters.Glyph(entry.Kind, entry.Attributes);
                icon.Opacity = entry.IsHidden ? 0.55 : 1;
                cell.Text.Opacity = entry.IsHidden ? 0.65 : 1;
            }
            else if (cell.ColumnId == "size")
            {
                cell.Text.FontStyle = sizes?.IsStale(entry) == true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            }
        }
    }

    private sealed record Cell(string ColumnId, TextBlock Text, FontIcon? Icon);
}
