using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;
using Leviathan.Testing;

namespace Leviathan.Providers.Local.Tests;

/// <summary>Network folders are watched by a cheap, backing-off poll of the folder's time (spec §3.3).</summary>
public sealed class PollingFolderWatcherTests
{
    private static readonly LocationPath Share = LocationPath.Parse(@"\\nas\media\shows");
    private static readonly DateTime Stamp = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Counts questions and answers with a time the test controls.</summary>
    private sealed class Folder
    {
        private int _asked;

        public DateTime? Stamp { get; set; } = PollingFolderWatcherTests.Stamp;

        public int Asked => Volatile.Read(ref _asked);

        public Task<DateTime?> Read(LocationPath path, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            return Task.FromResult(Stamp);
        }
    }

    private static PollingFolderWatcher Make(ManualTimeProvider time, Folder folder, Func<LocationPath, bool>? canPoll = null) => new(time)
    {
        // No jitter: the waits are exactly 3 s, 6 s, 12 s, 24 s, 48 s, 60 s, 60 s.
        Backoff = new BackoffPolicy(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60), 0),
        ReadStamp = folder.Read,
        CanPoll = canPoll ?? (_ => true),
    };

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(5);
        }
    }

    // Lets the watcher's loop reach its next wait, moves the clock by exactly that wait, and waits for the question it asks.
    private static async Task StepAsync(ManualTimeProvider time, Folder folder, TimeSpan wait)
    {
        var before = folder.Asked;
        await Task.Delay(30);
        time.Advance(wait);
        await UntilAsync(() => folder.Asked > before);
    }

    [Fact]
    public async Task ANetworkFolder_IsAskedOnceAtStart_ThenLessAndLessOften_WhileNothingChanges()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder();
        var changes = 0;
        using var watch = Make(time, folder).Watch(Share, () => Interlocked.Increment(ref changes));
        await UntilAsync(() => folder.Asked == 1);

        foreach (var seconds in new[] { 3, 6, 12, 24, 48, 60, 60 })
        {
            // Just short of the wait: no question yet.
            var asked = folder.Asked;
            await Task.Delay(30);
            time.Advance(TimeSpan.FromSeconds(seconds - 1));
            await Task.Delay(30);
            Assert.Equal(asked, folder.Asked);
            time.Advance(TimeSpan.FromSeconds(1));
            await UntilAsync(() => folder.Asked > asked);
        }

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task AChangedTime_IsReportedOnce_AndTheWaitStartsOverAtThreeSeconds()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder();
        var changes = 0;
        using var watch = Make(time, folder).Watch(Share, () => Interlocked.Increment(ref changes));
        await UntilAsync(() => folder.Asked == 1);
        await StepAsync(time, folder, TimeSpan.FromSeconds(3));
        await StepAsync(time, folder, TimeSpan.FromSeconds(6));

        folder.Stamp = Stamp.AddMinutes(1);
        await StepAsync(time, folder, TimeSpan.FromSeconds(12));
        await UntilAsync(() => Volatile.Read(ref changes) == 1);

        // Back to the short wait, and the same time again is not another change.
        await StepAsync(time, folder, TimeSpan.FromSeconds(3));
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task AFolderThatCannotBeRead_BacksOffAndNeverReportsAChange()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder { Stamp = null };
        var changes = 0;
        using var watch = Make(time, folder).Watch(Share, () => Interlocked.Increment(ref changes));
        await UntilAsync(() => folder.Asked == 1);

        await StepAsync(time, folder, TimeSpan.FromSeconds(3));
        await StepAsync(time, folder, TimeSpan.FromSeconds(6));
        folder.Stamp = Stamp;
        await StepAsync(time, folder, TimeSpan.FromSeconds(12));

        // It was never seen before, so coming back is only a first sighting, not a change.
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task WhileTheServerMayNotBeAsked_NothingIsAsked_AndPollingResumesByItself()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder();
        var allowed = false;
        using var watch = Make(time, folder, _ => Volatile.Read(ref allowed)).Watch(Share, () => { });
        await UntilAsync(() => folder.Asked == 1);

        await Task.Delay(30);
        time.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        Assert.Equal(1, folder.Asked);

        Volatile.Write(ref allowed, true);
        await StepAsync(time, folder, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Dispose_StopsThePolling()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder();
        var watch = Make(time, folder).Watch(Share, () => { });
        await UntilAsync(() => folder.Asked == 1);

        watch.Dispose();
        await Task.Delay(30);
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);

        Assert.Equal(1, folder.Asked);
    }

    [Fact]
    public void Watching_CountsTheFoldersBeingWatched_AndNotLocalOnes()
    {
        var folder = new Folder();
        var watcher = Make(new ManualTimeProvider(), folder);
        Assert.Equal(0, watcher.Watching);

        var first = watcher.Watch(Share, () => { });
        var second = watcher.Watch(Share, () => { });
        Assert.Equal(2, watcher.Watching);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, watcher.Watching);

        second.Dispose();
        Assert.Equal(0, watcher.Watching);

        new PollingFolderWatcher { IsPollable = static _ => false }.Watch(LocationPath.Parse(@"C:\data"), () => { }).Dispose();
    }

    [Fact]
    public async Task LocalFolders_AreNotPolled()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder();

        using var watch = new PollingFolderWatcher(time)
        {
            ReadStamp = folder.Read,
            IsPollable = location => location.IsNetwork,
        }.Watch(LocationPath.Parse(@"C:\data"), () => { });
        await Task.Delay(100);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, folder.Asked);
    }

    [Fact]
    public async Task AQuestionThatNeverAnswers_IsNotAskedAgainBehindItself()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var hung = new TaskCompletionSource<DateTime?>();
        using var watch = new PollingFolderWatcher(time)
        {
            Backoff = new BackoffPolicy(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60), 0),
            StatTimeout = TimeSpan.FromSeconds(5),
            ReadStamp = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return hung.Task;
            },
        }.Watch(Share, () => { });
        await UntilAsync(() => Volatile.Read(ref calls) == 1);

        // The first question times out; later waits pass without asking another while it hangs.
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(30);
            time.Advance(TimeSpan.FromSeconds(20));
        }

        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        hung.SetResult(Stamp);
    }

    [Fact]
    public async Task ARealFolder_ReportsANewFile_ThroughTheRealStat()
    {
        var root = Path.Combine(Path.GetTempPath(), "leviathan-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var changes = 0;
            using var watch = new PollingFolderWatcher
            {
                Backoff = new BackoffPolicy(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80), 0),
                IsPollable = _ => true,
            }.Watch(LocationPath.Parse(root), () => Interlocked.Increment(ref changes));
            await Task.Delay(150);

            // The folder's time moves when an item is added; file systems with coarse clocks need it to differ from the first reading.
            Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow.AddMinutes(-5));
            await Task.Delay(150);
            File.WriteAllText(Path.Combine(root, "new.txt"), "x");
            Directory.SetLastWriteTimeUtc(root, DateTime.UtcNow);

            await UntilAsync(() => Volatile.Read(ref changes) > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheCompositeWatcher_HandsEachFolderToTheWatcherMeantForIt()
    {
        var time = new ManualTimeProvider();
        var folder = new Folder();
        var polling = Make(time, folder);
        var local = new LocalFolderWatcher(time);
        using var watch = new CompositeFolderWatcher(local, polling).Watch(Share, () => { });

        await UntilAsync(() => folder.Asked == 1);
        Assert.Equal(1, folder.Asked);
    }
}
