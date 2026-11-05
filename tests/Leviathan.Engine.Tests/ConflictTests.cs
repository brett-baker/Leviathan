using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

/// <summary>One question per job for every name clash (spec §12.4).</summary>
public sealed class ConflictTests
{
    private static readonly DateTime Old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MockFileData File(string text, DateTime modified) => new(text) { LastWriteTime = modified };

    /// <summary>Source folder C:\src with a.txt, b.txt and sub\c.txt; the destination already holds a.txt, b.txt and sub\c.txt.</summary>
    private static MockFileSystem Disk(string srcA = "new-a", DateTime? srcAModified = null, string dstA = "old-a", DateTime? dstAModified = null)
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"C:\dst");
        fs.AddFile(@"C:\src\a.txt", File(srcA, srcAModified ?? New));
        fs.AddFile(@"C:\src\b.txt", File("new-b", New));
        fs.AddFile(@"C:\src\sub\c.txt", File("new-c", New));
        fs.AddFile(@"C:\dst\a.txt", File(dstA, dstAModified ?? Old));
        fs.AddFile(@"C:\dst\b.txt", File("old-b", Old));
        fs.AddFile(@"C:\dst\sub\c.txt", File("old-c", Old));
        return fs;
    }

    private static TransferPlan Plan(MockFileSystem fs, ConflictPolicy policy, params string[] sources) =>
        new TransferPlanner(fs).Plan(Guid.NewGuid(), new JobRequest(JobKind.Copy, sources, @"C:\dst") { Conflict = policy }, CancellationToken.None);

    private static string Text(MockFileSystem fs, string path) => fs.File.ReadAllText(path);

    [Fact]
    public void Plan_Ask_FlagsEveryFileThatWouldBeOverwritten_AndKeepsTheExistingName()
    {
        var plan = Plan(Disk(), ConflictPolicy.Ask, @"C:\src\a.txt", @"C:\src\b.txt");

        var clashes = plan.Clashes.ToList();
        Assert.Equal(2, clashes.Count);
        Assert.Equal(@"C:\dst\a.txt", clashes[0].Target);
    }

    [Fact]
    public void Plan_Ask_FlagsFilesInsideAMergedFolder_ButNotTheFolderItself()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\sub\only-new.txt", File("x", New));

        var plan = Plan(fs, ConflictPolicy.Ask, @"C:\src\sub");

        var clash = Assert.Single(plan.Clashes);
        Assert.Equal(@"C:\dst\sub\c.txt", clash.Target);
        Assert.Equal(2, plan.Items[0].Steps.Count(s => s.Op == PlanOp.CopyFile));
    }

    [Fact]
    public void Plan_WithoutClashes_FlagsNothing()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\fresh.txt", File("x", New));

        Assert.Empty(Plan(fs, ConflictPolicy.Ask, @"C:\src\fresh.txt").Clashes);
    }

    [Fact]
    public void DescribeClashes_GivesBothSizesAndTimes()
    {
        var fs = Disk();
        var planner = new TransferPlanner(fs);
        var plan = Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt");

        var item = Assert.Single(planner.DescribeClashes(plan));

        Assert.Equal((5L, 5L), (item.SourceSize, item.TargetSize));
        Assert.True(item.SourceModified > item.TargetModified);
        Assert.Equal(@"C:\dst\a.txt", item.Target);
    }

    [Fact]
    public void Resolve_Replace_KeepsTheStepWithoutTheFlag()
    {
        var fs = Disk();
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(ConflictPolicy.Replace), CancellationToken.None);

        Assert.Empty(plan.Clashes);
        var step = Assert.Single(plan.Items[0].Steps);
        Assert.Equal(@"C:\dst\a.txt", step.Target);
        Assert.Equal(ItemStatus.Done, plan.Items[0].Status);
    }

    [Fact]
    public void Resolve_Skip_MarksTheItemSkipped_AndDropsItsBytes()
    {
        var fs = Disk();
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(ConflictPolicy.Skip), CancellationToken.None);

        Assert.Equal(ItemStatus.Skipped, plan.Items[0].Status);
        Assert.Equal(0, plan.BytesTotal);
    }

    [Fact]
    public void Resolve_KeepBoth_GivesTheNewFileAFreeName()
    {
        var fs = Disk();
        fs.AddFile(@"C:\dst\a (2).txt", File("taken", Old));
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(ConflictPolicy.KeepBoth), CancellationToken.None);

        Assert.Equal(@"C:\dst\a (3).txt", Assert.Single(plan.Items[0].Steps).Target);
    }

    [Fact]
    public void Resolve_PerFileOverride_BeatsTheRuleForAll()
    {
        var fs = Disk();
        var planner = new TransferPlanner(fs);
        var answer = new ConflictAnswer(ConflictPolicy.Skip, new Dictionary<string, ConflictPolicy> { [@"C:\dst\b.txt"] = ConflictPolicy.Replace });

        var plan = planner.ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt", @"C:\src\b.txt"), answer, CancellationToken.None);

        Assert.Equal(ItemStatus.Skipped, plan.Items[0].Status);
        Assert.Equal(ItemStatus.Done, plan.Items[1].Status);
    }

    [Fact]
    public void Resolve_Folder_SkipsOnlyTheFilesThatClash_AndStillCopiesTheRest()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\sub\only-new.txt", File("x", New));
        var planner = new TransferPlanner(fs);

        var plan = planner.ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\sub"), new ConflictAnswer(ConflictPolicy.Skip), CancellationToken.None);

        var files = plan.Items[0].Steps.Where(s => s.Op == PlanOp.CopyFile).ToList();
        Assert.Equal(ItemStatus.Done, plan.Items[0].Status);
        Assert.Equal(@"C:\dst\sub\only-new.txt", Assert.Single(files).Target);
    }

    [Theory]
    [InlineData(ConflictPolicy.ReplaceIfNewer, true)]
    [InlineData(ConflictPolicy.ReplaceIfLarger, false)]
    public void Resolve_Conditions_CompareTimeAndSize(ConflictPolicy rule, bool replaced)
    {
        // The new a.txt is newer than the old one but the same size.
        var fs = Disk();
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(rule), CancellationToken.None);

        Assert.Equal(replaced ? ItemStatus.Done : ItemStatus.Skipped, plan.Items[0].Status);
    }

    [Fact]
    public void Resolve_IfLarger_ReplacesWhenTheNewFileIsBigger()
    {
        var fs = Disk(srcA: "much longer new content");
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(ConflictPolicy.ReplaceIfLarger), CancellationToken.None);

        Assert.Equal(ItemStatus.Done, plan.Items[0].Status);
    }

    [Fact]
    public void Resolve_IfNewer_KeepsTheExistingFileWhenItIsNewer()
    {
        var fs = Disk(srcAModified: Old, dstAModified: New);
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(ConflictPolicy.ReplaceIfNewer), CancellationToken.None);

        Assert.Equal(ItemStatus.Skipped, plan.Items[0].Status);
    }

    [Theory]
    [InlineData("same!", "same!", false)]
    [InlineData("same!", "diff!", true)]
    [InlineData("short", "longer text", true)]
    public void Resolve_IfDifferent_ComparesTheContents(string source, string existing, bool replaced)
    {
        var fs = Disk(srcA: source, dstA: existing);
        var plan = new TransferPlanner(fs).ResolveClashes(Plan(fs, ConflictPolicy.Ask, @"C:\src\a.txt"), new ConflictAnswer(ConflictPolicy.ReplaceIfDifferent), CancellationToken.None);

        Assert.Equal(replaced ? ItemStatus.Done : ItemStatus.Skipped, plan.Items[0].Status);
    }

    private static JobHost Start(MockFileSystem fs)
    {
        var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs)], new InMemoryJobJournal());
        host.Start();
        return host;
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return false;
    }

    private static JobConflicts? Asked(JobHost host, Guid id) => host.Snapshot(host.Scheduler.Find(id)!).Conflicts;

    [Fact]
    public async Task Job_Ask_WaitsForOneAnswer_ThenAppliesItToEveryClash()
    {
        var fs = Disk();
        await using var host = Start(fs);
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\a.txt", @"C:\src\b.txt", @"C:\src\sub"], @"C:\dst") { Conflict = ConflictPolicy.Ask }, JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => Asked(host, job.Id) is not null));
        var question = Asked(host, job.Id)!;
        Assert.Equal(3, question.TotalCount);
        Assert.Equal("old-a", Text(fs, @"C:\dst\a.txt"));
        Assert.False(host.AnswerConflicts(job.Id, Guid.NewGuid(), new ConflictAnswer(ConflictPolicy.Replace)));
        Assert.False(host.AnswerConflicts(job.Id, question.Id, new ConflictAnswer(ConflictPolicy.Ask)));
        Assert.True(host.AnswerConflicts(job.Id, question.Id, new ConflictAnswer(ConflictPolicy.Replace, new Dictionary<string, ConflictPolicy> { [@"C:\dst\b.txt"] = ConflictPolicy.Skip })));
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.Equal("new-a", Text(fs, @"C:\dst\a.txt"));
        Assert.Equal("old-b", Text(fs, @"C:\dst\b.txt"));
        Assert.Equal("new-c", Text(fs, @"C:\dst\sub\c.txt"));
        Assert.Null(Asked(host, job.Id));
    }

    [Fact]
    public async Task Job_Ask_KeepBoth_LeavesTheOldFileAndAddsTheNewOneUnderANewName()
    {
        var fs = Disk();
        await using var host = Start(fs);
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\a.txt"], @"C:\dst") { Conflict = ConflictPolicy.Ask }, JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => Asked(host, job.Id) is not null));
        host.AnswerConflicts(job.Id, Asked(host, job.Id)!.Id, new ConflictAnswer(ConflictPolicy.KeepBoth));
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.Equal("old-a", Text(fs, @"C:\dst\a.txt"));
        Assert.Equal("new-a", Text(fs, @"C:\dst\a (2).txt"));
    }

    [Fact]
    public async Task Job_Ask_WithoutClashes_NeverAsks()
    {
        var fs = Disk();
        fs.AddFile(@"C:\src\fresh.txt", File("x", New));
        await using var host = Start(fs);
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\fresh.txt"], @"C:\dst") { Conflict = ConflictPolicy.Ask }, JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));
        Assert.Equal("x", Text(fs, @"C:\dst\fresh.txt"));
    }

    [Fact]
    public async Task Job_Ask_Skip_ReportsTheFileAsSkipped_AndTheJobStillCompletes()
    {
        var fs = Disk();
        await using var host = Start(fs);
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\a.txt"], @"C:\dst") { Conflict = ConflictPolicy.Ask }, JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => Asked(host, job.Id) is not null));
        host.AnswerConflicts(job.Id, Asked(host, job.Id)!.Id, new ConflictAnswer(ConflictPolicy.Skip));
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.Equal("old-a", Text(fs, @"C:\dst\a.txt"));
    }

    [Fact]
    public async Task Job_IfNewerGivenUpFront_NeedsNoQuestion()
    {
        var fs = Disk(srcAModified: Old, dstAModified: New);
        await using var host = Start(fs);
        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\a.txt", @"C:\src\b.txt"], @"C:\dst") { Conflict = ConflictPolicy.ReplaceIfNewer }, JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.Equal("old-a", Text(fs, @"C:\dst\a.txt"));
        Assert.Equal("new-b", Text(fs, @"C:\dst\b.txt"));
    }

    [Fact]
    public async Task Job_Move_Skip_KeepsTheSourceFile()
    {
        var fs = Disk();
        await using var host = Start(fs);
        var job = host.Submit(new JobRequest(JobKind.Move, [@"C:\src\a.txt"], @"C:\dst") { Conflict = ConflictPolicy.Ask }, JobLane.Operations, null, "move");

        Assert.True(await UntilAsync(() => Asked(host, job.Id) is not null));
        host.AnswerConflicts(job.Id, Asked(host, job.Id)!.Id, new ConflictAnswer(ConflictPolicy.Skip));
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.True(fs.File.Exists(@"C:\src\a.txt"));
        Assert.Equal("old-a", Text(fs, @"C:\dst\a.txt"));
    }
}
