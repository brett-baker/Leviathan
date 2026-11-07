using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Media;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class MediaInfoTrackerTests
{
    private sealed class FakeSource : IMediaInfoSource
    {
        private readonly Lock _lock = new();
        private readonly List<string> _probed = [];
        private int _running;
        private int _peak;

        public Dictionary<string, MediaInfoResult> Cached { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TaskCompletionSource? Gate { get; set; }

        public Func<MediaFileRef, int, Task<MediaInfoResult>>? Behave { get; set; }

        public int PeekCalls { get; private set; }

        public int Peak => Volatile.Read(ref _peak);

        public List<string> Probed
        {
            get
            {
                lock (_lock)
                {
                    return [.. _probed];
                }
            }
        }

        private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, MediaInfoResult>> PeekAsync(IReadOnlyList<MediaFileRef> files, CancellationToken cancellationToken)
        {
            PeekCalls++;
            var answers = files.Where(f => Cached.ContainsKey(f.Path)).ToDictionary(f => f.Path, f => Cached[f.Path], StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, MediaInfoResult>>(answers);
        }

        public async Task<MediaInfoResult> ProbeAsync(MediaFileRef file, CancellationToken cancellationToken)
        {
            int call;
            lock (_lock)
            {
                _probed.Add(file.Path);
                call = _calls[file.Path] = _calls.GetValueOrDefault(file.Path) + 1;
            }

            var now = Interlocked.Increment(ref _running);
            int peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
            }

            try
            {
                if (Gate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Behave is { } behave ? await behave(file, call) : Ok();
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private sealed class InlineUi : IUiContext
    {
        public void Post(Action action) => action();
    }

    private static MediaInfoResult Ok(int width = 1920) => new(MediaInfoState.Ok, new MediaInfo(
        "matroska", null, TimeSpan.FromMinutes(1), null, null,
        [new VideoStreamInfo(0, "h264", null, width, 1080, 24, null, null, 8, HdrKind.None, false, null, null)], [], [], [], new Dictionary<string, string>()), null);

    private static FileEntry File(string name, string folder = @"D:\v") =>
        new(name, LocationPath.Parse($@"{folder}\{name}"), FileEntryKind.File, 1000, DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, LocationPath.Parse($@"D:\v\{name}"), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    private static string Path(string name) => $@"D:\v\{name}";

    [Fact]
    public async Task SetVisible_ReadsTheCacheOnce_ThenProbesOnlyWhatIsMissing()
    {
        var source = new FakeSource();
        source.Cached[Path("a.mkv")] = Ok(1280);
        using var tracker = new MediaInfoTracker(source, new InlineUi());

        tracker.SetVisible([File("a.mkv"), File("b.mkv")]);

        Assert.True(await UntilAsync(() => tracker.TryGet(File("b.mkv")) is not null && tracker.TryGet(File("a.mkv")) is not null));
        Assert.Equal(1, source.PeekCalls);
        Assert.Equal([Path("b.mkv")], source.Probed);
        Assert.Equal(1280, tracker.TryGet(File("a.mkv"))!.Info!.PrimaryVideo!.Width);
    }

    [Fact]
    public async Task SetVisible_IgnoresFoldersAndFilesThatAreNotMedia()
    {
        var source = new FakeSource();
        using var tracker = new MediaInfoTracker(source, new InlineUi());

        tracker.SetVisible([Folder("sub"), File("notes.txt"), File("clip.mp4")]);

        Assert.True(await UntilAsync(() => tracker.TryGet(File("clip.mp4")) is not null));
        Assert.Equal([Path("clip.mp4")], source.Probed);
        Assert.Null(tracker.TryGet(File("notes.txt")));
        Assert.Null(tracker.TryGet(Folder("sub")));
    }

    [Fact]
    public async Task Probes_AreStartedRowsOnScreenFirst_AndNoMoreThanMaxParallelAtOnce()
    {
        // Every probe is held until the test lets that one file finish, so each freed slot starts exactly one more probe and the
        // order in which they start is the tracker's choice alone (starting them from the thread pool would reorder a burst).
        var holds = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
        var open = false;
        var source = new FakeSource { Behave = async (file, _) =>
        {
            if (Volatile.Read(ref open))
            {
                return Ok();
            }

            await holds.GetOrAdd(file.Path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            return Ok();
        } };
        using var tracker = new MediaInfoTracker(source, new InlineUi());
        var all = Enumerable.Range(0, 10).Select(i => File($"f{i:D2}.mkv")).ToList();
        tracker.FillFolder(all);
        Assert.True(await UntilAsync(() => source.Probed.Count == MediaInfoTracker.MaxParallel));

        // The user scrolls to the end: those rows jump the queue.
        tracker.SetVisible([all[8], all[9]]);
        var started = source.Probed;
        holds.GetOrAdd(started[0], _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
        Assert.True(await UntilAsync(() => source.Probed.Count == MediaInfoTracker.MaxParallel + 1));
        Assert.Equal(Path("f08.mkv"), source.Probed[^1]);

        holds.GetOrAdd(started[1], _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
        Assert.True(await UntilAsync(() => source.Probed.Count == MediaInfoTracker.MaxParallel + 2));
        Assert.Equal(Path("f09.mkv"), source.Probed[^1]);
        Assert.Equal(MediaInfoTracker.MaxParallel, source.Peak);

        Volatile.Write(ref open, true);
        foreach (var hold in holds.Values)
        {
            hold.TrySetResult();
        }

        Assert.True(await UntilAsync(() => all.All(f => tracker.TryGet(f) is not null)));
        Assert.Equal(MediaInfoTracker.MaxParallel, source.Peak);
    }

    [Fact]
    public async Task SetFolder_CancelsWhatIsWaiting_AndForgetsTheOldFolder()
    {
        var source = new FakeSource { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var tracker = new MediaInfoTracker(source, new InlineUi());
        tracker.SetVisible([.. Enumerable.Range(0, 8).Select(i => File($"old{i}.mkv"))]);
        Assert.True(await UntilAsync(() => source.Probed.Count == MediaInfoTracker.MaxParallel));

        tracker.SetFolder();
        source.Gate.SetResult();
        await Task.Delay(200);

        Assert.Equal(MediaInfoTracker.MaxParallel, source.Probed.Count);
        Assert.Null(tracker.TryGet(File("old0.mkv")));
        Assert.False(tracker.Progress.IsRunning);
    }

    [Fact]
    public async Task Changed_ArrivesInBatches_OnTheUiThread_NotOnePerFile()
    {
        var source = new FakeSource();
        var posts = 0;
        var changedPaths = new List<string>();
        using var tracker = new MediaInfoTracker(source, new CountingUi(() => posts++));
        tracker.Changed += (_, paths) => changedPaths.AddRange(paths);

        tracker.SetVisible([.. Enumerable.Range(0, 30).Select(i => File($"f{i:D2}.mkv"))]);

        Assert.True(await UntilAsync(() => changedPaths.Count == 30));
        Assert.True(posts < 30, $"{posts} posts for 30 files");
    }

    private sealed class CountingUi(Action posted) : IUiContext
    {
        public void Post(Action action)
        {
            posted();
            action();
        }
    }

    [Fact]
    public async Task FillFolder_ReportsProgress_UntilEveryFileHasAnAnswer()
    {
        var source = new FakeSource { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var tracker = new MediaInfoTracker(source, new InlineUi());
        var all = Enumerable.Range(0, 12).Select(i => File($"f{i:D2}.mkv")).ToList();
        var progress = new List<MediaFillProgress>();
        tracker.ProgressChanged += (_, _) =>
        {
            lock (progress)
            {
                progress.Add(tracker.Progress);
            }
        };

        tracker.FillFolder(all);

        // Held at the gate, the fill is certainly running: the progress cannot finish before it is looked at.
        Assert.True(await UntilAsync(() => tracker.Progress.IsRunning));
        source.Gate.SetResult();
        Assert.True(await UntilAsync(() => !tracker.Progress.IsRunning && tracker.Progress.Total == 12));
        Assert.Equal(new MediaFillProgress(12, 12, false), tracker.Progress);
        Assert.Equal(1.0, tracker.Progress.Fraction);
        lock (progress)
        {
            Assert.Contains(progress, p => p.IsRunning);
        }
    }

    [Fact]
    public async Task FillFolder_WithNothingToRead_HasNoProgress()
    {
        using var tracker = new MediaInfoTracker(new FakeSource(), new InlineUi());

        tracker.FillFolder([File("notes.txt"), Folder("sub")]);
        await Task.Delay(50);

        Assert.Null(tracker.Progress.Fraction);
        Assert.False(tracker.Progress.IsRunning);
    }

    [Fact]
    public async Task CancelFill_StopsTheRest_ButKeepsWhatWasRead()
    {
        var source = new FakeSource { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var tracker = new MediaInfoTracker(source, new InlineUi());
        var all = Enumerable.Range(0, 9).Select(i => File($"f{i}.mkv")).ToList();
        tracker.FillFolder(all);
        Assert.True(await UntilAsync(() => source.Probed.Count == MediaInfoTracker.MaxParallel));

        tracker.CancelFill();
        source.Gate.SetResult();
        Assert.True(await UntilAsync(() => all.Count(f => tracker.TryGet(f) is not null) == MediaInfoTracker.MaxParallel));
        await Task.Delay(150);

        Assert.Equal(MediaInfoTracker.MaxParallel, source.Probed.Count);
        Assert.False(tracker.Progress.IsRunning);
    }

    [Fact]
    public async Task AnUnreadableFile_IsShownWithItsReason_AndNotAskedAgain()
    {
        var source = new FakeSource { Behave = (_, _) => Task.FromResult(new MediaInfoResult(MediaInfoState.Unreadable, null, "moov atom not found")) };
        using var tracker = new MediaInfoTracker(source, new InlineUi());
        var file = File("bad.mp4");

        tracker.SetVisible([file]);
        Assert.True(await UntilAsync(() => tracker.TryGet(file) is not null));
        tracker.SetVisible([file]);
        await Task.Delay(100);

        Assert.Equal(MediaInfoState.Unreadable, tracker.TryGet(file)!.State);
        Assert.Equal("moov atom not found", tracker.TryGet(file)!.Message);
        Assert.Single(source.Probed);
    }

    [Fact]
    public async Task WhenTheMediaProcessIsUnavailable_TheTrackerStops_UntilTheNextFolder()
    {
        var source = new FakeSource { Behave = (_, _) => throw new MediaInfoUnavailableException("ffprobe was not found.") };
        using var tracker = new MediaInfoTracker(source, new InlineUi());

        tracker.SetVisible([.. Enumerable.Range(0, 10).Select(i => File($"f{i}.mkv"))]);

        Assert.True(await UntilAsync(() => tracker.IsUnavailable));
        await Task.Delay(100);
        var probedWhenGivingUp = source.Probed.Count;
        tracker.SetVisible([File("more.mkv")]);
        await Task.Delay(100);
        Assert.Equal(probedWhenGivingUp, source.Probed.Count);
        Assert.True(probedWhenGivingUp <= MediaInfoTracker.MaxParallel);

        source.Behave = null;
        tracker.SetFolder();
        Assert.False(tracker.IsUnavailable);
        tracker.SetVisible([File("again.mkv")]);
        Assert.True(await UntilAsync(() => tracker.TryGet(File("again.mkv")) is { State: MediaInfoState.Ok }));
    }

    [Fact]
    public async Task ATransientFailure_IsRetriedOnce_ThenGivenUp_SoNothingLoopsForEver()
    {
        var source = new FakeSource { Behave = (_, _) => throw new MediaInfoTransientException("the pipe closed") };
        using var tracker = new MediaInfoTracker(source, new InlineUi());

        tracker.SetVisible([File("a.mkv")]);
        Assert.True(await UntilAsync(() => source.Probed.Count == 2));
        await Task.Delay(200);

        Assert.Equal(2, source.Probed.Count);
        Assert.Null(tracker.TryGet(File("a.mkv")));
    }

    [Fact]
    public async Task ATransientFailureThatPasses_StillGivesAnAnswer()
    {
        var source = new FakeSource { Behave = (_, call) => call == 1 ? throw new MediaInfoTransientException("the pipe closed") : Task.FromResult(Ok()) };
        using var tracker = new MediaInfoTracker(source, new InlineUi());

        tracker.SetVisible([File("a.mkv")]);
        await UntilAsync(() => source.Probed.Count >= 1);

        // The first failure leaves the file waiting; the next time the rows are reported it is asked again and answered.
        tracker.SetVisible([File("a.mkv")]);
        Assert.True(await UntilAsync(() => tracker.TryGet(File("a.mkv")) is { State: MediaInfoState.Ok }));
    }

    [Fact]
    public async Task ATab_ForgetsTheOldFoldersMediaInformation_WhenItNavigates()
    {
        var source = new FakeSource();
        var services = new Leviathan.ViewModels.Tabs.TabServices(
            new Leviathan.Core.Providers.LocationProviderRegistry([TabTestSupport.Tree()]), new InlineUiContext(), new Leviathan.ViewModels.Views.FolderViewRules(), TimeProvider.System,
            () => TabTestSupport.P(@"C:\Home"), MediaInfo: source);
        using var tab = new Leviathan.ViewModels.Tabs.TabViewModel(services, TabTestSupport.P(@"C:\v"));
        await tab.ReloadAsync(CancellationToken.None);
        var clip = tab.Items.Rows.First(r => r.Name == "x.mkv");
        tab.Media.SetVisible([clip]);
        Assert.True(await UntilAsync(() => tab.Media.TryGet(clip) is not null));

        await tab.NavigateAsync(TabTestSupport.P(@"C:\a"), CancellationToken.None);

        Assert.Null(tab.Media.TryGet(clip));
    }

    [Fact]
    public void WithoutASource_NothingHappens()
    {
        using var tracker = new MediaInfoTracker(null, new InlineUi());

        tracker.SetVisible([File("a.mkv")]);
        tracker.FillFolder([File("a.mkv")]);

        Assert.Null(tracker.TryGet(File("a.mkv")));
        Assert.False(tracker.Progress.IsRunning);
    }

    [Theory]
    [InlineData("a.mkv", true)]
    [InlineData("a.MP4", true)]
    [InlineData("a.flac", true)]
    [InlineData("a.txt", false)]
    [InlineData("a", false)]
    public void IsMedia_GoesByExtension(string name, bool expected)
    {
        Assert.Equal(expected, MediaInfoTracker.IsMedia(File(name)));
    }
}
