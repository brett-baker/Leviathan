using System.Runtime.InteropServices;
using Leviathan.Core.Images;
using Leviathan.Core.Paths;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.Interop;

/// <summary>
/// <see cref="IImageEditor"/> on the Windows imaging component (spec §6.2): decodes with the transform applied, encodes in the target
/// format to a <c>*.leviathan-part</c> file and renames it into place, so a crash leaves no half-written picture (spec §3.4).
/// The original is never touched. Embedded metadata (EXIF, colour profile) is not carried over, and the orientation tag is applied to the pixels.
/// </summary>
public sealed class WicImageEditor : IImageEditor
{
    private const string PartSuffix = ".leviathan-part";

    /// <inheritdoc />
    public async Task<LocationPath> EditAsync(LocationPath source, ImageEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edit);
        var from = source.Value;
        var folder = Path.GetDirectoryName(from) ?? throw new ImageEditException($"{from} has no folder.");
        var format = edit.Format ?? ImageEditRules.FormatOf(Path.GetExtension(from));
        var part = Path.Combine(folder, Guid.NewGuid().ToString("N") + PartSuffix);
        try
        {
            await Task.Run(() => WriteAsync(from, part, edit, format, cancellationToken), cancellationToken).ConfigureAwait(false);
            return LocationPath.Parse(MoveIntoPlace(part, folder, Path.GetFileName(from), edit));
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or NotSupportedException or InvalidCastException)
        {
            throw new ImageEditException($"Windows could not edit this picture: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(part);
        }
    }

    private static async Task WriteAsync(string from, string part, ImageEdit edit, ImageFormat format, CancellationToken ct)
    {
        using var input = File.OpenRead(from);
        using var inputStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(input);
        var decoder = await BitmapDecoder.CreateAsync(inputStream).AsTask(ct).ConfigureAwait(false);

        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        switch (edit.Kind)
        {
            case ImageEditKind.RotateLeft:
                transform.Rotation = BitmapRotation.Clockwise270Degrees;
                break;
            case ImageEditKind.RotateRight:
                transform.Rotation = BitmapRotation.Clockwise90Degrees;
                break;
            case ImageEditKind.FlipHorizontal:
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case ImageEditKind.FlipVertical:
                transform.Flip = BitmapFlip.Vertical;
                break;
            case ImageEditKind.Resize:
                transform.ScaledWidth = Scaled(decoder.OrientedPixelWidth, edit.Percent);
                transform.ScaledHeight = Scaled(decoder.OrientedPixelHeight, edit.Percent);
                break;
        }

        var alpha = format is ImageFormat.Jpeg or ImageFormat.Bmp ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight;
        using var pixels = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            alpha,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

        using var output = new FileStream(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var outputStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(output);
        var options = Options(format);
        var encoder = await (options is null
            ? BitmapEncoder.CreateAsync(EncoderId(format), outputStream)
            : BitmapEncoder.CreateAsync(EncoderId(format), outputStream, options)).AsTask(ct).ConfigureAwait(false);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static uint Scaled(uint size, int percent) => (uint)Math.Max(1, Math.Round(size * (double)percent / 100));

    private static Guid EncoderId(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => BitmapEncoder.JpegEncoderId,
        ImageFormat.Bmp => BitmapEncoder.BmpEncoderId,
        ImageFormat.Gif => BitmapEncoder.GifEncoderId,
        ImageFormat.Tiff => BitmapEncoder.TiffEncoderId,
        _ => BitmapEncoder.PngEncoderId,
    };

    private static BitmapPropertySet? Options(ImageFormat format)
    {
        if (format != ImageFormat.Jpeg)
        {
            return null;
        }

        // The default quality of the encoder is low enough to show; 0.92 is what most editors use.
        return new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) } };
    }

    /// <summary>Renames the finished file to the first free name; another process taking the name in between just means the next number.</summary>
    private static string MoveIntoPlace(string part, string folder, string sourceName, ImageEdit edit)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var name = ImageEditRules.OutputName(sourceName, edit, n => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n)));
            var target = Path.Combine(folder, name);
            try
            {
                File.Move(part, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Lost a race for the name: look again.
            }
        }

        throw new ImageEditException("Could not find a free name for the new picture.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover part file is harmless; the next sweep of the folder's part files removes it.
        }
    }
}
