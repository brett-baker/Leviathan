using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>
/// A PDF in the preview pane (spec §9.1): Windows.Data.Pdf draws each page to a picture, and only the pages in view are drawn, because
/// the list is virtualized and a page is rendered when its row is realized. The file is read into memory off the UI thread (at most
/// <see cref="MaxBytes"/>, with the 5 s load timeout), so no page ever waits on the disk. A damaged or password-protected file says so.
/// Text selection inside a page is not offered by the system renderer; the page is a picture.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The document is dropped by Clear, which every new file and the window closing call; a control cannot express that as IDisposable.")]
internal sealed partial class PdfPreview : Grid
{
    /// <summary>The largest PDF read for the pane (64 MiB).</summary>
    public const long MaxBytes = 64L << 20;

    private const int MaxPages = 300;

    private readonly ListView _pages = new() { SelectionMode = ListViewSelectionMode.None, Padding = new Thickness(0) };
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };

    private readonly Border _badge = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(6),
        Padding = new Thickness(6, 2, 6, 2),
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(190, 0, 0, 0)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _badgeText = new() { FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    private PdfDocument? _document;
    private int _generation;

    public PdfPreview()
    {
        _pages.ItemTemplate = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Image Stretch='Uniform' MinHeight='160' />" +
            "</DataTemplate>");
        _pages.ItemContainerStyle = (Style)XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'>" +
            "<Setter Property='MinHeight' Value='0' /><Setter Property='Padding' Value='4,2,4,2' /><Setter Property='HorizontalContentAlignment' Value='Stretch' /></Style>");
        _pages.ContainerContentChanging += OnContainerContentChanging;
        _badge.Child = _badgeText;
        Children.Add(_pages);
        Children.Add(_message);
        Children.Add(_badge);
    }

    /// <summary>Loads <paramref name="path"/>; throws <see cref="OperationCanceledException"/> when the selection moves on first.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        Clear();
        var generation = _generation;
        try
        {
            var bytes = await Task.Run(() => Read(path), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancellationToken);
            stream.Seek(0);
            var document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(cancellationToken);
            if (generation != _generation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _document = document;
            var count = (int)Math.Min(document.PageCount, MaxPages);
            _pages.ItemsSource = Enumerable.Range(0, count).ToList();
            _badgeText.Text = document.PageCount > MaxPages ? $"{document.PageCount:N0} pages · first {MaxPages} shown" : $"{document.PageCount:N0} pages";
            _badge.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Fail("This PDF took too long to open.");
        }
        catch (InvalidDataException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"This PDF could not be read: {ex.Message}");
        }
        catch (COMException)
        {
            Fail("This PDF is password-protected or damaged, so there is nothing to show.");
        }
    }

    /// <summary>Drops the document and empties the list.</summary>
    public void Clear()
    {
        _generation++;
        _pages.ItemsSource = null;
        _document = null;
        _message.Visibility = Visibility.Collapsed;
        _badge.Visibility = Visibility.Collapsed;
    }

    private static byte[] Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (file.Length > MaxBytes)
        {
            throw new InvalidDataException($"This PDF is too large to preview ({file.Length >> 20} MB).");
        }

        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private void Fail(string text)
    {
        _pages.ItemsSource = null;
        _badge.Visibility = Visibility.Collapsed;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    private async void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Image image || args.Item is not int index || _document is not { } document)
        {
            return;
        }

        var generation = _generation;
        image.Source = null;
        image.Tag = index;
        try
        {
            using var page = document.GetPage((uint)index);
            var width = Math.Max(200, ActualWidth - 24) * (XamlRoot?.RasterizationScale ?? 1);
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)width };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            if (generation != _generation || image.Tag is not int current || current != index)
            {
                return; // the row was recycled for another page, or the file changed
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (generation == _generation && image.Tag is int still && still == index)
            {
                image.Source = bitmap;
                image.MinHeight = 0;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // One page that will not draw leaves its row blank; the rest of the file still shows.
        }
    }
}
