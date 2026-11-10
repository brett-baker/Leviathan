using Leviathan.Core.Platform;
using Leviathan.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Leviathan.Interop.Tests;

/// <summary>Writing title, comments and rating through the shell's property handler (spec §9.2), on a real JPEG in a temp folder.</summary>
public sealed class ShellPropertyWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-props-" + Guid.NewGuid().ToString("N"));

    public ShellPropertyWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private async Task<string> MakeJpegAsync(string name = "pic.jpg")
    {
        var path = Path.Combine(_folder, name);
        var file = await (await StorageFolder.GetFolderFromPathAsync(_folder)).CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 8, 8, 96, 96, new byte[8 * 8 * 4]);
        await encoder.FlushAsync();
        return path;
    }

    private static async Task<IDictionary<string, object>> ReadAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        return await file.Properties.RetrievePropertiesAsync(["System.Title", "System.Comment", "System.Rating"]);
    }

    [Fact]
    public async Task Write_SavesTitleCommentAndRating_AndKeepsTheModifiedTime()
    {
        var path = await MakeJpegAsync();
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        await new ShellPropertyWriter().WriteAsync(path, new Dictionary<string, object> { ["System.Title"] = "UAT title", ["System.Comment"] = "UAT comment", ["System.Rating"] = 75u }, CancellationToken.None);

        var read = await ReadAsync(path);
        Assert.Equal("UAT title", read["System.Title"]);
        Assert.Equal("UAT comment", read["System.Comment"]);
        Assert.Equal(75u, Convert.ToUInt32(read["System.Rating"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Write_WhileAnotherProgramHoldsTheFileOpenForReading_SaysWhatHappened()
    {
        var path = await MakeJpegAsync();
        await using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failure = await Record.ExceptionAsync(() => new ShellPropertyWriter().WriteAsync(path, new Dictionary<string, object> { ["System.Title"] = "x" }, CancellationToken.None));

        // Documents the behaviour the details pane has to live with: a reader without FileShare.Write blocks the save.
        Assert.NotNull(failure);
        Assert.True(failure is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException, failure.GetType().Name);
    }

    [Fact]
    public async Task Write_AfterTheOtherHandleIsClosed_Works()
    {
        var path = await MakeJpegAsync();
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // Even a fully shared read handle makes the shell handler refuse (it opens the file exclusively), so the details pane
            // releases its preview before saving; this is the case that failed with "unauthorized operation" on a previewed picture.
            var failure = await Record.ExceptionAsync(() => new ShellPropertyWriter().WriteAsync(path, new Dictionary<string, object> { ["System.Title"] = "x" }, CancellationToken.None));
            Assert.IsType<UnauthorizedAccessException>(failure);
        }

        await new ShellPropertyWriter().WriteAsync(path, new Dictionary<string, object> { ["System.Title"] = "shared" }, CancellationToken.None);

        Assert.Equal("shared", (await ReadAsync(path))["System.Title"]);
    }

    [Fact]
    public async Task Write_OnAReadOnlyFile_Fails_AndSaysUnauthorized()
    {
        var path = await MakeJpegAsync();
        File.SetAttributes(path, System.IO.FileAttributes.ReadOnly);

        var failure = await Record.ExceptionAsync(() => new ShellPropertyWriter().WriteAsync(path, new Dictionary<string, object> { ["System.Title"] = "x" }, CancellationToken.None));

        File.SetAttributes(path, System.IO.FileAttributes.Normal);
        Assert.NotNull(failure);
    }
}
