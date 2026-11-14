using Leviathan.Contracts;
using Leviathan.ViewModels.Elevation;
using Leviathan.ViewModels.Hub;

namespace Leviathan.ViewModels.Tests;

/// <summary>Doing one refused item as administrator (spec §15.6).</summary>
public sealed class ElevationTests
{
    private sealed class FakeControl : IJobControl
    {
        public List<(Guid Job, ErrorDecision Decision)> Answers { get; } = [];

        public Task PauseAsync(Guid jobId) => Task.CompletedTask;

        public Task ResumeAsync(Guid jobId) => Task.CompletedTask;

        public Task CancelAsync(Guid jobId) => Task.CompletedTask;

        public Task RemoveAsync(Guid jobId) => Task.CompletedTask;

        public Task RunNowAsync(Guid jobId) => Task.CompletedTask;

        public Task MoveAsync(Guid jobId, int queuePosition) => Task.CompletedTask;

        public Task SetPriorityAsync(Guid jobId, JobPriority priority) => Task.CompletedTask;

        public Task PauseAllAsync() => Task.CompletedTask;

        public Task ResumeAllAsync() => Task.CompletedTask;

        public Task AnswerPromptAsync(Guid jobId, Guid promptId, ErrorDecision decision)
        {
            Answers.Add((jobId, decision));
            return Task.CompletedTask;
        }

        public Task AnswerConflictsAsync(Guid jobId, Guid promptId, ConflictAnswer answer) => Task.CompletedTask;

        public Task<JobReport?> GetReportAsync(Guid jobId) => Task.FromResult<JobReport?>(null);
    }

    private sealed class FakeRunner : IElevatedRunner
    {
        public List<JobRequest> Requests { get; } = [];

        public Task<ElevatedOutcome> RunAsync(JobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ElevatedOutcome(true, true, "1 item(s) done as administrator."));
        }
    }

    private static OperationEntry Entry(Guid id, ErrorKind kind, string item)
    {
        var model = new OperationsModel();
        model.Apply(new JobSnapshot
        {
            Id = id,
            Kind = JobKind.Copy,
            Lane = JobLane.Operations,
            Title = "Copy",
            State = JobState.Running,
            Priority = JobPriority.Normal,
            Prompt = new JobPrompt(Guid.NewGuid(), kind, item, "Access is denied"),
        });
        return model.Entries.Single();
    }

    [Fact]
    public void OnlyAnAccessDeniedQuestion_OffersAdministrator()
    {
        Assert.True(Entry(Guid.NewGuid(), ErrorKind.AccessDenied, @"C:\a").NeedsAdministrator);
        Assert.False(Entry(Guid.NewGuid(), ErrorKind.DiskFull, @"C:\a").NeedsAdministrator);
    }

    [Fact]
    public void TheRequest_IsTheRefusedItem_WithTheJobsDestination()
    {
        var id = Guid.NewGuid();
        var memory = new ElevationMemory();
        memory.Remember(id, JobKind.Move, [@"C:\a\1.txt", @"C:\a\2.txt"], @"C:\Windows\Target", permanent: false);

        var request = memory.RequestFor(Entry(id, ErrorKind.AccessDenied, @"C:\a\2.txt"))!;

        Assert.Equal(JobKind.Move, request.Kind);
        Assert.Equal([@"C:\a\2.txt"], request.Sources);
        Assert.Equal(@"C:\Windows\Target", request.Destination);
    }

    [Fact]
    public void ForAnItemItCannotMatch_TheWholeJobIsTheRequest()
    {
        var id = Guid.NewGuid();
        var memory = new ElevationMemory();
        memory.Remember(id, JobKind.Delete, [@"C:\a", @"C:\b"], null, permanent: false);

        Assert.Equal([@"C:\a", @"C:\b"], memory.RequestFor(Entry(id, ErrorKind.AccessDenied, @"C:\a\inside\x.txt"))!.Sources);
    }

    [Fact]
    public void ThereIsNoRequest_ForAnUnknownJob_OrOtherKinds_OrOtherErrors()
    {
        var id = Guid.NewGuid();
        var memory = new ElevationMemory();
        memory.Remember(Guid.NewGuid(), JobKind.Extract, [@"C:\a.zip"], @"C:\x", permanent: false);
        memory.Remember(id, JobKind.Copy, [@"C:\a"], @"C:\x", permanent: false);

        Assert.Null(memory.RequestFor(Entry(Guid.NewGuid(), ErrorKind.AccessDenied, @"C:\a")));
        Assert.Null(memory.RequestFor(Entry(id, ErrorKind.DiskFull, @"C:\a")));
    }

    [Fact]
    public async Task SkipAndDoAsAdministrator_SkipsInTheJob_ThenRunsTheItemElevated()
    {
        var id = Guid.NewGuid();
        var memory = new ElevationMemory();
        memory.Remember(id, JobKind.Copy, [@"C:\a\1.txt"], @"C:\Windows\Target", permanent: false);
        var control = new FakeControl();
        var runner = new FakeRunner();

        var outcome = await new OperationActions(control, memory, runner).AnswerAsAdministratorAsync(Entry(id, ErrorKind.AccessDenied, @"C:\a\1.txt"), CancellationToken.None);

        Assert.True(outcome!.Succeeded);
        Assert.Equal([(id, ErrorDecision.Skip)], control.Answers);
        Assert.Equal(@"C:\a\1.txt", Assert.Single(Assert.Single(runner.Requests).Sources));
    }

    [Fact]
    public async Task WithNothingToDo_NothingIsSkipped_AndNothingRuns()
    {
        var control = new FakeControl();
        var runner = new FakeRunner();

        var outcome = await new OperationActions(control, new ElevationMemory(), runner).AnswerAsAdministratorAsync(Entry(Guid.NewGuid(), ErrorKind.AccessDenied, @"C:\a"), CancellationToken.None);

        Assert.Null(outcome);
        Assert.Empty(control.Answers);
        Assert.Empty(runner.Requests);
    }
}
