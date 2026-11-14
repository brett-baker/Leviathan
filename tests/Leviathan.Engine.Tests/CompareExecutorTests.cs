using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Leviathan.Contracts;
using Leviathan.Core.Compare;
using Leviathan.Engine.Compare;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Tests;

/// <summary>The compare job and how its results travel (spec §15.5).</summary>
public sealed class CompareExecutorTests
{
    private static readonly DateTime T = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MockFileData File(string text, DateTime? modified = null) => new(Encoding.ASCII.GetBytes(text)) { LastWriteTime = modified ?? T };

    private static async Task<(JobSnapshot Snapshot, JobResult Result, JobReport? Report)> RunAsync(MockFileSystem disk, JobRequest request)
    {
        await using var host = new JobHost(new JobScheduler(), [new CompareExecutor(disk)], new InMemoryJobJournal());
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

    private static MockFileSystem Disk()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"D:\Left\same.txt", File("abc"));
        disk.AddFile(@"E:\Right\same.txt", File("abc"));
        disk.AddFile(@"D:\Left\sub\only.txt", File("left"));
        disk.AddFile(@"E:\Right\new.txt", File("right"));
        disk.AddFile(@"D:\Left\edited.txt", File("aaaa", T.AddDays(1)));
        disk.AddFile(@"E:\Right\edited.txt", File("bbbb"));
        return disk;
    }

    private static JobRequest Compare(bool checksum = false) => new(JobKind.Compare, [@"D:\Left"], @"E:\Right") { CompareByChecksum = checksum };

    [Fact]
    public async Task TheJob_ReturnsEveryPath_WithItsComparison_AndASummaryNote()
    {
        var (snapshot, result, _) = await RunAsync(Disk(), Compare());

        Assert.Equal(JobState.Completed, snapshot.State);
        var entries = result.Items.Select(i => CompareCodec.Decode(i.Source, i.Result)!).ToDictionary(e => e.Path);
        Assert.Equal(CompareStatus.Identical, entries["same.txt"].Status);
        Assert.Equal(CompareStatus.LeftOnly, entries["sub"].Status);
        Assert.Equal(CompareStatus.LeftOnly, entries[@"sub\only.txt"].Status);
        Assert.Equal(CompareStatus.RightOnly, entries["new.txt"].Status);
        Assert.Equal(CompareStatus.NewerLeft, entries["edited.txt"].Status);
        Assert.All(result.Items, i => Assert.Equal(ItemStatus.Done, i.Status));
    }

    [Fact]
    public async Task ByChecksum_TheSameFileComesBackWithItsSums()
    {
        var disk = Disk();
        disk.AddFile(@"D:\Left\touched.txt", File("same", T.AddDays(2)));
        disk.AddFile(@"E:\Right\touched.txt", File("same"));

        var (_, result, _) = await RunAsync(disk, Compare(checksum: true));

        var touched = CompareCodec.Decode("touched.txt", result.Items.Single(i => i.Source == "touched.txt").Result)!;
        Assert.Equal(CompareStatus.Identical, touched.Status);
        Assert.NotNull(touched.LeftChecksum);
        Assert.Equal(touched.LeftChecksum, touched.RightChecksum);
    }

    [Fact]
    public async Task AFolderThatIsNotThere_FailsTheJobWithAReason()
    {
        var (snapshot, _, _) = await RunAsync(Disk(), new JobRequest(JobKind.Compare, [@"D:\Left"], @"E:\Nope"));

        Assert.Equal(JobState.Failed, snapshot.State);
        Assert.Contains("Nope", snapshot.Error);
    }

    [Fact]
    public void Validator_NeedsTwoFolders_ThatDoNotContainEachOther()
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compare, [@"D:\Left"], null)));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compare, [@"D:\Left", @"D:\Other"], @"E:\Right")));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compare, [@"D:\Left"], @"D:\Left")));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Compare, [@"D:\Left"], @"D:\Left\inside")));
        Assert.Null(JobRequestValidator.Validate(new JobRequest(JobKind.Compare, [@"D:\Left"], @"E:\Right")));
    }

    [Fact]
    public void Codec_RoundTrips_EveryShape()
    {
        var when = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        CompareEntry[] entries =
        [
            new("a", false, new CompareSide(5, when), new CompareSide(7, when.AddDays(1)), CompareStatus.NewerRight, "aa", "bb"),
            new("b", true, new CompareSide(0, when), null, CompareStatus.LeftOnly),
            new("c", false, null, new CompareSide(1, when), CompareStatus.RightOnly),
        ];

        foreach (var entry in entries)
        {
            Assert.Equal(entry, CompareCodec.Decode(entry.Path, CompareCodec.Encode(entry)));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1|f")]
    [InlineData("99|f|-|-|-|-")]
    [InlineData("x|f|-|-|-|-")]
    public void Codec_Decode_RefusesWhatIsNotAComparison(string? text)
    {
        Assert.Null(CompareCodec.Decode("p", text));
    }
}
