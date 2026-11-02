using Leviathan.Contracts;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;

namespace Leviathan.Engine.Tests;

public sealed class JobRequestValidatorTests
{
    [Theory]
    [InlineData(JobKind.Copy)]
    [InlineData(JobKind.Move)]
    [InlineData(JobKind.Link)]
    public void CopyMoveLink_NeedADestination(JobKind kind)
    {
        Assert.Contains("destination", JobRequestValidator.Validate(new JobRequest(kind, [@"C:\a\1.txt"], null)), StringComparison.Ordinal);
        Assert.Null(JobRequestValidator.Validate(new JobRequest(kind, [@"C:\a\1.txt"], @"D:\b")));
    }

    [Fact]
    public void NoSources_IsRefused()
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Delete, [], null)));
    }

    [Fact]
    public void AnInvalidSourceLocation_IsRefused()
    {
        Assert.Contains("not a valid location", JobRequestValidator.Validate(new JobRequest(JobKind.Delete, ["not a path"], null)), StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderIntoItselfOrBelow_IsRefused_ForCopyAndMove()
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Move, [@"C:\a"], @"C:\a\b")));
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Copy, [@"C:\a"], @"C:\a")));
        Assert.Null(JobRequestValidator.Validate(new JobRequest(JobKind.Copy, [@"C:\a"], @"C:\ab")));
    }

    [Fact]
    public void Delete_HasNoDestination()
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Delete, [@"C:\a"], @"D:\x")));
        Assert.Null(JobRequestValidator.Validate(new JobRequest(JobKind.Delete, [@"C:\a"], null) { DeleteMode = DeleteMode.Permanent }));
    }

    [Theory]
    [InlineData("new.txt", true)]
    [InlineData("a:b", false)]
    [InlineData("", false)]
    [InlineData("ends.", false)]
    [InlineData(null, false)]
    public void Rename_NeedsOneItemAndAValidName(string? name, bool valid)
    {
        var problem = JobRequestValidator.Validate(new JobRequest(JobKind.Rename, [@"C:\a\1.txt"], null) { NewName = name });

        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void Rename_OfSeveralItems_IsRefused()
    {
        Assert.NotNull(JobRequestValidator.Validate(new JobRequest(JobKind.Rename, [@"C:\a\1.txt", @"C:\a\2.txt"], null) { NewName = "x" }));
    }
}

public sealed class SimpleOperationJobTests
{
    private sealed class FakeCopy : IJobExecutor
    {
        public bool CanExecute(JobKind kind) => kind is JobKind.Copy or JobKind.Rename;

        public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.AddResult(new ItemOutcome(@"C:\a\1.txt", @"D:\b\1.txt", ItemStatus.Done));
            context.AddResult(new ItemOutcome(@"C:\a\2.txt", null, ItemStatus.Failed, "Access denied"));
            return Task.FromResult(JobOutcome.Completed);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task GetResult_ReturnsWhatTheExecutorRecorded_AndNullForUnknownJobs()
    {
        await using var host = new JobHost(new JobScheduler(), [new FakeCopy()], new InMemoryJobJournal());
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\a\1.txt", @"C:\a\2.txt"], @"D:\b"), JobLane.Operations, null, "Copy");
        await WaitUntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished);

        var result = host.GetResult(job.Id)!;
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(@"D:\b\1.txt", result.Items[0].Result);
        Assert.Equal(ItemStatus.Failed, result.Items[1].Status);
        Assert.Equal("Access denied", result.Items[1].Error);
        Assert.Null(host.GetResult(Guid.NewGuid()));
    }

    [Fact]
    public void NewRequestFields_SurviveTheJournal_SoARestartedWorkerKeepsThePolicy()
    {
        var journal = new InMemoryJobJournal();
        var request = new JobRequest(JobKind.Rename, [@"C:\a\1.txt"], null)
        {
            NewName = "two.txt",
            Conflict = ConflictPolicy.Skip,
            DeleteMode = DeleteMode.Permanent,
        };
        journal.Append(new JournalRecord { Type = JournalRecordType.Enqueued, JobId = Guid.NewGuid(), At = DateTimeOffset.UnixEpoch, Request = request, Lane = JobLane.Operations, Title = "Rename" });

        var recovered = Assert.Single(JournalReplay.Recover(journal.ReadAll()));

        Assert.Equal("two.txt", recovered.Request.NewName);
        Assert.Equal(ConflictPolicy.Skip, recovered.Request.Conflict);
        Assert.Equal(DeleteMode.Permanent, recovered.Request.DeleteMode);
    }

    [Fact]
    public void NewRequestFields_SurviveTheFileJournal()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var journal = new FileJobJournal(fs, @"C:\j\jobs.journal");
        var request = new JobRequest(JobKind.Move, [@"C:\a\1.txt"], @"D:\b") { Conflict = ConflictPolicy.Replace };
        journal.Append(new JournalRecord { Type = JournalRecordType.Enqueued, JobId = Guid.NewGuid(), At = DateTimeOffset.UnixEpoch, Request = request, Lane = JobLane.Operations, Title = "Move" });

        var read = Assert.Single(journal.ReadAll());

        Assert.Equal(ConflictPolicy.Replace, read.Request!.Conflict);
        Assert.Equal(DeleteMode.RecycleBin, read.Request.DeleteMode);
    }
}
