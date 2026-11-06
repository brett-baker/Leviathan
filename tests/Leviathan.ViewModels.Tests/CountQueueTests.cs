using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

/// <summary>Which folders get counted, in what order, and never twice (spec §12.7).</summary>
public sealed class CountQueueTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void Add_TheSameFolderTwice_IsQueuedOnce()
    {
        var queue = new CountQueue();

        Assert.Equal(CountAdd.Submit, queue.Add(P(@"D:\media")));
        Assert.Equal(CountAdd.AlreadyQueued, queue.Add(P(@"D:\media")));
        Assert.Equal(CountAdd.AlreadyQueued, queue.Add(P(@"d:\MEDIA")));
    }

    [Fact]
    public void Add_AFolderBelowOneBeingCounted_IsCovered_NotQueuedAgain()
    {
        var queue = new CountQueue();
        queue.Add(P(@"D:\media"));

        Assert.Equal(CountAdd.Covered, queue.Add(P(@"D:\media\shows")));
        Assert.Equal(CountAdd.Covered, queue.Add(P(@"D:\media\shows")));
        Assert.Equal(CountAdd.Submit, queue.Add(P(@"D:\other")));
    }

    [Fact]
    public void Add_AFolderAboveOneBeingCounted_IsStillQueued()
    {
        var queue = new CountQueue();
        queue.Add(P(@"D:\media\shows"));

        Assert.Equal(CountAdd.Submit, queue.Add(P(@"D:\media")));
    }

    [Fact]
    public void Finished_ReleasesTheFolderAndWhatItCovered_ToBeReadFromTheStore()
    {
        var queue = new CountQueue();
        var job = Guid.NewGuid();
        queue.Add(P(@"D:\media"));
        queue.Started(P(@"D:\media"), job);
        queue.Add(P(@"D:\media\a"));
        queue.Add(P(@"D:\media\b"));

        var done = queue.Finished(job);

        Assert.NotNull(done);
        Assert.Equal(P(@"D:\media"), done!.Value.Folder);
        Assert.Equal([P(@"D:\media\a"), P(@"D:\media\b")], done.Value.Covered.OrderBy(p => p.Value));
        Assert.Equal(CountAdd.Submit, queue.Add(P(@"D:\media")));
        Assert.Null(queue.Finished(Guid.NewGuid()));
    }

    [Fact]
    public void Failed_ReleasesTheFolder_SoItCanBeAskedForAgain()
    {
        var queue = new CountQueue();
        queue.Add(P(@"D:\media"));
        queue.Add(P(@"D:\media\a"));

        var covered = queue.Failed(P(@"D:\media"));

        Assert.Equal([P(@"D:\media\a")], covered);
        Assert.Equal(CountAdd.Submit, queue.Add(P(@"D:\media")));
    }

    [Fact]
    public void SetUrgent_RaisesWhatIsOnScreen_AndDropsWhatScrolledAway_WithoutLosingIt()
    {
        var queue = new CountQueue();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        queue.Add(P(@"D:\a"));
        queue.Started(P(@"D:\a"), a);
        queue.Add(P(@"D:\b"));
        queue.Started(P(@"D:\b"), b);

        var first = queue.SetUrgent([P(@"D:\a")]);
        Assert.Equal([new PriorityChange(a, JobPriority.High)], first);

        var scrolled = queue.SetUrgent([P(@"D:\b")]);
        Assert.Equal(2, scrolled.Count);
        Assert.Contains(new PriorityChange(a, JobPriority.Low), scrolled);
        Assert.Contains(new PriorityChange(b, JobPriority.High), scrolled);

        Assert.Empty(queue.SetUrgent([P(@"D:\b")]));
        Assert.True(queue.IsQueued(P(@"D:\a")));
    }

    [Fact]
    public void PriorityFor_AFolderNotYetSubmitted_FollowsWhatIsUrgent()
    {
        var queue = new CountQueue();
        queue.SetUrgent([P(@"D:\visible")]);

        Assert.Equal(JobPriority.High, queue.PriorityFor(P(@"D:\visible")));
        Assert.Equal(JobPriority.Low, queue.PriorityFor(P(@"D:\hidden")));
    }

    [Fact]
    public void SetUrgent_AFolderCoveredByAWalk_RaisesThatWalk()
    {
        var queue = new CountQueue();
        var walk = Guid.NewGuid();
        queue.Add(P(@"D:\media"));
        queue.Started(P(@"D:\media"), walk);
        queue.Add(P(@"D:\media\shows"));

        var changes = queue.SetUrgent([P(@"D:\media\shows")]);

        Assert.Equal([new PriorityChange(walk, JobPriority.High)], changes);
    }

    [Fact]
    public void FolderOf_FindsTheFolderOfARunningJob()
    {
        var queue = new CountQueue();
        var job = Guid.NewGuid();
        queue.Add(P(@"D:\media"));
        queue.Started(P(@"D:\media"), job);

        Assert.Equal(P(@"D:\media"), queue.FolderOf(job));
        Assert.Null(queue.FolderOf(Guid.NewGuid()));
    }
}

/// <summary>The daily re-check of the shares the user opted in (spec §12.7).</summary>
public sealed class SizeRecheckScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Parse_KeepsDriveLetterPathsOnce_AndSkipsTheRest()
    {
        var paths = SizeRecheckSchedule.Parse(["Y:\\media", "  ", "y:\\MEDIA", "\\\\nas\\media", "not a path", " Z:\\ "]);

        Assert.Equal([LocationPath.Parse(@"Y:\media"), LocationPath.Parse(@"Z:\")], paths);
    }

    [Fact]
    public async Task Run_WaitsFiveMinutes_ThenCountsEachPathOncePerDay_ReadingTheListEachTime()
    {
        var time = new ManualTimeProvider(Now);
        var asked = new List<string>();
        IReadOnlyList<LocationPath> list = [LocationPath.Parse(@"Y:\media")];
        using var stop = new CancellationTokenSource();
        var run = new SizeRecheckSchedule(time).RunAsync(() => list, p => { lock (asked) { asked.Add(p.Value); } }, stop.Token);

        await Task.Delay(50);
        lock (asked)
        {
            Assert.Empty(asked);
        }

        time.Advance(SizeRecheckSchedule.FirstDelay);
        await UntilAsync(() => { lock (asked) { return asked.Count == 1; } });

        list = [LocationPath.Parse(@"Y:\media"), LocationPath.Parse(@"Z:\")];
        time.Advance(SizeRecheckSchedule.Interval);
        await UntilAsync(() => { lock (asked) { return asked.Count == 3; } });

        lock (asked)
        {
            Assert.Equal([@"Y:\media", @"Y:\media", @"Z:\"], asked);
        }

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Run_AWorkerThatIsAway_IsSkippedNotFatal()
    {
        var time = new ManualTimeProvider(Now);
        var calls = 0;
        using var stop = new CancellationTokenSource();
        var run = new SizeRecheckSchedule(time).RunAsync(
            () => [LocationPath.Parse(@"Y:\media")],
            _ => { Interlocked.Increment(ref calls); throw new IOException("worker away"); },
            stop.Token);

        time.Advance(SizeRecheckSchedule.FirstDelay);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);
        time.Advance(SizeRecheckSchedule.Interval);
        await UntilAsync(() => Volatile.Read(ref calls) == 2);

        Assert.False(run.IsFaulted);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public void TheSettingIsEmptyByDefault_AndKeepsWhatWasTyped()
    {
        Assert.Empty(new FilesSettings().RecheckSizePaths);
        var typed = new FilesSettings { RecheckSizePaths = [@"Y:\media"] };
        Assert.Equal([@"Y:\media"], typed.RecheckSizePaths);
    }
}
