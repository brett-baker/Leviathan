using Leviathan.Contracts;
using Leviathan.Engine.Elevation;
using Leviathan.Engine.Execution;

namespace Leviathan.Engine.Tests;

/// <summary>The request, the allow-list and the answer of the administrator helper (spec §15.6).</summary>
public sealed class ElevatedWorkTests
{
    private sealed class OneItemCopy : IJobExecutor
    {
        public bool CanExecute(JobKind kind) => kind == JobKind.Copy;

        public Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.AddResult(new ItemOutcome(context.Job.Request.Sources[0], @"C:\Windows\Target\a.txt", ItemStatus.Done));
            return Task.FromResult(JobOutcome.Completed);
        }
    }

    private static JobRequest Copy() => new(JobKind.Copy, [@"C:\a\1.txt"], @"C:\Windows\Target");

    [Theory]
    [InlineData(JobKind.Copy, true)]
    [InlineData(JobKind.Move, true)]
    [InlineData(JobKind.Extract, false)]
    [InlineData(JobKind.Sync, false)]
    [InlineData(JobKind.Purge, false)]
    public void OnlyCopyMoveAndDelete_MayRunElevated(JobKind kind, bool allowed) =>
        Assert.Equal(allowed, ElevatedWork.IsAllowed(new JobRequest(kind, [@"C:\a"], @"C:\b"), out _));

    [Fact]
    public void ADeleteTakesNoDestination_AndACopyNeedsOne()
    {
        Assert.True(ElevatedWork.IsAllowed(new JobRequest(JobKind.Delete, [@"C:\a"], null), out _));
        Assert.False(ElevatedWork.IsAllowed(new JobRequest(JobKind.Delete, [@"C:\a"], @"C:\b"), out _));
        Assert.False(ElevatedWork.IsAllowed(new JobRequest(JobKind.Copy, [@"C:\a"], null), out _));
        Assert.False(ElevatedWork.IsAllowed(new JobRequest(JobKind.Copy, [], @"C:\b"), out _));
    }

    [Theory]
    [InlineData(@"\\nas\media\x", true)]
    [InlineData(@"D:\x", true)]
    [InlineData("sftp://host/x", false)]
    [InlineData("shell:tag", false)]
    [InlineData("relative\\x", false)]
    public void OnlyDrivesAndShares_AreAllowed(string place, bool allowed) =>
        Assert.Equal(allowed, ElevatedWork.IsAllowed(new JobRequest(JobKind.Delete, [place], null), out _));

    [Fact]
    public void TheRequest_SurvivesTheCommandLine()
    {
        var request = Copy() with { Conflict = ConflictPolicy.Replace, DeleteMode = DeleteMode.Permanent };

        var encoded = ElevatedWork.Encode(request);
        var back = ElevatedWork.Decode(encoded)!;

        Assert.Equal(request.Kind, back.Kind);
        Assert.Equal(request.Sources, back.Sources);
        Assert.Equal(request.Destination, back.Destination);
        Assert.Equal(ConflictPolicy.Replace, back.Conflict);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void ADamagedOrForbiddenArgument_IsRefused()
    {
        Assert.Null(ElevatedWork.Decode("not base64 !!"));
        Assert.Null(ElevatedWork.Decode(string.Empty));
        Assert.Throws<InvalidOperationException>(() => ElevatedWork.Encode(new JobRequest(JobKind.Extract, [@"C:\a.zip"], @"C:\x")));
        Assert.Throws<InvalidOperationException>(() => ElevatedWork.Encode(new JobRequest(JobKind.Delete, [.. Enumerable.Range(0, 2000).Select(i => $@"C:\folder\a long file name number {i}.txt")], null)));
    }

    [Fact]
    public void AnAnswer_SurvivesThePipe_AndGarbageIsAFailure()
    {
        var result = new ElevatedResult(JobState.Completed, null, [new ItemOutcome(@"C:\a", @"C:\b", ItemStatus.Done)]);

        var back = ElevatedWork.DecodeResult(ElevatedWork.EncodeResult(result));

        Assert.True(back.Succeeded);
        Assert.Equal("1 item(s) done as administrator.", back.Summary);
        Assert.False(ElevatedWork.DecodeResult("{broken").Ran);
        Assert.False(ElevatedWork.DecodeResult(null).Ran);
    }

    [Fact]
    public void AFailedItem_IsNamedInTheSummary()
    {
        var result = new ElevatedResult(JobState.Completed, null, [new ItemOutcome(@"C:\a", null, ItemStatus.Failed, "Sharing violation")]);

        Assert.False(result.Succeeded);
        Assert.Contains("Sharing violation", result.Summary);
    }

    [Fact]
    public async Task Run_ExecutesTheRequestOnce_AndReturnsItsItems()
    {
        var result = await ElevatedWork.RunAsync(Copy(), [new OneItemCopy()], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(@"C:\a\1.txt", Assert.Single(result.Items).Source);
    }

    [Fact]
    public async Task Run_RefusesWhatIsNotAllowed_WithoutRunningAnything()
    {
        var result = await ElevatedWork.RunAsync(new JobRequest(JobKind.Extract, [@"C:\a.zip"], @"C:\x"), [new OneItemCopy()], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Contains("Extract", result.Error);
    }
}
