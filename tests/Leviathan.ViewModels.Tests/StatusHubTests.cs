using Leviathan.Contracts;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Hub;

namespace Leviathan.ViewModels.Tests;

public sealed class StatusHubModelTests
{
    [Fact]
    public void TheHub_StartsClosed_OnTheOperationsTab_AsAFlyout_AboutFortyPercentHigh()
    {
        var hub = new StatusHubModel();

        Assert.False(hub.IsOpen);
        Assert.Equal(HubTab.Operations, hub.Tab);
        Assert.Equal(HubPlacement.Flyout, hub.Placement);
        Assert.False(hub.IsPinned);
        Assert.Equal(0.4, hub.HeightFraction);
    }

    [Fact]
    public void Toggle_OpensOnTheLastTab_AndClosesAgain()
    {
        var hub = new StatusHubModel();
        hub.Open(HubTab.Background);
        hub.Close();

        hub.Toggle();
        Assert.True(hub.IsOpen);
        Assert.Equal(HubTab.Background, hub.Tab);

        hub.Toggle();
        Assert.False(hub.IsOpen);
    }

    [Fact]
    public void Toggle_WithATab_SwitchesToItWhenOpenOnAnotherTab_AndClosesWhenAlreadyOnIt()
    {
        var hub = new StatusHubModel();
        hub.Toggle(HubTab.Operations);
        Assert.True(hub.IsOpen);

        hub.Toggle(HubTab.Space);
        Assert.True(hub.IsOpen);
        Assert.Equal(HubTab.Space, hub.Tab);

        hub.Toggle(HubTab.Space);
        Assert.False(hub.IsOpen);
    }

    [Fact]
    public void Dismiss_ClosesAnUnpinnedFlyoutOnly()
    {
        var hub = new StatusHubModel();
        Assert.False(hub.Dismiss());

        hub.Open();
        Assert.True(hub.Dismiss());
        Assert.False(hub.IsOpen);

        hub.Open();
        hub.IsPinned = true;
        Assert.False(hub.Dismiss());
        Assert.True(hub.IsOpen);

        hub.IsPinned = false;
        hub.Place(HubPlacement.DockBottom);
        Assert.False(hub.Dismiss());
        Assert.True(hub.IsOpen);

        hub.Place(HubPlacement.PoppedOut);
        Assert.False(hub.Dismiss());
    }

    [Fact]
    public void Place_OpensTheHub_AtTheNewPlace()
    {
        var hub = new StatusHubModel();

        hub.Place(HubPlacement.DockRight);

        Assert.True(hub.IsOpen);
        Assert.Equal(HubPlacement.DockRight, hub.Placement);
    }

    [Theory]
    [InlineData(10, 1000, StatusHubModel.MinHeightFraction)]
    [InlineData(900, 1000, StatusHubModel.MaxHeightFraction)]
    [InlineData(300, 1000, 0.3)]
    public void SetHeight_KeepsTheHeightInsideTheLimits(double height, double available, double expected)
    {
        var hub = new StatusHubModel();

        hub.SetHeight(height, available);

        Assert.Equal(expected, hub.HeightFraction, 5);
    }

    [Fact]
    public void SetHeight_IgnoresNonsense()
    {
        var hub = new StatusHubModel();

        hub.SetHeight(double.NaN, 1000);
        hub.SetHeight(300, 0);

        Assert.Equal(StatusHubModel.DefaultHeightFraction, hub.HeightFraction);
    }

    [Fact]
    public void SetWidth_StaysBetweenTheMinimumAndAShareOfTheWindow()
    {
        var hub = new StatusHubModel();

        hub.SetWidth(50, 1500);
        Assert.Equal(StatusHubModel.MinWidth, hub.Width);

        hub.SetWidth(5000, 1500);
        Assert.Equal(900, hub.Width);

        hub.SetWidth(500, 1500);
        Assert.Equal(500, hub.Width);
    }

    [Fact]
    public void Changes_ToWhatIsRemembered_RaiseLayoutChanged_ButOpeningDoesNot()
    {
        var hub = new StatusHubModel();
        var seen = new List<HubLayout>();
        hub.LayoutChanged += (_, layout) => seen.Add(layout);

        hub.Open();
        Assert.Empty(seen);

        hub.Open(HubTab.Space);
        hub.IsPinned = true;
        hub.SetHeight(500, 1000);

        Assert.Equal(3, seen.Count);
        Assert.Equal(HubTab.Space, seen[^1].Tab);
        Assert.True(seen[^1].Pinned);
        Assert.Equal(0.5, seen[^1].HeightFraction);
    }

    [Fact]
    public void Apply_TakesALayout_WithoutRaisingLayoutChanged_AndWithoutOpening()
    {
        var hub = new StatusHubModel();
        var raised = false;
        hub.LayoutChanged += (_, _) => raised = true;

        hub.Apply(new HubLayout { Tab = HubTab.QuickSettings, Placement = HubPlacement.DockRight, Pinned = true, HeightFraction = 0.6, Width = 500 });

        Assert.False(raised);
        Assert.False(hub.IsOpen);
        Assert.Equal(HubTab.QuickSettings, hub.Tab);
        Assert.Equal(HubPlacement.DockRight, hub.Placement);
        Assert.True(hub.IsPinned);
        Assert.Equal(0.6, hub.HeightFraction);
        Assert.Equal(500, hub.Width);
    }

    [Fact]
    public void Apply_RepairsALayoutFromAHandEditedFile()
    {
        var hub = new StatusHubModel();

        hub.Apply(new HubLayout { Tab = (HubTab)99, Placement = HubPlacement.PoppedOut, HeightFraction = double.NaN, Width = -5 });

        Assert.Equal(HubTab.Operations, hub.Tab);
        Assert.Equal(HubPlacement.Flyout, hub.Placement);
        Assert.Equal(StatusHubModel.DefaultHeightFraction, hub.HeightFraction);
        Assert.Equal(StatusHubModel.MinWidth, hub.Width);
    }

    [Fact]
    public void ThePoppedOutHub_IsRememberedAsAFlyout()
    {
        var hub = new StatusHubModel();

        hub.Place(HubPlacement.PoppedOut);

        Assert.Equal(HubPlacement.Flyout, hub.Layout.Placement);
    }
}

public sealed class OperationsModelTests
{
    private static JobSnapshot Job(JobState state, string title = "Copy", JobLane lane = JobLane.Operations, long done = 0, long total = -1, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = JobKind.Copy,
        Lane = lane,
        Title = title,
        State = state,
        Priority = JobPriority.Normal,
        BytesDone = done,
        BytesTotal = total,
    };

    [Fact]
    public void BackgroundJobs_AreNotOperations()
    {
        var model = new OperationsModel();

        model.Apply(Job(JobState.Running, lane: JobLane.Background));

        Assert.Empty(model.Entries);
        Assert.Equal(string.Empty, model.Summary);
    }

    [Fact]
    public void ASnapshot_UpdatesTheSameJob()
    {
        var model = new OperationsModel();
        var id = Guid.NewGuid();

        model.Apply(Job(JobState.Running, done: 10, total: 100, id: id));
        model.Apply(Job(JobState.Running, done: 50, total: 100, id: id));

        var entry = Assert.Single(model.Entries);
        Assert.Equal(50, entry.Percent);
    }

    [Fact]
    public void Entries_ListRunningFirst_ThenQueued_ThenFinishedNewestFirst()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Completed, "old done"));
        model.Apply(Job(JobState.Queued, "queued"));
        model.Apply(Job(JobState.Completed, "new done"));
        model.Apply(Job(JobState.Running, "running"));

        Assert.Equal(["running", "queued", "new done", "old done"], model.Entries.Select(e => e.Title));
    }

    [Fact]
    public void ATotalNotKnownYet_HasNoPercent_AndACompletedJobIsHundred()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Running, "scanning"));
        model.Apply(Job(JobState.Completed, "done", done: 5, total: 5));

        Assert.Null(model.Entries.Single(e => e.Title == "scanning").Percent);
        Assert.Equal(100, model.Entries.Single(e => e.Title == "done").Percent);
    }

    [Fact]
    public void Summary_CountsJobs_Queue_AndSpeed()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Running) with { BytesPerSecond = 112_000_000, Remaining = TimeSpan.FromMinutes(2) });
        model.Apply(Job(JobState.Queued));

        Assert.Equal(1, model.ActiveCount);
        Assert.Equal(1, model.QueuedCount);
        Assert.StartsWith("2 jobs · 1 queued · ", model.Summary, StringComparison.Ordinal);
        Assert.Contains("/s", model.Summary, StringComparison.Ordinal);
        Assert.EndsWith("2 min left", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_OfOneJob_SaysJob_AndIsEmptyWhenNothingRuns()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Running));
        Assert.Equal("1 job", model.Summary);

        model.Apply(Job(JobState.Completed));
        model.ClearFinished();
        var running = model.Entries.Single();
        model.Apply(Job(JobState.Completed, id: running.Id));
        Assert.Equal(string.Empty, model.Summary);
    }

    [Fact]
    public void FailedJobs_CountAsUnseen_UntilMarkedSeen()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Failed, "broken") with { Error = "Access denied" });
        Assert.Equal(1, model.UnseenFailures);
        Assert.Equal("Access denied", model.Entries.Single().Detail);
        Assert.True(model.Entries.Single().IsFailed);

        model.MarkFailuresSeen();
        Assert.Equal(0, model.UnseenFailures);

        model.Apply(Job(JobState.Failed, "broken again"));
        Assert.Equal(1, model.UnseenFailures);
    }

    [Fact]
    public void OnlyTheNewestFinishedJobs_AreKept_ButActiveOnesAreNever_Dropped()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Running, "long one"));
        for (var i = 0; i < OperationsModel.FinishedKept + 10; i++)
        {
            model.Apply(Job(JobState.Completed, $"done {i}"));
        }

        var titles = model.Entries.Select(e => e.Title).ToList();
        Assert.Equal(OperationsModel.FinishedKept + 1, titles.Count);
        Assert.Contains("long one", titles);
        Assert.DoesNotContain("done 0", titles);
        Assert.Contains($"done {OperationsModel.FinishedKept + 9}", titles);
    }

    [Fact]
    public void ClearFinished_KeepsActiveJobs_AndRemoveForgetsOne()
    {
        var model = new OperationsModel();
        var running = Job(JobState.Running, "running");
        model.Apply(running);
        model.Apply(Job(JobState.Completed, "done"));

        model.ClearFinished();
        Assert.Equal(["running"], model.Entries.Select(e => e.Title));

        model.Remove(running.Id);
        Assert.Empty(model.Entries);
    }

    [Fact]
    public void Changed_IsRaisedOnEveryChange_NotOnAnIgnoredJob()
    {
        var model = new OperationsModel();
        var count = 0;
        model.Changed += (_, _) => count++;

        model.Apply(Job(JobState.Running));
        model.Apply(Job(JobState.Running, lane: JobLane.Background));
        model.MarkFailuresSeen();
        model.ClearFinished();

        Assert.Equal(3, count);
    }

    [Fact]
    public void AQueuedJob_SaysWhyItWaits()
    {
        var model = new OperationsModel();
        model.Apply(Job(JobState.Queued) with { WaitingReason = "Waiting: 1 job per NAS volume" });

        Assert.Equal("Waiting: 1 job per NAS volume", model.Entries.Single().Detail);
    }
}

public sealed class StatusHubCommandTests
{
    [Fact]
    public async Task TheHubCommand_OpensOnTheTabGiven_ThenClosesOnTheSameTab_AndHasItsShortcut()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.HubToggle);
        Assert.True(h.Window.Hub.IsOpen);

        await h.Run(CommandIds.HubToggle, parameter: HubTab.Background);
        Assert.True(h.Window.Hub.IsOpen);
        Assert.Equal(HubTab.Background, h.Window.Hub.Tab);

        await h.Run(CommandIds.HubToggle, parameter: HubTab.Background);
        Assert.False(h.Window.Hub.IsOpen);
        Assert.Equal(CommandIds.HubToggle, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Ctrl+Shift+O"), isSplit: false));
    }
}
