using Leviathan.ViewModels.Panes;
using Leviathan.ViewModels.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>What one pane's footer shows. The window works it out from the pane's tab; the footer only draws it.</summary>
/// <param name="Items">"1,244 items (0 folders, 1,244 files)".</param>
/// <param name="Selection">"1 selected · 1.55 GB", or empty.</param>
/// <param name="Filter">The filter note, or empty.</param>
/// <param name="Health">The share health and link speed, or empty.</param>
/// <param name="Free">"443 GB free of 1.82 TB", or empty.</param>
/// <param name="FreePercent">How full the drive is, 0 to 100.</param>
/// <param name="LowSpace">True when the drive is nearly full (the bar turns to warning colour).</param>
/// <param name="Layout">The pane's layout, or null without a pane.</param>
/// <param name="ThumbnailSize">The thumbnail size of the Thumbnails layout.</param>
public sealed record PaneFooterState(string Items, string Selection, string Filter, string Health, string Free, double FreePercent, bool LowSpace, ViewLayout? Layout, int ThumbnailSize)
{
    /// <summary>A footer with nothing to say.</summary>
    public static PaneFooterState Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, false, null, 0);
}

/// <summary>
/// The status line of one pane of a tab (spec §8.7, §15.1): count, selection, filter, share health, free space and the layout switches. Every
/// pane has its own, the same width as its file list. The window hooks the same handlers for each; touching a pane gives it the focus first.
/// </summary>
public sealed partial class PaneFooter : UserControl
{
    private bool _syncingSlider;

    /// <summary>Creates the footer.</summary>
    public PaneFooter() => InitializeComponent();

    /// <summary>The line of the last message of this pane (the window writes to the focused pane's).</summary>
    public TextBlock Message => MessageText;

    /// <summary>The cache notice: its panel, spinner, icon and text.</summary>
    public StackPanel Notice => NoticePart;

    /// <summary>The cache notice's spinner.</summary>
    public ProgressRing NoticeSpinner => NoticeRing;

    /// <summary>The cache notice's icon.</summary>
    public ContentControl NoticeGlyph => NoticeIcon;

    /// <summary>The cache notice's text.</summary>
    public TextBlock NoticeLabel => NoticeText;

    /// <summary>The count was clicked: show or hide hidden items.</summary>
    public event RoutedEventHandler ItemsClick { add => ItemsPart.Click += value; remove => ItemsPart.Click -= value; }

    /// <summary>The selection was clicked: select none.</summary>
    public event RoutedEventHandler SelectionClick { add => SelectionPart.Click += value; remove => SelectionPart.Click -= value; }

    /// <summary>The filter note was clicked: clear the filter.</summary>
    public event RoutedEventHandler FilterClick { add => FilterPart.Click += value; remove => FilterPart.Click -= value; }

    /// <summary>Free space was tapped: open the space map.</summary>
    public event TappedEventHandler FreeTapped { add => FreePart.Tapped += value; remove => FreePart.Tapped -= value; }

    /// <summary>A layout switch was clicked; its <c>Tag</c> says which (details, list, thumbnails).</summary>
    public event RoutedEventHandler LayoutClick
    {
        add { DetailsSwitch.Click += value; ListSwitch.Click += value; ThumbsSwitch.Click += value; }
        remove { DetailsSwitch.Click -= value; ListSwitch.Click -= value; ThumbsSwitch.Click -= value; }
    }

    /// <summary>The user moved the thumbnail size slider.</summary>
    public event EventHandler<int>? ThumbnailSizeChanged;

    /// <summary>Draws the three layout switches from the icon pack, now and whenever the pack or theme changes.</summary>
    public void BindIcons()
    {
        Icons.Bind(DetailsSwitch, static (b, i) => b.Content = i, "layout-details");
        Icons.Bind(ListSwitch, static (b, i) => b.Content = i, "layout-list");
        Icons.Bind(ThumbsSwitch, static (b, i) => b.Content = i, "layout-thumbnails");
    }

    /// <summary>Draws <paramref name="state"/>.</summary>
    public void Show(PaneFooterState state)
    {
        ItemsText.Text = state.Items;
        Fill(SelectionPart, SelectionText, state.Selection);
        Fill(FilterPart, FilterText, state.Filter);
        HealthText.Text = state.Health;
        _hasHealth = state.Health.Length > 0;
        FreeText.Text = state.Free;
        FreeBar.Value = state.FreePercent;
        FreeBar.Foreground = SidebarFormat.BarBrush(state.LowSpace);
        _hasFree = state.Free.Length > 0;

        Mark(DetailsSwitch, state.Layout == ViewLayout.Details);
        Mark(ListSwitch, state.Layout == ViewLayout.List);
        Mark(ThumbsSwitch, state.Layout == ViewLayout.Thumbnails);
        SizeSlider.Visibility = state.Layout == ViewLayout.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
        if (state.Layout is not null && Math.Abs(SizeSlider.Value - state.ThumbnailSize) > 0.5)
        {
            _syncingSlider = true;
            SizeSlider.Value = state.ThumbnailSize;
            _syncingSlider = false;
        }

        ApplyWidth(Row.ActualWidth);
    }

    private static void Fill(Button part, TextBlock text, string value)
    {
        text.Text = value;
        part.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Mark(Button button, bool on) => button.Background = on
        ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    // The free-space bar and then the health note give way when the pane is narrow (spec §15.1); PaneFooterLayout decides.
    private void OnRowSizeChanged(object sender, SizeChangedEventArgs e) => ApplyWidth(e.NewSize.Width);

    private void ApplyWidth(double width)
    {
        var parts = PaneFooterLayout.Visible(width);
        HealthText.Visibility = _hasHealth && parts.HasFlag(FooterParts.Health) ? Visibility.Visible : Visibility.Collapsed;
        FreePart.Visibility = _hasFree && parts.HasFlag(FooterParts.FreeSpace) ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _hasHealth;
    private bool _hasFree;

    private void OnSizeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingSlider)
        {
            ThumbnailSizeChanged?.Invoke(this, (int)e.NewValue);
        }
    }
}
