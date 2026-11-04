using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Hashing;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class VerificationTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static byte[] Data(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // ---------- the policy ----------

    [Theory]
    [InlineData(VerifyMode.Default, true, false, 1, VerifyMethod.Full)]
    [InlineData(VerifyMode.Default, true, false, 4 * Gb, VerifyMethod.Full)]
    [InlineData(VerifyMode.Default, true, false, (4 * Gb) + 1, VerifyMethod.Sampled)]
    [InlineData(VerifyMode.Default, false, true, 100, VerifyMethod.Full)]
    [InlineData(VerifyMode.Default, false, true, 50 * Gb, VerifyMethod.Sampled)]
    [InlineData(VerifyMode.Default, false, false, 100, VerifyMethod.None)]
    [InlineData(VerifyMode.Off, false, true, 100, VerifyMethod.None)]
    [InlineData(VerifyMode.Off, true, false, 100, VerifyMethod.Full)]
    [InlineData(VerifyMode.Off, true, true, 50 * Gb, VerifyMethod.Sampled)]
    [InlineData(VerifyMode.Full, false, false, 100, VerifyMethod.Full)]
    [InlineData(VerifyMode.Full, true, true, 50 * Gb, VerifyMethod.Full)]
    public void Decide_FollowsThePolicy(VerifyMode mode, bool move, bool network, long length, VerifyMethod expected) =>
        Assert.Equal(expected, new VerifyPolicy().Decide(mode, move, network, length));

    // ---------- the verifier ----------

    private static (MockFileSystem Fs, byte[] Original) Pair(int length)
    {
        var original = Data(length);
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\src\f.bin", new MockFileData(original));
        fs.AddFile(@"C:\dst\f.bin", new MockFileData(original));
        return (fs, original);
    }

    private static void FlipByte(MockFileSystem fs, long offset)
    {
        var bytes = fs.File.ReadAllBytes(@"C:\dst\f.bin");
        bytes[offset] ^= 0xFF;
        fs.File.WriteAllBytes(@"C:\dst\f.bin", bytes);
    }

    [Fact]
    public async Task Full_AcceptsAnIdenticalCopy_AndRejectsOneBadByte()
    {
        var (fs, _) = Pair(50_000);
        var verifier = new HashFileVerifier(fs);

        Assert.True(await verifier.VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.Full, null, CancellationToken.None));
        FlipByte(fs, 31_234);
        Assert.False(await verifier.VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.Full, null, CancellationToken.None));
    }

    [Fact]
    public async Task Full_WithTheHashFromTheCopy_ReadsOnlyTheTarget()
    {
        var (fs, original) = Pair(50_000);
        var hash = XxHash3.Hash(original);
        fs.File.Delete(@"C:\src\f.bin");
        fs.AddFile(@"C:\src\f.bin", new MockFileData(new byte[original.Length]));

        // The source on disk no longer matches, but the hash made while copying is what counts for a full check.
        Assert.True(await new HashFileVerifier(fs).VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.Full, hash, CancellationToken.None));
        FlipByte(fs, 10);
        Assert.False(await new HashFileVerifier(fs).VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.Full, hash, CancellationToken.None));
    }

    [Fact]
    public async Task ADifferentSize_AlwaysFails_EvenWhenNothingIsHashed()
    {
        var (fs, _) = Pair(1000);
        fs.File.AppendAllText(@"C:\dst\f.bin", "x");

        Assert.False(await new HashFileVerifier(fs).VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.None, null, CancellationToken.None));
    }

    [Fact]
    public async Task None_AcceptsTheSameSize()
    {
        var (fs, _) = Pair(1000);
        FlipByte(fs, 5);

        Assert.True(await new HashFileVerifier(fs).VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.None, null, CancellationToken.None));
    }

    private static readonly VerifyPolicy Small = new(FullLimit: 100, EdgeBytes: 100, RandomBlocks: 3, BlockBytes: 50);

    [Fact]
    public void SampleRegions_AreTheEdgesAndRandomBlocks_StayInsideTheFile_AndRepeatForTheSameFile()
    {
        var verifier = new HashFileVerifier(new MockFileSystem(), Small);

        var regions = verifier.SampleRegions(10_000, "f.bin");

        Assert.Equal((0, 100), regions[0]);
        Assert.Equal((9_900, 100), regions[1]);
        Assert.Equal(5, regions.Count);
        Assert.All(regions, r => Assert.InRange(r.Offset + r.Count, 1, 10_000));
        Assert.Equal(regions, verifier.SampleRegions(10_000, "f.bin"));
        Assert.NotEqual(regions, verifier.SampleRegions(10_000, "other.bin"));
    }

    [Fact]
    public void SampleRegions_OfTinyAndEmptyFiles_AreSafe()
    {
        var verifier = new HashFileVerifier(new MockFileSystem(), Small);

        Assert.Empty(verifier.SampleRegions(0, "x"));
        var tiny = verifier.SampleRegions(10, "x");
        Assert.All(tiny, r => Assert.InRange(r.Offset + r.Count, 1, 10));
    }

    [Fact]
    public async Task Sampled_CatchesDamageAtBothEnds_AndInASampledBlock_ButNotInTheGaps()
    {
        var verifier = new HashFileVerifier(new MockFileSystem(), Small);
        var regions = verifier.SampleRegions(10_000, @"C:\dst\f.bin");
        var gap = Enumerable.Range(0, 10_000).First(o => regions.All(r => o < r.Offset || o >= r.Offset + r.Count));

        foreach (var (offset, why) in new[] { (5L, "start"), (9_950L, "end"), (regions[2].Offset + 3, "random block"), (gap, "gap") })
        {
            var (fs, _) = Pair(10_000);
            FlipByte(fs, offset);
            var ok = await new HashFileVerifier(fs, Small).VerifyAsync(@"C:\src\f.bin", @"C:\dst\f.bin", VerifyMethod.Sampled, null, CancellationToken.None);

            Assert.Equal(why == "gap", ok);
        }
    }

    // ---------- the executor ----------

    private sealed class CorruptingCopier(IFileSystem fs) : IFileCopier
    {
        private readonly ChunkedFileCopier _real = new(fs);

        public async Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken)
        {
            // The source hash is taken from what was read, so damage done after the read (a bad write) is what verification must find.
            var result = await _real.CopyAsync(request, cancellationToken);
            var bytes = fs.File.ReadAllBytes(request.Target);
            bytes[bytes.Length / 2] ^= 0xFF;
            fs.File.WriteAllBytes(request.Target, bytes);
            return result;
        }
    }

    private static MockFileSystem Disk()
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDrive("D:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"C:\dst");
        fs.AddDirectory(@"D:\other");
        fs.AddFile(@"C:\src\f.bin", new MockFileData(Data(20_000)));
        return fs;
    }

    private static async Task<(JobState State, IReadOnlyList<ItemOutcome> Items)> RunAsync(MockFileSystem fs, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, copier: new CorruptingCopier(fs))], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Operations, null, "t");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        return (host.Scheduler.Find(job.Id)!.State, host.GetResult(job.Id)!.Items);
    }

    [Fact]
    public async Task Move_ToAnotherDrive_WithABadCopy_KeepsTheOriginal_AndRemovesTheCopy()
    {
        var fs = Disk();

        var (state, items) = await RunAsync(fs, new JobRequest(JobKind.Move, [@"C:\src\f.bin"], @"D:\other") { OnError = ErrorMode.SkipAndReport });

        Assert.Equal(JobState.Failed, state);
        Assert.Equal(ItemStatus.Failed, Assert.Single(items).Status);
        Assert.Contains("original was kept", items[0].Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"C:\src\f.bin"));
        Assert.False(fs.File.Exists(@"D:\other\f.bin"));
    }

    [Fact]
    public async Task Move_WithVerifyOff_IsStillChecked_BecauseTheSourceIsDeleted()
    {
        var fs = Disk();

        var (state, _) = await RunAsync(fs, new JobRequest(JobKind.Move, [@"C:\src\f.bin"], @"D:\other") { Verify = VerifyMode.Off, OnError = ErrorMode.SkipAndReport });

        Assert.Equal(JobState.Failed, state);
        Assert.True(fs.File.Exists(@"C:\src\f.bin"));
    }

    [Fact]
    public async Task LocalCopy_IsNotCheckedByDefault_ButFullChecksIt()
    {
        var plain = Disk();
        var full = Disk();

        var (defaultState, _) = await RunAsync(plain, new JobRequest(JobKind.Copy, [@"C:\src\f.bin"], @"D:\other"));
        var (fullState, items) = await RunAsync(full, new JobRequest(JobKind.Copy, [@"C:\src\f.bin"], @"D:\other") { Verify = VerifyMode.Full, OnError = ErrorMode.SkipAndReport });

        Assert.Equal(JobState.Completed, defaultState);
        Assert.Equal(JobState.Failed, fullState);
        Assert.Contains("copy was removed", items[0].Error, StringComparison.Ordinal);
        Assert.False(full.File.Exists(@"D:\other\f.bin"));
        Assert.True(full.File.Exists(@"C:\src\f.bin"));
    }

    [Fact]
    public async Task GoodCopies_PassTheCheck_AndAMoveCompletes()
    {
        var fs = Disk();
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs)], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Move, [@"C:\src\f.bin"], @"D:\other"), JobLane.Operations, null, "t");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Scheduler.Find(job.Id)!.IsFinished && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.False(fs.File.Exists(@"C:\src\f.bin"));
        Assert.Equal(20_000, fs.FileInfo.New(@"D:\other\f.bin").Length);
    }
}
