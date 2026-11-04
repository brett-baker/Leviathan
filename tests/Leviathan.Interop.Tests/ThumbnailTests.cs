using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Interop.Tests;

public sealed class ThumbnailTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leviathan-thumbs-" + Guid.NewGuid().ToString("N"));

    public ThumbnailTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>A 32 by 32 24-bit BMP, red in the top half and blue in the bottom half. BMP rows are stored bottom first.</summary>
    private string WriteTwoColorBitmap(string name)
    {
        const int size = 32;
        var rowBytes = size * 3;
        var pixelBytes = rowBytes * size;
        var file = new byte[54 + pixelBytes];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(54).CopyTo(file, 10);
        BitConverter.GetBytes(40).CopyTo(file, 14);
        BitConverter.GetBytes(size).CopyTo(file, 18);
        BitConverter.GetBytes(size).CopyTo(file, 22);
        BitConverter.GetBytes((short)1).CopyTo(file, 26);
        BitConverter.GetBytes((short)24).CopyTo(file, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(file, 34);
        for (var row = 0; row < size; row++)
        {
            var isTopHalf = row >= size / 2; // file row 0 is the bottom of the picture
            for (var x = 0; x < size; x++)
            {
                var offset = 54 + (row * rowBytes) + (x * 3);
                file[offset] = (byte)(isTopHalf ? 0 : 255);       // blue
                file[offset + 1] = 0;                              // green
                file[offset + 2] = (byte)(isTopHalf ? 255 : 0);   // red
            }
        }

        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    private static ThumbnailRequest Request(string path, int size = 32, ThumbnailMode mode = ThumbnailMode.Thumbnail)
    {
        var info = new FileInfo(path);
        return new ThumbnailRequest(LocationPath.Parse(path), info.LastWriteTimeUtc, info.Exists ? info.Length : 0, size, mode);
    }

    // The hosted Windows runner has no working shell thumbnail provider (a bitmap comes back black or as the generic icon), so the
    // pixel checks can only run on a real desktop. They pass there; the rest of this class does not depend on the shell's pixels.
    private static bool IsHostedRunner => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    private const string HostedRunnerReason = "the GitHub-hosted runner's shell cannot render thumbnails";

    private static (byte B, byte G, byte R, byte A) Pixel(ThumbnailImage image, int x, int y)
    {
        var span = image.Bgra.Span;
        var i = ((y * image.Width) + x) * 4;
        return (span[i], span[i + 1], span[i + 2], span[i + 3]);
    }

    [Fact]
    public async Task Thumbnail_OfAnImage_IsUprightBgra_RedOnTopBlueBelow()
    {
        Assert.SkipWhen(IsHostedRunner, HostedRunnerReason);
        using var source = new ShellThumbnailSource();
        var path = WriteTwoColorBitmap("two.bmp");

        var image = await source.CreateAsync(Request(path), CancellationToken.None);

        Assert.NotNull(image);
        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        var top = Pixel(image, image.Width / 2, 2);
        var bottom = Pixel(image, image.Width / 2, image.Height - 3);
        Assert.True(top.R > 200 && top.B < 60, $"top pixel should be red, was B{top.B} G{top.G} R{top.R} A{top.A}");
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom pixel should be blue, was B{bottom.B} G{bottom.G} R{bottom.R} A{bottom.A}");
        Assert.True(top.A > 200 && bottom.A > 200, "pixels should be opaque");
    }

    [Fact]
    public async Task Thumbnail_AskedForAgain_StaysUpright_WhetherMadeFreshOrServedFromTheShellCache()
    {
        // The shell returns a bottom-up bitmap when it serves a thumbnail from its own cache; GetObject cannot tell the two apart.
        Assert.SkipWhen(IsHostedRunner, HostedRunnerReason);
        using var source = new ShellThumbnailSource();
        var path = WriteTwoColorBitmap("again.bmp");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var image = await source.CreateAsync(Request(path, 128), CancellationToken.None);
            Assert.NotNull(image);
            AssertRedOnTop(image);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var cached = await source.CreateAsync(Request(path, 128, ThumbnailMode.CachedOnly), CancellationToken.None);
        if (cached is not null)
        {
            AssertRedOnTop(cached);
        }
    }

    private static void AssertRedOnTop(ThumbnailImage image)
    {
        var top = Pixel(image, image.Width / 2, 2);
        var bottom = Pixel(image, image.Width / 2, image.Height - 3);
        Assert.True(top.R > 200 && top.B < 60, $"top pixel should be red, was B{top.B} G{top.G} R{top.R}");
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom pixel should be blue, was B{bottom.B} G{bottom.G} R{bottom.R}");
    }

    [Fact]
    public async Task IconOnly_OfATextFile_GivesTheFileTypeIcon()
    {
        using var source = new ShellThumbnailSource();
        var path = Path.Combine(_folder, "note.txt");
        await File.WriteAllTextAsync(path, "hello");

        var image = await source.CreateAsync(Request(path, 48, ThumbnailMode.IconOnly), CancellationToken.None);

        Assert.NotNull(image);
        Assert.InRange(image.Width, 16, 256);
    }

    [Fact]
    public async Task Thumbnail_OfAFolder_IsAnImage()
    {
        using var source = new ShellThumbnailSource();

        var image = await source.CreateAsync(Request(_folder, 64, ThumbnailMode.IconOnly), CancellationToken.None);

        Assert.NotNull(image);
    }

    [Fact]
    public async Task Thumbnail_OfAMissingFile_IsNull_NotAnException()
    {
        using var source = new ShellThumbnailSource();

        var image = await source.CreateAsync(Request(Path.Combine(_folder, "gone.jpg")), CancellationToken.None);

        Assert.Null(image);
    }

    [Fact]
    public async Task CreateAsync_AlreadyCancelled_Throws()
    {
        using var source = new ShellThumbnailSource();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CreateAsync(Request(_folder), cts.Token));
    }

    [Fact]
    public async Task StaThreadPool_RunsWorkInASingleThreadedApartment_OffTheCallingThread()
    {
        using var pool = new StaThreadPool(2);
        var callerThread = Environment.CurrentManagedThreadId;

        var (apartment, thread) = await pool.RunAsync(() => (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId), CancellationToken.None);

        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(callerThread, thread);
    }

    [Fact]
    public async Task StaThreadPool_WorkCancelledWhileQueued_NeverRuns()
    {
        using var pool = new StaThreadPool(1);
        var release = new ManualResetEventSlim();
        var blocker = pool.RunAsync(() => { release.Wait(); return 0; }, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var ran = false;

        var queued = pool.RunAsync(() => { ran = true; return 1; }, cts.Token);
        await cts.CancelAsync();
        release.Set();
        await blocker;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(ran);
    }

    [Fact]
    public async Task StaThreadPool_AnExceptionInTheWork_FailsOnlyThatRequest()
    {
        using var pool = new StaThreadPool(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RunAsync<int>(() => throw new InvalidOperationException("boom"), CancellationToken.None));
        Assert.Equal(7, await pool.RunAsync(() => 7, CancellationToken.None));
    }
}
