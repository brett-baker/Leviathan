using System.IO.Abstractions;
using Leviathan.Engine.Transfers;

namespace Leviathan.Interop.Tests;

/// <summary>The real Windows pieces on a real disk: CopyFile2, alternate data streams and unbuffered I/O.</summary>
public sealed class WindowsFileCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-copier-" + Guid.NewGuid().ToString("N"));

    public WindowsFileCopierTests() => Directory.CreateDirectory(_root);

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

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    private string MakeSource(int length)
    {
        var path = Path.Combine(_root, "source ✓.bin");
        File.WriteAllBytes(path, Data(length));
        File.SetLastWriteTimeUtc(path, new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public async Task ServerSideCopy_CopiesWithCopyFile2_ReportsBytes_KeepsTimes_AndLeavesNoPartFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CopyFile2");
        var source = MakeSource(5_000_001);
        var target = Path.Combine(_root, "target.bin");
        long total = 0;

        var result = await new WindowsFileCopier().CopyAsync(new FileCopyRequest(source, target, TransferPath.ServerSideCopy, false, n => total += n), CancellationToken.None);

        Assert.Equal(5_000_001, result.Length);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        Assert.Equal(new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc(target));
        Assert.Equal(5_000_001, total);
        Assert.False(File.Exists(target + CopyMoveExecutor.PartSuffix));
    }

    [Fact]
    public async Task ServerSideCopy_OfAMissingSource_FailsWithAnIoException_AndLeavesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CopyFile2");
        var target = Path.Combine(_root, "t.bin");
        var source = MakeSource(10);
        File.Delete(source);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new WindowsFileCopier().CopyAsync(new FileCopyRequest(source, target, TransferPath.ServerSideCopy, false, _ => { }), CancellationToken.None));
        Assert.False(File.Exists(target + CopyMoveExecutor.PartSuffix));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ServerSideCopy_AlreadyCancelled_DoesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CopyFile2");
        var source = MakeSource(10);
        var target = Path.Combine(_root, "t.bin");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileCopier().CopyAsync(new FileCopyRequest(source, target, TransferPath.ServerSideCopy, false, _ => { }), cts.Token));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ServerSideCopy_CarriesAlternateStreams()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams");
        var source = MakeSource(100);
        await File.WriteAllTextAsync(source + ":color", "green");
        var target = Path.Combine(_root, "t.bin");

        await new WindowsFileCopier().CopyAsync(new FileCopyRequest(source, target, TransferPath.ServerSideCopy, false, _ => { }), CancellationToken.None);

        Assert.Equal("green", await File.ReadAllTextAsync(target + ":color"));
    }

    [Fact]
    public async Task ChunkedCopy_CarriesAlternateStreams_AndTheListSkipsTheDefaultStream()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams");
        var source = MakeSource(100);
        await File.WriteAllTextAsync(source + ":color", "green");
        await File.WriteAllTextAsync(source + ":files", "tag");
        var target = Path.Combine(_root, "t.bin");
        var streams = new WindowsAlternateStreams();

        var result = await new WindowsFileCopier().CopyAsync(new FileCopyRequest(source, target, TransferPath.ChunkedCopy, true, _ => { }), CancellationToken.None);

        Assert.Equal(["color", "files"], streams.Names(source).Order(StringComparer.Ordinal));
        Assert.Equal("green", await File.ReadAllTextAsync(target + ":color"));
        Assert.Equal("tag", await File.ReadAllTextAsync(target + ":files"));
        Assert.Empty(streams.Names(Path.Combine(_root, "missing.bin")));
        Assert.NotNull(result.SourceHash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4097)]
    [InlineData(3_000_001)]
    public async Task UnbufferedCopy_OnARealDisk_IsByteExact(int length)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "unbuffered I/O");
        var source = MakeSource(length);
        var target = Path.Combine(_root, "t.bin");
        var copier = new ChunkedFileCopier(new FileSystem(), unbufferedThreshold: 1);

        await copier.CopyAsync(new FileCopyRequest(source, target, TransferPath.ChunkedCopy, false, _ => { }), CancellationToken.None);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        Assert.False(File.Exists(target + CopyMoveExecutor.PartSuffix));
    }
}
