using Leviathan.Testing;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Updates;

namespace Leviathan.ViewModels.Tests;

public sealed class UpdateCheckerTests
{
    private sealed class FakeSource(Func<UpdateRequest, CancellationToken, Task<UpdateOffer?>> answer) : IUpdateSource
    {
        public int Calls;
        public UpdateRequest? Last;

        public Task<UpdateOffer?> CheckAsync(UpdateRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Last = request;
            return answer(request, cancellationToken);
        }
    }

    private static FakeSource Offering(string? version) =>
        new((_, _) => Task.FromResult(version is null ? null : new UpdateOffer(version, "Notes")));

    private static AppVersion Stable(string number = "0.5.0") => new(number, null, "abc1234", true);

    private static AppVersion Beta(string number = "0.6.0", string label = "beta.1") => new(number, label, "abc1234", true);

    private static AppVersion Dev() => new(AppVersion.UntaggedNumber, null, null, false);

    private static UpdateChecker Checker(IUpdateSource source, AppVersion current, UpdateSettings? settings = null, TimeProvider? time = null) =>
        new(source, current, () => settings ?? new UpdateSettings(), time);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "The condition was not met in time.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Check_ANewerVersion_IsAvailableWithItsNotes()
    {
        var checker = Checker(Offering("0.6.0"), Stable());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal("0.6.0", checker.Offer!.Version);
        Assert.Equal("Notes", checker.Offer.Notes);
        Assert.Equal("Leviathan 0.6.0 is ready.", checker.Message);
    }

    [Theory]
    [InlineData("0.5.0")]
    [InlineData("0.4.0")]
    [InlineData("not a version")]
    public async Task Check_TheSameOlderOrUnreadableVersion_IsUpToDate(string offered)
    {
        var checker = Checker(Offering(offered), Stable());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, checker.State);
        Assert.Null(checker.Offer);
    }

    [Fact]
    public async Task Check_NothingOffered_IsUpToDate()
    {
        var checker = Checker(Offering(null), Stable());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, checker.State);
    }

    [Fact]
    public async Task Check_ABetaOfferedToAStableBuild_IsIgnored()
    {
        var checker = Checker(Offering("0.6.0-beta.1"), Stable());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, checker.State);
    }

    [Fact]
    public async Task Check_ABetaBuild_IsOfferedTheNextBeta_AndAsksTheBetaChannel()
    {
        var source = Offering("0.6.0-beta.2");
        var checker = Checker(source, Beta());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, checker.State);
        Assert.Equal(ReleaseChannel.Beta, source.Last!.Channel);
    }

    [Fact]
    public async Task Check_ABetaBuild_IsOfferedTheFinalRelease()
    {
        var checker = Checker(Offering("0.6.0"), Beta());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, checker.State);
    }

    [Fact]
    public async Task Check_ChoosingBeta_OnAStableBuild_AsksTheBetaChannel()
    {
        var source = Offering("0.6.0-beta.1");
        var checker = Checker(source, Stable(), new UpdateSettings { Channel = UpdateChannelChoice.Beta });

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(ReleaseChannel.Beta, source.Last!.Channel);
        Assert.Equal(UpdateState.Available, checker.State);
    }

    [Fact]
    public async Task Check_ChoosingStable_OnABetaBuild_AsksTheStableChannel()
    {
        var source = Offering("0.6.0-beta.2");
        var checker = Checker(source, Beta(), new UpdateSettings { Channel = UpdateChannelChoice.Stable });

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(ReleaseChannel.Stable, source.Last!.Channel);
        Assert.Equal(UpdateState.UpToDate, checker.State);
    }

    [Fact]
    public async Task Check_ADevelopmentBuild_NeverAsksTheFeed()
    {
        var source = Offering("9.9.9");
        var checker = Checker(source, Dev());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Disabled, checker.State);
        Assert.Equal(0, source.Calls);
        Assert.Contains("development build", checker.Message);
    }

    [Fact]
    public async Task Check_WhenTheFeedCannotBeReached_FailsQuietlyAndKeepsTheReason()
    {
        var checker = Checker(new FakeSource((_, _) => throw new HttpRequestException("No such host")), Stable());

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Contains("Could not reach the update feed", checker.Message);
        Assert.Equal("No such host", checker.LastError);
        Assert.Null(checker.LastChecked);
    }

    [Fact]
    public async Task Check_AFeedThatNeverAnswers_GivesUpAfterTheTimeout()
    {
        var time = new ManualTimeProvider();
        var checker = Checker(new FakeSource(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }), Stable(), time: time);

        var check = checker.CheckAsync(CancellationToken.None);
        await Until(() => time.Pending > 0);
        time.Advance(UpdateChecker.CheckTimeout + TimeSpan.FromSeconds(1));
        await check;

        Assert.Equal(UpdateState.Failed, checker.State);
        Assert.Contains("did not answer in time", checker.Message);
    }

    [Fact]
    public async Task Check_AfterAFailure_TheNextSuccessClearsTheReason()
    {
        var fail = true;
        var checker = Checker(new FakeSource((_, _) => fail ? throw new IOException("offline") : Task.FromResult<UpdateOffer?>(null)), Stable());
        await checker.CheckAsync(CancellationToken.None);

        fail = false;
        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, checker.State);
        Assert.Null(checker.LastError);
        Assert.NotNull(checker.LastChecked);
    }

    [Fact]
    public async Task Check_CancelledByTheCaller_Throws_AndIsNotLeftChecking()
    {
        using var cts = new CancellationTokenSource();
        var checker = Checker(new FakeSource(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }), Stable());

        var check = checker.CheckAsync(cts.Token);
        await Until(() => checker.State == UpdateState.Checking);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.NotEqual(UpdateState.Checking, checker.State);
    }

    [Fact]
    public async Task Check_RaisesChangedForCheckingAndTheResult()
    {
        var states = new List<UpdateState>();
        var checker = Checker(Offering("0.6.0"), Stable());
        checker.Changed += (_, _) => states.Add(checker.State);

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal([UpdateState.Checking, UpdateState.Available], states);
    }

    [Fact]
    public async Task Run_WaitsForTheFirstDelay_ThenChecksEverySixHours()
    {
        var time = new ManualTimeProvider();
        var source = Offering(null);
        var checker = Checker(source, Stable(), time: time);
        using var cts = new CancellationTokenSource();

        var run = checker.RunAsync(cts.Token);
        await Until(() => time.Pending > 0);
        time.Advance(UpdateChecker.FirstCheckDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, source.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => source.Calls == 1 && time.Pending > 0);
        time.Advance(UpdateChecker.Interval);
        await Until(() => source.Calls == 2);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Run_WithCheckingSwitchedOff_NeverAsksTheFeed()
    {
        var time = new ManualTimeProvider();
        var source = Offering("0.6.0");
        var checker = Checker(source, Stable(), new UpdateSettings { CheckForUpdates = false }, time);
        using var cts = new CancellationTokenSource();

        var run = checker.RunAsync(cts.Token);
        await Until(() => time.Pending > 0);
        time.Advance(UpdateChecker.FirstCheckDelay);
        await Until(() => time.Pending > 0);
        time.Advance(UpdateChecker.Interval);

        Assert.Equal(0, source.Calls);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Run_ADevelopmentBuild_EndsAtOnce()
    {
        var checker = Checker(Offering("9.9.9"), Dev());

        await checker.RunAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Disabled, checker.State);
    }
}
