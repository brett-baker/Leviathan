using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leviathan.App;

/// <summary>One rectangle of the mini space map: what it is called, how heavy it is, and whether it is a folder.</summary>
internal sealed record MapItem(string Label, double Weight, bool IsFolder);

/// <summary>
/// The pane for things that are summarised rather than shown (spec §9.1): the inside of an archive, a folder (item counts and a mini space
/// map of what is in it) and a selection of several items (count, size, type breakdown and a strip of thumbnails). It holds a headline,
/// an optional squarified treemap (<see cref="TreemapLayout"/>), an optional strip of picture thumbnails decoded small, and a list of
/// text rows, and is filled by the window after the work of finding them is done off the UI thread.
/// </summary>
internal sealed partial class SummaryPreview : Grid
{
    private const double MapHeight = 96;

    private readonly TextBlock _headline = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 8, 10, 4), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 0, 10, 4) };
    private readonly ScrollViewer _stripScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled };
    private readonly Canvas _map = new() { Height = MapHeight, Margin = new Thickness(10, 0, 10, 6), Visibility = Visibility.Collapsed };
    private readonly ListView _rows = new() { SelectionMode = ListViewSelectionMode.None, Padding = new Thickness(0) };
    private IReadOnlyList<MapItem> _mapItems = [];

    public SummaryPreview()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _stripScroll.Content = _strip;
        _stripScroll.Visibility = Visibility.Collapsed;
        SetRow(_stripScroll, 1);
        SetRow(_map, 2);
        SetRow(_rows, 3);
        _rows.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock FontSize='12' TextTrimming='CharacterEllipsis' /></DataTemplate>");
        _rows.ItemContainerStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'>" +
            "<Setter Property='MinHeight' Value='0' /><Setter Property='Padding' Value='10,1,10,1' /></Style>");
        _rows.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is TextBlock block && args.Item is string text)
            {
                block.Text = text;
                ToolTipService.SetToolTip(block, text);
            }
        };
        Children.Add(_headline);
        Children.Add(_stripScroll);
        Children.Add(_map);
        Children.Add(_rows);
        _map.SizeChanged += (_, _) => DrawMap();
    }

    /// <summary>Shows the headline and rows, with an optional map and picture strip.</summary>
    public void Show(string headline, IReadOnlyList<string> rows, IReadOnlyList<MapItem>? map = null, IReadOnlyList<BitmapImage>? thumbnails = null)
    {
        _headline.Text = headline;
        _rows.ItemsSource = rows;
        _mapItems = map ?? [];
        _map.Visibility = _mapItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawMap();
        _strip.Children.Clear();
        foreach (var bitmap in thumbnails ?? [])
        {
            _strip.Children.Add(new Image { Source = bitmap, Height = 56, Stretch = Stretch.UniformToFill, Width = 56 });
        }

        _stripScroll.Visibility = _strip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Empties the pane.</summary>
    public void Clear()
    {
        _headline.Text = string.Empty;
        _rows.ItemsSource = null;
        _mapItems = [];
        _map.Children.Clear();
        _map.Visibility = Visibility.Collapsed;
        _strip.Children.Clear();
        _stripScroll.Visibility = Visibility.Collapsed;
    }

    private void DrawMap()
    {
        _map.Children.Clear();
        var width = _map.ActualWidth;
        if (_mapItems.Count == 0 || width <= 0)
        {
            return;
        }

        var rects = TreemapLayout.Layout([.. _mapItems.Select(i => i.Weight)], width, MapHeight);
        for (var i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            if (r.Width < 1 || r.Height < 1)
            {
                continue;
            }

            var item = _mapItems[i];
            var tile = new Border
            {
                Width = Math.Max(0, r.Width - 1),
                Height = Math.Max(0, r.Height - 1),
                Background = new SolidColorBrush(item.IsFolder ? Windows.UI.Color.FromArgb(255, 59, 130, 196) : Shade(i)),
                Child = r.Width > 44 && r.Height > 16
                    ? new TextBlock { Text = item.Label, FontSize = 10, Margin = new Thickness(3, 1, 3, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
                    : null,
            };
            ToolTipService.SetToolTip(tile, item.Label);
            Canvas.SetLeft(tile, r.X);
            Canvas.SetTop(tile, r.Y);
            _map.Children.Add(tile);
        }
    }

    // Files take muted greens and greys so folders (blue) stand out; neighbours differ a little so tiles are told apart.
    private static Windows.UI.Color Shade(int i) => (i % 3) switch
    {
        0 => Windows.UI.Color.FromArgb(255, 96, 140, 110),
        1 => Windows.UI.Color.FromArgb(255, 120, 130, 140),
        _ => Windows.UI.Color.FromArgb(255, 150, 120, 100),
    };
}
