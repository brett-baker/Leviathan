using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Core.Model;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Leviathan.App;

/// <summary>
/// One item of the List or Thumbnails layout: an icon and name (List), or a thumbnail over a two-line name (Thumbnails). Cells
/// are recycled, so <see cref="Bind"/> resets everything, and a thumbnail still being generated for the previous item is
/// cancelled (spec §3.1: off-screen requests are cancelled).
/// </summary>
internal sealed class FlowCell : Grid
{
    private static readonly TimeSpan ThumbnailDelay = TimeSpan.FromMilliseconds(120);
    private static Brush? s_selected;
    private static Brush? s_hover;
    private static Brush? s_drop;

    private readonly FlowView _owner;
    private readonly Grid _picture = new() { IsHitTestVisible = false };
    private readonly FontIcon _glyph = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _kind = new() { IsHitTestVisible = false, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Image _image = new() { IsHitTestVisible = false, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock _name = new() { IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _focus = new()
    {
        Stroke = new SolidColorBrush(Colors.Transparent),
        StrokeThickness = 1,
        RadiusX = 4,
        RadiusY = 4,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly RenameBox _editor = new();
    private CancellationTokenSource? _loading;
    private ViewLayout _layout = ViewLayout.Details;
    private int _size;
    private bool _hover;
    private bool _dropTarget;

    /// <summary>Creates an empty cell for <paramref name="owner"/>.</summary>
    public FlowCell(FlowView owner)
    {
        _owner = owner;
        _editor.Finished += (_, result) => _owner.RaiseRenameFinished(result);
        Background = new SolidColorBrush(Colors.Transparent);
        CornerRadius = new CornerRadius(4);
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        FileDragSource.Attach(this);
    }

    /// <summary>Marks the cell as the folder a drag would drop into (spec §8.4).</summary>
    public void SetDropTarget(bool on)
    {
        if (_dropTarget != on)
        {
            _dropTarget = on;
            UpdateVisuals();
        }
    }

    /// <summary>The entry shown, or null while the cell sits in the recycle pool.</summary>
    public FileEntry? Entry { get; private set; }

    private static Brush SelectedBrush => s_selected ??= new SolidColorBrush(Color.FromArgb(0x3A, Accent.R, Accent.G, Accent.B));

    private static Brush DropBrush => s_drop ??= new SolidColorBrush(Color.FromArgb(0x70, Accent.R, Accent.G, Accent.B));

    private static Brush HoverBrush => s_hover ??= (Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush) ?? new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80));

    private static Color Accent => Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color ? color : Color.FromArgb(255, 0, 120, 212);

    // The icon pack draws the kind of item (spec 4.3) until a thumbnail replaces it; the system glyph stays for a pack without it and for high contrast.
    private void ShowKindIcon(FileEntry entry)
    {
        var picture = Icons.Shared(Formatters.IconKey(entry.Kind, entry.Attributes), _kind.Width is > 0 and < 1000 ? _kind.Width : 16);
        _kind.Source = picture;
        _kind.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows <paramref name="entry"/> in <paramref name="layout"/> at thumbnail edge <paramref name="size"/>.</summary>
    public void Bind(FileEntry entry, ViewLayout layout, int size)
    {
        CancelLoading();
        Entry = entry;
        if (layout != _layout || size != _size || Children.Count == 0)
        {
            Build(layout, size);
        }

        _name.Text = entry.Name;
        _name.Opacity = entry.IsHidden ? 0.65 : 1;
        _glyph.Glyph = Formatters.Glyph(entry.Kind, entry.Attributes);
        _glyph.Opacity = entry.IsHidden ? 0.55 : 1;
        _kind.Opacity = entry.IsHidden ? 0.55 : 1;
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        ShowKindIcon(entry);
        if (entry.Kind == FileEntryKind.File)
        {
            _ = ShowTypeIconAsync(entry, layout == ViewLayout.Thumbnails ? Math.Max(TypeIcons.RowSize, Bucket(size / 2)) : TypeIcons.RowSize);
        }

        UpdateVisuals();
        UpdateEditor();
        if (layout == ViewLayout.Thumbnails)
        {
            _ = LoadThumbnailAsync(entry, size);
        }
    }

    // The icon of the program that opens the file replaces the generic one as soon as the shell has answered for this file type.
    private async Task ShowTypeIconAsync(FileEntry entry, int pixels)
    {
        if (!TypeIcons.TryGet(entry, pixels, out var icon))
        {
            icon = await TypeIcons.GetAsync(entry, pixels);
        }

        // The cell may have been recycled for another item, or a thumbnail may already be showing.
        if (icon is not null && ReferenceEquals(Entry, entry) && _image.Visibility != Visibility.Visible)
        {
            _kind.Source = icon;
            _kind.Visibility = Visibility.Visible;
            _glyph.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Releases the entry and stops any thumbnail work for it.</summary>
    public void Unbind()
    {
        CancelLoading();
        _editor.Hide();
        _dropTarget = false;
        Entry = null;
        _image.Source = null;
    }

    /// <summary>
    /// Shows or hides the inline rename box to match the tab's rename state. Returns true when it closed a box that had the
    /// keyboard focus, so the view can take the focus back.
    /// </summary>
    public bool UpdateEditor()
    {
        if (Entry is not { } entry)
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

    /// <summary>Sets the background and focus outline from the selection model.</summary>
    public void UpdateVisuals()
    {
        if (Entry is not { } entry || _owner.Selection is not { } selection)
        {
            return;
        }

        Background = _dropTarget ? DropBrush : selection.IsSelected(entry) ? SelectedBrush : _hover ? HoverBrush : new SolidColorBrush(Colors.Transparent);
        var focused = selection.Focus == entry.Location && _owner.HasKeyboardFocus;
        _focus.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        ((SolidColorBrush)_focus.Stroke).Color = focused ? Accent : Colors.Transparent;
    }

    private void SetHover(bool hover)
    {
        _hover = hover;
        UpdateVisuals();
    }

    private void CancelLoading()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    private void Build(ViewLayout layout, int size)
    {
        _layout = layout;
        _size = size;
        _editor.Hide();
        // The glyph and image move between the picture holder (Thumbnails) and the cell itself (List).
        _picture.Children.Clear();
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        if (layout == ViewLayout.Thumbnails)
        {
            Width = ItemFlow.CellWidth(size);
            Height = ItemFlow.CellHeight(size);
            Padding = new Thickness(ItemFlow.ThumbnailPadding);
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _glyph.FontSize = Math.Max(24, size / 2.0);
            _kind.Width = _kind.Height = Math.Max(24, size / 2.0);
            _name.TextWrapping = TextWrapping.Wrap;
            _name.MaxLines = 2;
            _name.TextAlignment = TextAlignment.Center;
            _name.VerticalAlignment = VerticalAlignment.Top;
            _name.Margin = new Thickness(0, 6, 0, 0);
            _editor.TextWrapping = TextWrapping.Wrap;
            _editor.TextAlignment = TextAlignment.Center;
            _editor.VerticalAlignment = VerticalAlignment.Top;
            _editor.Margin = new Thickness(0, 4, 0, 0);
            SetRow(_editor, 1);
            SetColumn(_editor, 0);
            SetRow(_name, 1);
            _picture.Children.Add(_glyph);
            _picture.Children.Add(_kind);
            _picture.Children.Add(_image);
            Children.Add(_picture);
        }
        else
        {
            Width = ItemFlow.ListItemWidth;
            Height = ItemFlow.ListItemHeight;
            Padding = new Thickness(0);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _glyph.FontSize = 16;
            _kind.Width = _kind.Height = 16;
            _name.TextWrapping = TextWrapping.NoWrap;
            _name.MaxLines = 1;
            _name.TextAlignment = TextAlignment.Left;
            _name.VerticalAlignment = VerticalAlignment.Center;
            _name.Margin = new Thickness(0, 0, 8, 0);
            _editor.TextWrapping = TextWrapping.NoWrap;
            _editor.TextAlignment = TextAlignment.Left;
            _editor.VerticalAlignment = VerticalAlignment.Center;
            _editor.Margin = new Thickness(0, 0, 8, 0);
            SetRow(_editor, 0);
            SetColumn(_editor, 1);
            SetRow(_name, 0);
            SetColumn(_name, 1);
            Children.Add(_glyph);
            SetColumn(_kind, 0);
            Children.Add(_kind);
        }

        Children.Add(_name);
        Children.Add(_editor);
        SetRowSpan(_focus, Math.Max(1, RowDefinitions.Count));
        SetColumnSpan(_focus, Math.Max(1, ColumnDefinitions.Count));
        Children.Add(_focus);
    }

    private async Task LoadThumbnailAsync(FileEntry entry, int size)
    {
        if (entry.Kind == FileEntryKind.Folder)
        {
            return;
        }

        var cache = FlowView.Thumbnails;

        // A bucketed request size means zooming does not ask the shell for a new image at every step.
        var request = new ThumbnailRequest(entry.Location, entry.Modified, entry.Size, Bucket(size),
            entry.Location.IsNetwork ? ThumbnailMode.IconOnly : ThumbnailMode.Thumbnail);
        var loading = new CancellationTokenSource();
        _loading = loading;
        try
        {
            ThumbnailImage? image;
            if (!cache.TryGetCached(request, out image))
            {
                // A cell that is recycled or hidden within a moment (switching view, scrolling past) never reaches the shell, so
                // a burst of changes does not queue hundreds of thumbnail jobs behind the one the user waits for.
                await Task.Delay(ThumbnailDelay, loading.Token);
                image = await cache.GetAsync(request, loading.Token);
            }

            if (image is not null && !loading.IsCancellationRequested && ReferenceEquals(Entry, entry))
            {
                using var scope = UiTrace.Begin("FlowCell.Thumbnail", 5);
                var bitmap = new WriteableBitmap(image.Width, image.Height);
                using (var pixels = bitmap.PixelBuffer.AsStream())
                {
                    pixels.Write(image.Bgra.Span);
                }

                _image.Source = bitmap;
                _image.Visibility = Visibility.Visible;
                _glyph.Visibility = Visibility.Collapsed;
                _kind.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or recycled for another item.
        }
    }

    private static int Bucket(int size) => size switch
    {
        <= 64 => 64,
        <= 96 => 96,
        <= 128 => 128,
        <= 192 => 192,
        <= 256 => 256,
        <= 384 => 384,
        _ => 512,
    };
}
