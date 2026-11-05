using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Hub;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Leviathan.App;

/// <summary>
/// The body of the conflict dialog (spec §12.4): one rule for all the clashes of a job, a rule per file, and a Compare flyout that
/// shows the new file and the one already there side by side with their thumbnails and details. The choices live in the
/// <see cref="ConflictDialogModel"/>; this class only shows them.
/// </summary>
internal sealed partial class ConflictDialogContent : UserControl
{
    private const int ThumbnailSize = 160;

    private readonly ConflictDialogModel _model;
    private bool _loading;

    /// <summary>Creates the content for <paramref name="model"/>.</summary>
    public ConflictDialogContent(ConflictDialogModel model)
    {
        _model = model;
        InitializeComponent();
        _loading = true;
        ForAllBox.ItemsSource = ConflictRuleOption.All;
        ForAllBox.SelectedItem = ConflictRuleOption.All.First(o => o.Policy == model.ForAll);
        RowList.ItemsSource = model.Rows;
        RefreshUnlisted();
        _loading = false;
    }

    private void RefreshUnlisted()
    {
        UnlistedText.Text = _model.UnlistedText;
        UnlistedText.Visibility = _model.UnlistedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnForAllChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ForAllBox.SelectedItem is ConflictRuleOption option)
        {
            _model.ForAll = option.Policy;
            RefreshUnlisted();
        }
    }

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConflictRow row } button)
        {
            return;
        }

        var item = row.Item;
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4, MinWidth = 380 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var newer = item.SourceModified.CompareTo(item.TargetModified);
        var larger = item.SourceSize.CompareTo(item.TargetSize);
        AddSide(grid, 0, "New", item.Source, item.SourceSize, item.SourceModified, newer > 0, larger > 0, item.SourceModified);
        AddSide(grid, 1, "Already there", item.Target, item.TargetSize, item.TargetModified, newer < 0, larger < 0, item.TargetModified);
        new Flyout { Content = grid, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Left }.ShowAt(button);
    }

    // One side of the comparison: heading, thumbnail (filled in when the shell has it), name, size and time. The better value is bold.
    private static void AddSide(Grid grid, int column, string heading, string location, long size, DateTimeOffset modified, bool isNewer, bool isLarger, DateTimeOffset stamp)
    {
        var picture = new Image { Width = ThumbnailSize, Height = ThumbnailSize, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var path = LocationPath.Parse(location);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = heading, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(picture);
        panel.Children.Add(new TextBlock { Text = path.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        panel.Children.Add(Detail(DisplayFormat.Size(size), isLarger ? "larger" : string.Empty, isLarger));
        panel.Children.Add(Detail(DisplayFormat.Date(modified), isNewer ? "newer" : string.Empty, isNewer));
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        _ = LoadThumbnailAsync(picture, path, stamp, size);
    }

    private static TextBlock Detail(string text, string better, bool isBetter) => new()
    {
        Text = better.Length > 0 ? $"{text} ({better})" : text,
        FontWeight = isBetter ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)Application.Current.Resources[isBetter ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
    };

    private static async Task LoadThumbnailAsync(Image target, LocationPath path, DateTimeOffset modified, long length)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var image = await App.Host.Thumbnails.GetAsync(new ThumbnailRequest(path, modified, length, ThumbnailSize, ThumbnailMode.Thumbnail), timeout.Token);
            if (image is null)
            {
                return;
            }

            var bitmap = new WriteableBitmap(image.Width, image.Height);
            using (var pixels = bitmap.PixelBuffer.AsStream())
            {
                pixels.Write(image.Bgra.Span);
            }

            target.Source = bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.Runtime.InteropServices.COMException)
        {
            // No thumbnail: the details beside it say enough.
        }
    }
}
