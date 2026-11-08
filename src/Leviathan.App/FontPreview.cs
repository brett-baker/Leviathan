using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>
/// A font in the preview pane (spec §9.1: "sample text at several sizes"). XAML text cannot load a font from an arbitrary file, so the
/// samples are drawn by GDI+ from a private font collection (the font is never installed) into one transparent picture off the UI thread,
/// in the text colour of the current theme, and shown as an image. A file GDI+ cannot read (WOFF, bitmap FON) says so instead.
/// </summary>
internal sealed partial class FontPreview : Grid
{
    private static readonly float[] Sizes = [12, 18, 28, 44, 64];
    private const long MaxBytes = 64L << 20;

    private readonly Microsoft.UI.Xaml.Controls.Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };

    private int _generation;

    public FontPreview()
    {
        _scroll.Content = _image;
        Children.Add(_scroll);
        Children.Add(_message);
    }

    /// <summary>Loads the font at <paramref name="path"/>; throws <see cref="OperationCanceledException"/> when the selection moves on first.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        Clear();
        var generation = _generation;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)(Math.Max(200, ActualWidth - 40) * scale);
        var dark = ActualTheme == ElementTheme.Dark;
        try
        {
            var png = await Task.Run(() => Render(path, width, (float)scale, dark), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
            if (generation != _generation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (png is null)
            {
                Fail($"{Path.GetFileName(path)}\nThis kind of font cannot be previewed here; its details are below.");
                return;
            }

            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken);
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
            if (generation == _generation)
            {
                _image.Source = bitmap;
                _image.Width = bitmap.PixelWidth / scale;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Fail("This font took too long to read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"This font could not be read: {ex.Message}");
        }
    }

    /// <summary>Empties the pane.</summary>
    public void Clear()
    {
        _generation++;
        _image.Source = null;
        _message.Visibility = Visibility.Collapsed;
    }

    private void Fail(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    // Draws the samples into a PNG, or returns null when the file is not a font GDI+ can use. Runs off the UI thread.
    private static byte[]? Render(string path, int width, float scale, bool dark)
    {
        if (new FileInfo(path).Length > MaxBytes)
        {
            return null;
        }

        using var collection = new PrivateFontCollection();
        try
        {
            collection.AddFontFile(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or ExternalException)
        {
            return null;
        }

        if (collection.Families.Length == 0)
        {
            return null;
        }

        var family = collection.Families[0];
        var style = new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic }.FirstOrDefault(family.IsStyleAvailable, FontStyle.Regular);
        var lines = new List<(string Text, float Size)>
        {
            (family.Name, 20),
            ("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 18),
            ("abcdefghijklmnopqrstuvwxyz", 18),
            ("0123456789 !?&@#%()[]{}", 18),
        };
        lines.AddRange(Sizes.Select(s => ("The quick brown fox jumps over the lazy dog", s)));

        var gap = (int)(6 * scale);
        using var brush = new SolidBrush(dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(25, 25, 25));
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoClip };
        using var probe = new Bitmap(1, 1);
        using var measure = Graphics.FromImage(probe);
        var heights = lines.Select(l =>
        {
            using var font = new Font(family, l.Size * scale, style, GraphicsUnit.Pixel);
            return (int)Math.Ceiling(measure.MeasureString(l.Text, font, width, format).Height) + gap;
        }).ToList();

        using var bitmap = new Bitmap(width, heights.Sum() + gap, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var y = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            using var font = new Font(family, lines[i].Size * scale, style, GraphicsUnit.Pixel);
            g.DrawString(lines[i].Text, font, brush, new RectangleF(0, y, width, heights[i]), format);
            y += heights[i];
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }
}
