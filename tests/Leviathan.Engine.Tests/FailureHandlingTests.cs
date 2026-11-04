using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Leviathan.Contracts;
using Leviathan.Core.Resilience;
using Leviathan.Engine.Execution;
using Leviathan.Engine.Journal;
using Leviathan.Engine.Scheduling;
using Leviathan.Engine.Transfers;

namespace Leviathan.Engine.Tests;

public sealed class FailureHandlingTests
{
    private static byte[] Data(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static MockFileSystem Disk()
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDrive("D:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"D:\dst");
        fs.AddFile(@"C:\src\a.bin", new MockFileData(Data(10_000, 1)));
        fs.AddFile(@"C:\src\b.bin", new MockFileData(Data(10_000, 2)));
        fs.AddFile(@"C:\src\c.bin", new MockFileData(Data(10_000, 3)));
        return fs;
    }

    /// <summary>Fails chosen files with a chosen exception, then copies normally.</summary>
    private sealed class FailingCopier(IFileSystem fs, Func<string, int, Exception?> failure) : IFileCopier
    {
        private readonly ChunkedFileCopier _real = new(fs);
        private readonly Dictionary<string, int> _calls = [];

        public int Calls(string name) => _calls.Where(c => c.Key.EndsWith(name, StringComparison.Ordinal)).Sum(c => c.Value);

        public Task<FileCopyResult> CopyAsync(FileCopyRequest request, CancellationToken cancellationToken)
        {
            _calls[request.Source] = _calls.GetValueOrDefault(request.Source) + 1;
            return failure(request.Source, _calls[request.Source]) is { } error ? Task.FromException<FileCopyResult>(error) : _real.CopyAsync(request, cancellationToken);
        }
    }

    private static UnauthorizedAccessException Denied() => new UnauthorizedAccessException("Access to the path is denied.");

    private static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    private static IOException Dropped() => new("The specified network name is no longer available.", unchecked((int)0x80070040));

    private static bool Is(string source, string name) => source.EndsWith(name, StringComparison.Ordinal);

    private static JobHost Start(MockFileSystem fs, IFileCopier copier, IJobReportStore? reports = null, TimeSpan? waitLimit = null, TimeSpan? retry = null)
    {
        var wait = retry ?? TimeSpan.FromMilliseconds(20);
        var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs, copier: copier)], new InMemoryJobJournal(),
            retryBackoff: new BackoffPolicy(wait, wait, 0), waitLimit: waitLimit, reports: reports);
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

    private static JobRequest Copy(ErrorMode mode = ErrorMode.Ask) =>
        new(JobKind.Copy, [@"C:\src\a.bin", @"C:\src\b.bin", @"C:\src\c.bin"], @"D:\dst") { OnError = mode };

    private static JobPrompt? PromptOf(JobHost host, Guid id) => host.Snapshot(host.Scheduler.Find(id)!).Prompt;

    [Fact]
    public async Task SkipAndReport_SkipsTheBadFile_FinishesTheRest_AndTheReportListsIt()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, _) => Is(s, "b.bin") ? Denied() : null), new InMemoryJobReportStore());

        var job = host.Submit(Copy(ErrorMode.SkipAndReport), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => host.GetReport(job.Id) is not null));

        Assert.Equal(JobState.Failed, host.Scheduler.Find(job.Id)!.State);
        Assert.True(fs.File.Exists(@"D:\dst\a.bin"));
        Assert.True(fs.File.Exists(@"D:\dst\c.bin"));
        Assert.False(fs.File.Exists(@"D:\dst\b.bin"));
        var report = host.GetReport(job.Id);
        Assert.NotNull(report);
        Assert.Equal((2, 0, 1), (report.Done, report.Skipped, report.Failed));
        Assert.Contains(report.Items, i => i.Status == ItemStatus.Failed && Is(i.Source, "b.bin") && i.Error!.Contains("denied", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(JobKind.Copy, report.Kind);
    }

    [Fact]
    public async Task Ask_StopsOnTheBadFile_UntilTheUserAnswersSkip()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, _) => Is(s, "b.bin") ? Denied() : null));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => PromptOf(host, job.Id) is not null));
        var prompt = PromptOf(host, job.Id)!;

        Assert.Equal(ErrorKind.AccessDenied, prompt.Kind);
        Assert.EndsWith("b.bin", prompt.Item, StringComparison.Ordinal);
        Assert.False(host.Scheduler.Find(job.Id)!.IsFinished);
        Assert.False(host.AnswerPrompt(job.Id, Guid.NewGuid(), ErrorDecision.Skip));
        Assert.True(host.AnswerPrompt(job.Id, prompt.Id, ErrorDecision.Skip));
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.True(fs.File.Exists(@"D:\dst\c.bin"));
        Assert.Null(PromptOf(host, job.Id));
    }

    [Fact]
    public async Task Retry_RunsTheSameStepAgain_AndSucceedsOnceTheProblemIsFixed()
    {
        var fs = Disk();
        var copier = new FailingCopier(fs, (s, n) => Is(s, "b.bin") && n == 1 ? DiskFull() : null);
        await using var host = Start(fs, copier);

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => PromptOf(host, job.Id) is not null));
        var prompt = PromptOf(host, job.Id)!;
        Assert.Equal(ErrorKind.DiskFull, prompt.Kind);
        host.AnswerPrompt(job.Id, prompt.Id, ErrorDecision.Retry);
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));

        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.Equal(2, copier.Calls("b.bin"));
        Assert.True(fs.File.Exists(@"D:\dst\b.bin"));
    }

    [Fact]
    public async Task SkipAllLikeThis_AsksOnce_ForTheSameReason()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, _) => Is(s, "a.bin") || Is(s, "b.bin") ? Denied() : null));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => PromptOf(host, job.Id) is not null));
        host.AnswerPrompt(job.Id, PromptOf(host, job.Id)!.Id, ErrorDecision.SkipAllLikeThis);

        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));
        Assert.True(fs.File.Exists(@"D:\dst\c.bin"));
        Assert.False(fs.File.Exists(@"D:\dst\b.bin"));
        Assert.Equal(2, host.GetResult(job.Id)!.Items.Count(i => i.Status == ItemStatus.Failed));
    }

    [Fact]
    public async Task ADifferentReason_AsksAgain_EvenAfterSkipAll()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, _) => Is(s, "a.bin") ? Denied() : Is(s, "b.bin") ? DiskFull() : null));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => PromptOf(host, job.Id)?.Kind == ErrorKind.AccessDenied));
        host.AnswerPrompt(job.Id, PromptOf(host, job.Id)!.Id, ErrorDecision.SkipAllLikeThis);

        Assert.True(await UntilAsync(() => PromptOf(host, job.Id)?.Kind == ErrorKind.DiskFull));
        host.AnswerPrompt(job.Id, PromptOf(host, job.Id)!.Id, ErrorDecision.Skip);
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));
    }

    [Fact]
    public async Task CancellingAJobThatIsAsking_EndsIt()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, _) => Is(s, "a.bin") ? Denied() : null));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => PromptOf(host, job.Id) is not null));
        host.Cancel(job.Id);

        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Cancelled));
    }

    [Fact]
    public async Task ANetworkDrop_BecomesAWait_NotAPrompt_AndTheJobFinishes()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, n) => Is(s, "b.bin") && n == 1 ? Dropped() : null));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));
        Assert.Equal(JobState.Completed, host.Scheduler.Find(job.Id)!.State);
        Assert.Null(PromptOf(host, job.Id));
    }

    [Fact]
    public async Task AShareThatNeverComesBack_FailsTheJobAfterTheWaitLimit()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, _) => Is(s, "b.bin") ? Dropped() : null), waitLimit: TimeSpan.FromMilliseconds(100));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");

        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.IsFinished));
        Assert.Equal(JobState.Failed, host.Scheduler.Find(job.Id)!.State);
        Assert.Contains("did not come back", host.Scheduler.Find(job.Id)!.Error, StringComparison.Ordinal);
        Assert.True(fs.File.Exists(@"D:\dst\a.bin"));
    }

    [Fact]
    public async Task Waiting_CarriesTheRetryTime_ForTheCountdown()
    {
        var fs = Disk();
        await using var host = Start(fs, new FailingCopier(fs, (s, n) => Is(s, "b.bin") && n == 1 ? Dropped() : null), retry: TimeSpan.FromSeconds(30));

        var job = host.Submit(Copy(), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => host.Scheduler.Find(job.Id)!.State == JobState.Waiting));
        var snapshot = host.Snapshot(host.Scheduler.Find(job.Id)!);

        Assert.NotNull(snapshot.RetryAt);
        Assert.InRange((snapshot.RetryAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 20, 31);
        Assert.Contains("retrying in", snapshot.WaitingReason, StringComparison.Ordinal);
    }

    // ---------- names ----------

    [Theory]
    [InlineData("plain.txt", true)]
    [InlineData("a:b.txt", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("com1.log", false)]
    [InlineData("comet.txt", true)]
    [InlineData("what?.txt", false)]
    [InlineData("tab\there", false)]
    public void IsValid_FollowsTheShareRules(string name, bool expected) => Assert.Equal(expected, SafeNames.IsValid(name));

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("clip 12:30.mkv", "clip 12_30.mkv")]
    [InlineData("name. ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("...", "_")]
    [InlineData("a|b*c", "a_b_c")]
    public void Sanitize_GivesTheClosestValidName(string name, string expected)
    {
        Assert.Equal(expected, SafeNames.Sanitize(name));
        Assert.True(SafeNames.IsValid(SafeNames.Sanitize(name)));
    }

    [Fact]
    public void Sanitize_CutsALongNameBeforeItsExtension()
    {
        var safe = SafeNames.Sanitize(new string('x', 300) + ".mkv");

        Assert.Equal(255, safe.Length);
        Assert.EndsWith(".mkv", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ToAShare_RenamesAnInvalidFolderAndItsChildren_Consistently()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"\\nas\media");
        fs.AddFile(@"C:\src\What now.\inner.txt", new MockFileData("a"));

        var plan = new TransferPlanner(fs).Plan(Guid.NewGuid(), new JobRequest(JobKind.Copy, [@"C:\src\What now."], @"\\nas\media"), CancellationToken.None);

        Assert.Equal(@"\\nas\media\What now", plan.Items[0].Target);
        Assert.Contains(plan.Items[0].Steps, s => s.Op == PlanOp.CopyFile && s.Target == @"\\nas\media\What now\inner.txt");
        Assert.Contains("changed to be valid", plan.Items[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ToALocalDisk_LeavesNamesAlone()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"D:\dst");
        fs.AddFile(@"C:\src\trailing.", new MockFileData("a"));

        var plan = new TransferPlanner(fs).Plan(Guid.NewGuid(), new JobRequest(JobKind.Copy, [@"C:\src\trailing."], @"D:\dst"), CancellationToken.None);

        Assert.Null(plan.Items[0].Note);
    }

    [Fact]
    public async Task TheReport_ListsTheRenamedNames_AsNotes()
    {
        var fs = new MockFileSystem();
        fs.AddDrive("C:", new MockDriveData { TotalSize = 1L << 30, AvailableFreeSpace = 1L << 29 });
        fs.AddDirectory(@"\\nas\media");
        fs.AddFile(@"C:\src\aux.txt", new MockFileData("b"));
        await using var host = new JobHost(new JobScheduler(), [new CopyMoveExecutor(fs)], new InMemoryJobJournal(), reports: new InMemoryJobReportStore());
        host.Start();

        var job = host.Submit(new JobRequest(JobKind.Copy, [@"C:\src\aux.txt"], @"\\nas\media"), JobLane.Operations, null, "copy");
        Assert.True(await UntilAsync(() => host.GetReport(job.Id) is not null));

        var report = host.GetReport(job.Id)!;
        Assert.Contains(report.Notes, n => n.Contains("aux.txt -> _aux.txt", StringComparison.Ordinal));
        Assert.True(fs.File.Exists(@"\\nas\media\_aux.txt"));
    }

    // ---------- report store ----------

    [Fact]
    public void FileReportStore_RoundTrips_AndPrunesOldReports()
    {
        var fs = new MockFileSystem();
        var store = new FileJobReportStore(fs, @"C:\r");
        var report = new JobReport(Guid.NewGuid(), "Copy 1 item", JobKind.Copy, JobState.Failed, DateTimeOffset.UtcNow, 123, 0, 0, 1, [new ItemOutcome("a", null, ItemStatus.Failed, "denied")], ["note"], "1 of 1 failed");

        store.Save(report);
        var loaded = store.TryLoad(report.JobId);

        Assert.NotNull(loaded);
        Assert.Equal(report.Items, loaded.Items);
        Assert.Equal(JobState.Failed, loaded.State);
        store.Prune(TimeSpan.FromDays(7), DateTimeOffset.UtcNow);
        Assert.NotNull(store.TryLoad(report.JobId));
        store.Prune(TimeSpan.FromDays(7), DateTimeOffset.UtcNow.AddDays(8));
        Assert.Null(store.TryLoad(report.JobId));
        Assert.Null(store.TryLoad(Guid.NewGuid()));
    }

    [Fact]
    public void InMemoryReportStore_PrunesByAge()
    {
        var store = new InMemoryJobReportStore();
        var old = new JobReport(Guid.NewGuid(), "t", JobKind.Copy, JobState.Completed, DateTimeOffset.UtcNow.AddDays(-9), 0, 0, 0, 0, [], [], null);
        var fresh = old with { JobId = Guid.NewGuid(), FinishedAt = DateTimeOffset.UtcNow };
        store.Save(old);
        store.Save(fresh);

        store.Prune(TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        Assert.Null(store.TryLoad(old.JobId));
        Assert.NotNull(store.TryLoad(fresh.JobId));
    }

    [Fact]
    public void Classify_MapsExceptionsToKinds()
    {
        Assert.Equal(ErrorKind.AccessDenied, CopyMoveExecutor.Classify(new UnauthorizedAccessException()));
        Assert.Equal(ErrorKind.VerifyFailed, CopyMoveExecutor.Classify(new VerifyFailedException("x")));
        Assert.Equal(ErrorKind.Other, CopyMoveExecutor.Classify(new InvalidOperationException()));
    }

    [Theory]
    [InlineData(0x80070005u, ErrorKind.AccessDenied)]
    [InlineData(0x80070070u, ErrorKind.DiskFull)]
    [InlineData(0x80070027u, ErrorKind.DiskFull)]
    [InlineData(0x8007007Bu, ErrorKind.InvalidName)]
    [InlineData(0x800700CEu, ErrorKind.InvalidName)]
    [InlineData(0x80070001u, ErrorKind.Other)]
    public void Classify_ReadsWin32CodesFromIoExceptions(uint hresult, ErrorKind expected) =>
        Assert.Equal(expected, CopyMoveExecutor.Classify(new IOException("x", unchecked((int)hresult))));
}
