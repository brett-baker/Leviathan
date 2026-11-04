using System.IO.Abstractions.TestingHelpers;
using System.Security.Cryptography;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class ChunkedFileCopierTests
{
    private sealed class FakeStreams(Dictionary<string, string[]> names, bool failCopies = false) : IAlternateStreams
    {
        public List<string> Copied { get; } = [];

        public IReadOnlyList<string> Names(string path) => names.TryGetValue(path, out var n) ? n : [];

        public Task CopyAsync(string source, string target, string name, CancellationToken cancellationToken)
        {
            if (failCopies)
            {
                throw new IOException("no streams here");
            }

            Copied.Add($"{target}:{name}");
            return Task.CompletedTask;
        }
    }

    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31) + (i >> 8));
        }

        return data;
    }

    private static MockFileSystem Disk(int length)
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\dst");
        fs.AddFile(@"C:\src\f.bin", new MockFileData(Pattern(length)) { LastWriteTime = Stamp, CreationTime = Stamp.AddDays(-1) });
        return fs;
    }

    private static FileCopyRequest Request(bool hash = false, Action<long>? onBytes = null) =>
        new(@"C:\src\f.bin", @"C:\dst\f.bin", TransferPath.ChunkedCopy, hash, onBytes ?? (_ => { }));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    [InlineData(1_048_576)]
    [InlineData(2_500_001)]
    public async Task Copy_IsByteExact_AndLeavesNoPartFile(int length)
    {
        var fs = Disk(length);
        var copier = new ChunkedFileCopier(fs);

        var result = await copier.CopyAsync(Request(), CancellationToken.None);

        Assert.Equal(length, result.Length);
        Assert.Equal(Pattern(length), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
        Assert.False(fs.File.Exists(@"C:\dst\f.bin" + CopyMoveExecutor.PartSuffix));
    }

    [Fact]
    public async Task Copy_KeepsTimestampsAndAttributes()
    {
        var fs = Disk(10);
        fs.File.SetAttributes(@"C:\src\f.bin", FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden);

        await new ChunkedFileCopier(fs).CopyAsync(Request(), CancellationToken.None);

        Assert.Equal(Stamp, fs.File.GetLastWriteTimeUtc(@"C:\dst\f.bin"));
        Assert.Equal(Stamp.AddDays(-1), fs.File.GetCreationTimeUtc(@"C:\dst\f.bin"));
        var attributes = fs.File.GetAttributes(@"C:\dst\f.bin");
        Assert.True(attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task Copy_ReportsEveryByte_AndTheSourceHash()
    {
        var fs = Disk(3_000_000);
        long total = 0;

        var result = await new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(100_000)).CopyAsync(Request(hash: true, onBytes: n => total += n), CancellationToken.None);

        Assert.Equal(3_000_000, total);
        Assert.Equal(SHA256.HashData(Pattern(3_000_000)), result.SourceHash);
    }

    [Fact]
    public async Task Copy_WithoutAskingForAHash_ReturnsNone()
    {
        var result = await new ChunkedFileCopier(Disk(10)).CopyAsync(Request(), CancellationToken.None);

        Assert.Null(result.SourceHash);
    }

    [Fact]
    public async Task Copy_WhenCancelled_NeverLeavesAFinishedLookingTarget()
    {
        var fs = Disk(2_000_000);
        using var cts = new CancellationTokenSource();
        var copier = new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(100_000));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            copier.CopyAsync(Request(onBytes: n => cts.Cancel()), cts.Token));

        // The part file stays so a resume can continue it (spec 12.1 step 4); only the finished name must never appear.
        Assert.False(fs.File.Exists(@"C:\dst\f.bin"));
    }

    [Fact]
    public async Task Copy_ThatFailsForARealReason_RemovesItsPartFile()
    {
        var fs = Disk(1000);
        fs.AddFile(@"C:\dst\f.bin", new MockFileData("x") { Attributes = FileAttributes.Directory });
        var copier = new ChunkedFileCopier(fs, streams: new FakeStreams(new()));
        fs.File.Delete(@"C:\src\f.bin");

        await Assert.ThrowsAnyAsync<Exception>(() => copier.CopyAsync(Request(), CancellationToken.None));

        Assert.False(fs.File.Exists(@"C:\dst\f.bin" + CopyMoveExecutor.PartSuffix));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(1_048_577)]
    public async Task Copy_OfABigFileWithUnbufferedIo_IsByteExact_EvenWhenNotSectorSized(int length)
    {
        var fs = Disk(length);
        var copier = new ChunkedFileCopier(fs, () => BufferPolicy.Fixed(10_000), unbufferedThreshold: 1);

        var result = await copier.CopyAsync(Request(hash: true), CancellationToken.None);

        Assert.Equal(length, fs.FileInfo.New(@"C:\dst\f.bin").Length);
        Assert.Equal(Pattern(length), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
        Assert.Equal(SHA256.HashData(Pattern(length)), result.SourceHash);
    }

    [Fact]
    public async Task Copy_CarriesAlternateStreams_ToThePartFileBeforeTheRename()
    {
        var fs = Disk(10);
        var streams = new FakeStreams(new() { [@"C:\src\f.bin"] = ["color", "Zone.Identifier"] });

        var result = await new ChunkedFileCopier(fs, streams: streams).CopyAsync(Request(), CancellationToken.None);

        Assert.Equal([@"C:\dst\f.bin.leviathan-part:color", @"C:\dst\f.bin.leviathan-part:Zone.Identifier"], streams.Copied);
        Assert.Equal(0, result.StreamsNotCopied);
    }

    [Fact]
    public async Task Copy_WhenTheDestinationTakesNoStreams_KeepsTheDataAndCountsTheMisses()
    {
        var fs = Disk(10);
        var streams = new FakeStreams(new() { [@"C:\src\f.bin"] = ["color", "other"] }, failCopies: true);

        var result = await new ChunkedFileCopier(fs, streams: streams).CopyAsync(Request(), CancellationToken.None);

        Assert.Equal(2, result.StreamsNotCopied);
        Assert.Equal(Pattern(10), fs.File.ReadAllBytes(@"C:\dst\f.bin"));
    }

    [Fact]
    public async Task Copy_MeasuresThroughput_AndTheNextFileStartsFromIt()
    {
        var memory = new ThroughputMemory();
        var fs = Disk(3_000_000);
        var first = BufferPolicy.Fixed(1_000_000);

        await new ChunkedFileCopier(fs, () => first, memory: memory).CopyAsync(Request(), CancellationToken.None);
        memory.Set(@"C:\", 280_000_000);
        BufferPolicy? seen = null;
        await new ChunkedFileCopier(fs, () => seen = new BufferPolicy(), memory: memory).CopyAsync(Request(), CancellationToken.None);

        Assert.NotNull(seen);
        Assert.True(seen.BytesPerSecond > 0);
    }

    [Theory]
    [InlineData(0, 1 * 1024 * 1024)]
    [InlineData(30_000_000, 1 * 1024 * 1024)]
    [InlineData(112_000_000, 2 * 1024 * 1024)]
    [InlineData(280_000_000, 8 * 1024 * 1024)]
    [InlineData(2_000_000_000, 8 * 1024 * 1024)]
    public void BufferPolicy_GrowsWithThroughput_Between1And8Megabytes(double bytesPerSecond, int expected) =>
        Assert.Equal(expected, new BufferPolicy(bytesPerSecond).Size);

    [Fact]
    public void BufferPolicy_Observe_SmoothsTheRate_AndIgnoresNonsense()
    {
        var policy = new BufferPolicy();

        policy.Observe(100_000_000, TimeSpan.FromSeconds(1));
        policy.Observe(200_000_000, TimeSpan.FromSeconds(1));
        policy.Observe(0, TimeSpan.FromSeconds(1));
        policy.Observe(10, TimeSpan.Zero);

        Assert.Equal(120_000_000, policy.BytesPerSecond, 1);
        BufferPolicy.Fixed(10).Observe(1, TimeSpan.FromSeconds(1));
        Assert.Equal(10, BufferPolicy.Fixed(10).Size);
    }

    [Fact]
    public async Task NoAlternateStreams_HasNothing()
    {
        var none = new NoAlternateStreams();

        Assert.Empty(none.Names(@"C:\x"));
        await none.CopyAsync("a", "b", "c", CancellationToken.None);
    }
}
