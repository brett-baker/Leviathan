using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Leviathan.Contracts;
using Leviathan.Core.Hashing;
using Leviathan.Engine.Checksums;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Tests;

/// <summary>The checksum job: computing sums and verifying checksum files (spec §15.4).</summary>
public sealed class ChecksumExecutorTests
{
    private static async Task<(JobSnapshot Snapshot, JobResult Result, JobReport? Report)> RunAsync(MockFileSystem disk, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [new ChecksumExecutor(disk)], new InMemoryJobJournal());
        host.Start();
        var job = host.Submit(request, JobLane.Background, null, "test");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!host.Scheduler.Find(job.Id)!.IsFinished)
        {
            Assert.True(DateTime.UtcNow < deadline, "the job did not finish");
            await Task.Delay(5);
        }

        return (host.Snapshot(host.Scheduler.Find(job.Id)!), host.GetResult(job.Id)!, host.GetReport(job.Id));
    }

    private static MockFileData Text(string text) => new(Encoding.ASCII.GetBytes(text));

    private static MockFileSystem Disk()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"D:\Data\abc.txt", Text("abc"));
        disk.AddFile(@"D:\Data\sub\empty.bin", Text(""));
        disk.AddFile(@"D:\Data\sub\deep\nine.txt", Text("123456789"));
        return disk;
    }

    private static JobRequest Compute(string[] sources, params ChecksumKind[] kinds) =>
        new(JobKind.Checksum, sources, null) { ChecksumKinds = [.. kinds.Select(k => k.ToString())] };

    [Fact]
    public async Task Compute_OneFile_GivesEveryRequestedSum()
    {
        var (snapshot, result, _) = await RunAsync(Disk(), Compute([@"D:\Data\abc.txt"], ChecksumKind.Md5, ChecksumKind.Sha256, ChecksumKind.Crc32));

        Assert.Equal(JobState.Completed, snapshot.State);
        var item = Assert.Single(result.Items);
        var sums = ChecksumExecutor.Decode(item.Result);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", sums[ChecksumKind.Md5]);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", sums[ChecksumKind.Sha256]);
        Assert.Equal("352441c2", sums[ChecksumKind.Crc32]);
    }

    [Fact]
    public async Task Compute_AFolder_ReadsEveryFileBelowIt_InOrder_AndReportsBytes()
    {
        var (snapshot, result, _) = await RunAsync(Disk(), Compute([@"D:\Data"], ChecksumKind.Crc32));

        Assert.Equal([@"D:\Data\abc.txt", @"D:\Data\sub\deep\nine.txt", @"D:\Data\sub\empty.bin"], result.Items.Select(i => i.Source));
        Assert.Equal("cbf43926", ChecksumExecutor.Decode(result.Items[1].Result)[ChecksumKind.Crc32]);
        Assert.Equal(12, snapshot.BytesTotal);
        Assert.Equal(12, snapshot.BytesDone);
        Assert.Equal(3, snapshot.ItemsDone);
    }

    [Fact]
    public async Task Compute_AMissingSource_IsReported_TheRestStillRun()
    {
        var (_, result, _) = await RunAsync(Disk(), Compute([@"D:\Data\gone.txt", @"D:\Data\abc.txt"], ChecksumKind.Md5));

        Assert.Contains(result.Items, i => i.Status == ItemStatus.Failed && i.Source.EndsWith("gone.txt", StringComparison.Ordinal));
        Assert.Contains(result.Items, i => i.Status == ItemStatus.Done && i.Source.EndsWith("abc.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compute_ANetworkSourceThatIsNotAShare_IsSkipped()
    {
        var (_, result, _) = await RunAsync(Disk(), Compute(["sftp://host/x/a.bin"], ChecksumKind.Md5));

        Assert.Equal(ItemStatus.Skipped, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task Verify_AFileThatMatches_AndOneThatDoesNot_AndOneThatIsMissing()
    {
        var disk = Disk();
        disk.AddFile(@"D:\Data\all.sha256", Text(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  abc.txt\n" +
            "0000000000000000000000000000000000000000000000000000000000000000  sub/deep/nine.txt\n" +
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  gone.txt\n"));

        var (snapshot, result, report) = await RunAsync(disk, new JobRequest(JobKind.Checksum, [@"D:\Data\all.sha256"], null) { ChecksumVerify = true });

        Assert.Equal(JobState.Completed, snapshot.State);
        var ok = result.Items.Single(i => i.Source.EndsWith("abc.txt", StringComparison.Ordinal));
        var bad = result.Items.Single(i => i.Source.EndsWith("nine.txt", StringComparison.Ordinal));
        var gone = result.Items.Single(i => i.Source.EndsWith("gone.txt", StringComparison.Ordinal));
        Assert.Equal(ItemStatus.Done, ok.Status);
        Assert.Equal(ItemStatus.Failed, bad.Status);
        Assert.StartsWith(ChecksumExecutor.Mismatch, bad.Error, StringComparison.Ordinal);
        Assert.Contains("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", bad.Error);
        Assert.Equal(ItemStatus.Failed, gone.Status);
        Assert.StartsWith(ChecksumExecutor.Missing, gone.Error, StringComparison.Ordinal);
        Assert.Null(report);
    }

    [Fact]
    public async Task Verify_ALoneSum_BelongsToTheFileItIsNamedAfter()
    {
        var disk = Disk();
        disk.AddFile(@"D:\Data\abc.txt.md5", Text("900150983CD24FB0D6963F7D28E17F72\n"));

        var (_, result, _) = await RunAsync(disk, new JobRequest(JobKind.Checksum, [@"D:\Data\abc.txt.md5"], null) { ChecksumVerify = true });

        var item = Assert.Single(result.Items);
        Assert.Equal(@"D:\Data\abc.txt", item.Source);
        Assert.Equal(ItemStatus.Done, item.Status);
    }

    [Fact]
    public async Task Verify_MixedKindsInOneFile_EachCheckedWithItsOwnKind()
    {
        var disk = Disk();
        disk.AddFile(@"D:\Data\mix.txt", Text("900150983cd24fb0d6963f7d28e17f72  abc.txt\ncbf43926  sub/deep/nine.txt\n"));

        var (_, result, _) = await RunAsync(disk, new JobRequest(JobKind.Checksum, [@"D:\Data\mix.txt"], null) { ChecksumVerify = true });

        Assert.All(result.Items, i => Assert.Equal(ItemStatus.Done, i.Status));
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task Verify_AChecksumFileWithNothingReadable_FailsThatSource()
    {
        var disk = Disk();
        disk.AddFile(@"D:\Data\notes.sha256", Text("this is prose, not sums\n"));

        var (_, result, _) = await RunAsync(disk, new JobRequest(JobKind.Checksum, [@"D:\Data\notes.sha256"], null) { ChecksumVerify = true });

        Assert.Equal(ItemStatus.Failed, Assert.Single(result.Items).Status);
    }

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var sums = new Dictionary<ChecksumKind, string> { [ChecksumKind.Sha256] = "aa", [ChecksumKind.Md5] = "bb" };

        Assert.Equal(sums, ChecksumExecutor.Decode(ChecksumExecutor.Encode(sums)));
        Assert.Empty(ChecksumExecutor.Decode(null));
        Assert.Empty(ChecksumExecutor.Decode("junk;Nope=1"));
    }

    [Fact]
    public void Validator_NeedsAKind_ExceptWhenVerifying()
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Checksum, [@"D:\a.txt"], null)));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Checksum, [@"D:\a.txt"], null) { ChecksumKinds = ["Nonsense"] }));
        Assert.Null(JobRequestValidator.Validate(Compute([@"D:\a.txt"], ChecksumKind.Sha1)));
        Assert.Null(JobRequestValidator.Validate(new JobRequest(JobKind.Checksum, [@"D:\a.sha256"], null) { ChecksumVerify = true }));
    }
}
