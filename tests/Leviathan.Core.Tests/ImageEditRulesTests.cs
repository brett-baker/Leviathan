using Leviathan.Core.Images;

namespace Leviathan.Core.Tests;

public sealed class ImageEditRulesTests
{
    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".JPEG", true)]
    [InlineData(".png", true)]
    [InlineData(".webp", true)]
    [InlineData(".heic", true)]
    [InlineData(".txt", false)]
    [InlineData(".svg", false)]
    [InlineData(".cr2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanEdit_IsTrueForPicturesWindowsCanRead(string? extension, bool expected) =>
        Assert.Equal(expected, ImageEditRules.CanEdit(extension));

    [Theory]
    [InlineData("photo.jpg", ImageEditKind.RotateLeft, "photo (rotated left).jpg")]
    [InlineData("photo.PNG", ImageEditKind.RotateRight, "photo (rotated right).png")]
    [InlineData("a.bmp", ImageEditKind.FlipHorizontal, "a (flipped horizontally).bmp")]
    [InlineData("a.gif", ImageEditKind.FlipVertical, "a (flipped vertically).gif")]
    [InlineData("scan.tif", ImageEditKind.RotateLeft, "scan (rotated left).tif")]
    public void OutputName_KeepsTheFormat_AndSaysWhatWasDone(string source, ImageEditKind kind, string expected) =>
        Assert.Equal(expected, ImageEditRules.OutputName(source, new ImageEdit(kind), _ => false));

    [Fact]
    public void OutputName_ForAResize_NamesThePercentage() =>
        Assert.Equal("photo (50%).jpg", ImageEditRules.OutputName("photo.jpg", new ImageEdit(ImageEditKind.Resize, 50), _ => false));

    [Fact]
    public void OutputName_ForAConversion_ChangesTheExtensionOnly() =>
        Assert.Equal("photo.png", ImageEditRules.OutputName("photo.jpg", new ImageEdit(ImageEditKind.Convert, Format: ImageFormat.Png), _ => false));

    [Theory]
    [InlineData("shot.webp")]
    [InlineData("shot.heic")]
    public void OutputName_OfAPictureWindowsCannotWrite_IsAPng(string source) =>
        Assert.Equal("shot (rotated left).png", ImageEditRules.OutputName(source, new ImageEdit(ImageEditKind.RotateLeft), _ => false));

    [Fact]
    public void OutputName_NumbersTheName_WhileItIsTaken()
    {
        var taken = new HashSet<string>(["photo (rotated left).jpg", "photo (rotated left) (2).jpg"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal("photo (rotated left) (3).jpg", ImageEditRules.OutputName("photo.jpg", new ImageEdit(ImageEditKind.RotateLeft), taken.Contains));
    }

    [Fact]
    public void OutputName_NeverReturnsTheOriginal()
    {
        // Converting a PNG to PNG would write over the picture itself.
        var name = ImageEditRules.OutputName("photo.png", new ImageEdit(ImageEditKind.Convert, Format: ImageFormat.Png), _ => false);

        Assert.Equal("photo (2).png", name);
    }

    [Fact]
    public void Verb_NamesTheEdit()
    {
        Assert.Equal("Resized to 25%", ImageEditRules.Verb(new ImageEdit(ImageEditKind.Resize, 25)));
        Assert.Equal("Converted to JPEG", ImageEditRules.Verb(new ImageEdit(ImageEditKind.Convert, Format: ImageFormat.Jpeg)));
        Assert.Equal("Rotated right", ImageEditRules.Verb(new ImageEdit(ImageEditKind.RotateRight)));
    }
}
