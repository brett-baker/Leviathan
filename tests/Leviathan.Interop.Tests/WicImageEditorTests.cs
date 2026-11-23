using Leviathan.Core.Images;
using Leviathan.Core.Paths;
using Windows.Graphics.Imaging;

namespace Leviathan.Interop.Tests;

/// <summary>The Windows imaging component on this machine: a real PNG goes in, a real picture of the right size comes out.</summary>
public sealed class WicImageEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-wic-" + Guid.NewGuid().ToString("N"));
    private readonly WicImageEditor _editor = new();

    public WicImageEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>A 40 by 20 picture: red left half, blue right half, so a flip or a turn can be seen in the pixels.</summary>
    private async Task<string> MakePictureAsync(string name = "wide.png")
    {
        var path = Path.Combine(_root, name);
        var bytes = new byte[40 * 20 * 4];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var i = ((y * 40) + x) * 4;
                bytes[i] = (byte)(x < 20 ? 0 : 255);
                bytes[i + 2] = (byte)(x < 20 ? 255 : 0);
                bytes[i + 3] = 255;
            }
        }

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 40, 20, 96, 96, bytes);
        await encoder.FlushAsync();
        stream.Seek(0);
        var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        var written = new byte[stream.Size];
        reader.ReadBytes(written);
        await File.WriteAllBytesAsync(path, written, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task<(uint Width, uint Height, byte[] Pixels)> ReadAsync(string path)
    {
        using var file = File.OpenRead(path);
        using var stream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (decoder.PixelWidth, decoder.PixelHeight, data.DetachPixelData());
    }

    private static string Pixel(byte[] pixels, uint width, int x, int y)
    {
        var i = (int)((y * width) + x) * 4;
        return pixels[i + 2] > 128 ? "red" : pixels[i] > 128 ? "blue" : "other";
    }

    private async Task<string> EditAsync(string source, ImageEdit edit) =>
        (await _editor.EditAsync(LocationPath.Parse(source), edit, TestContext.Current.CancellationToken)).Value;

    [Fact]
    public async Task RotateRight_SwapsTheSides_AndTurnsTheLeftHalfToTheTop()
    {
        var source = await MakePictureAsync();

        var result = await EditAsync(source, new ImageEdit(ImageEditKind.RotateRight));

        var (width, height, pixels) = await ReadAsync(result);
        Assert.Equal((20u, 40u), (width, height));
        Assert.Equal("red", Pixel(pixels, width, 10, 5));
        Assert.Equal("blue", Pixel(pixels, width, 10, 35));
        Assert.Equal("wide (rotated right).png", Path.GetFileName(result));
    }

    [Fact]
    public async Task RotateLeft_TurnsTheLeftHalfToTheBottom()
    {
        var source = await MakePictureAsync();

        var (width, height, pixels) = await ReadAsync(await EditAsync(source, new ImageEdit(ImageEditKind.RotateLeft)));

        Assert.Equal((20u, 40u), (width, height));
        Assert.Equal("blue", Pixel(pixels, width, 10, 5));
        Assert.Equal("red", Pixel(pixels, width, 10, 35));
    }

    [Fact]
    public async Task FlipHorizontal_MirrorsTheHalves_WithoutChangingTheSize()
    {
        var source = await MakePictureAsync();

        var (width, height, pixels) = await ReadAsync(await EditAsync(source, new ImageEdit(ImageEditKind.FlipHorizontal)));

        Assert.Equal((40u, 20u), (width, height));
        Assert.Equal("blue", Pixel(pixels, width, 5, 10));
        Assert.Equal("red", Pixel(pixels, width, 35, 10));
    }

    [Fact]
    public async Task Resize_ScalesToThePercentage()
    {
        var source = await MakePictureAsync();

        var (width, height, _) = await ReadAsync(await EditAsync(source, new ImageEdit(ImageEditKind.Resize, 50)));

        Assert.Equal((20u, 10u), (width, height));
    }

    [Fact]
    public async Task Convert_WritesTheOtherFormat_WhichDecodesToTheSameSize()
    {
        var source = await MakePictureAsync();

        var result = await EditAsync(source, new ImageEdit(ImageEditKind.Convert, Format: ImageFormat.Jpeg));

        Assert.Equal("wide.jpg", Path.GetFileName(result));
        var bytes = await File.ReadAllBytesAsync(result, TestContext.Current.CancellationToken);
        Assert.Equal([0xFF, 0xD8], bytes[..2]);
        var (width, height, _) = await ReadAsync(result);
        Assert.Equal((40u, 20u), (width, height));
    }

    [Fact]
    public async Task TheOriginalIsUntouched_NoPartFileIsLeft_AndASecondEditIsNumbered()
    {
        var source = await MakePictureAsync();
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);

        var first = await EditAsync(source, new ImageEdit(ImageEditKind.RotateLeft));
        var second = await EditAsync(source, new ImageEdit(ImageEditKind.RotateLeft));

        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.NotEqual(first, second);
        Assert.Equal("wide (rotated left) (2).png", Path.GetFileName(second));
        Assert.Empty(Directory.GetFiles(_root, "*.leviathan-part"));
    }

    [Fact]
    public async Task AFileThatIsNotAPicture_FailsWithAnImageEditException_AndLeavesNothingBehind()
    {
        var path = Path.Combine(_root, "fake.png");
        await File.WriteAllTextAsync(path, "not a picture", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ImageEditException>(() => EditAsync(path, new ImageEdit(ImageEditKind.RotateLeft)));

        Assert.Equal(["fake.png"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }
}
