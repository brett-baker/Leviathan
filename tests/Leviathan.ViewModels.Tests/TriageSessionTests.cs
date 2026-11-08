using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

public sealed class TriageSessionTests
{
    private static FileEntry Video(string name, long size = 1000) =>
        new(name, LocationPath.Parse($@"C:\inbox\{name}"), FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private sealed class FakeActions(FakeVideoEngine engine) : ITriageActions
    {
        public List<string> Log { get; } = [];

        public Exception? FailKeep { get; set; }

        public Exception? FailDiscard { get; set; }

        public Exception? FailUndo { get; set; }

        public FileEntry? MoveTo { get; set; }

        public bool EngineOpenWhenActing { get; private set; }

        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken)
        {
            EngineOpenWhenActing = engine.Snapshot.IsOpen;
            Log.Add($"keep {file.Name}");
            if (FailKeep is not null)
            {
                throw FailKeep;
            }

            return Task.FromResult(new TriageActionResult(() => Undo($"unkeep {file.Name}"), MoveTo));
        }

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken)
        {
            EngineOpenWhenActing = engine.Snapshot.IsOpen;
            Log.Add($"discard {file.Name}");
            if (FailDiscard is not null)
            {
                throw FailDiscard;
            }

            return Task.FromResult(new TriageActionResult(() => Undo($"restore {file.Name}")));
        }

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken)
        {
            Log.Add($"tag {tagNumber} {file.Name}");
            var on = Log.Count(l => l == $"tag {tagNumber} {file.Name}") % 2 == 1;
            return Task.FromResult(on);
        }

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken)
        {
            Log.Add($"cleartags {file.Name}");
            return Task.CompletedTask;
        }

        private Task Undo(string what)
        {
            Log.Add(what);
            return FailUndo is null ? Task.CompletedTask : Task.FromException(FailUndo);
        }
    }

    private sealed class Rig
    {
        public Rig(int files = 3, TriageOptions? options = null, TimeProvider? time = null)
        {
            Engine = new FakeVideoEngine();
            Actions = new FakeActions(Engine);
            Files = [.. Enumerable.Range(1, files).Select(i => Video($"v{i}.mkv", i * 100))];
            Session = new TriageSession(Files, Engine, Actions, options ?? new TriageOptions { StartOffset = 0 }, time);
        }

        public FakeVideoEngine Engine { get; }

        public FakeActions Actions { get; }

        public List<FileEntry> Files { get; }

        public TriageSession Session { get; }

        public string? Open => Engine.OpenPath;
    }

    [Fact]
    public async Task Start_OpensTheFirstFile_Playing()
    {
        var rig = new Rig();

        await rig.Session.StartAsync();

        Assert.Equal(@"C:\inbox\v1.mkv", rig.Open);
        Assert.Equal(PlaybackState.Playing, rig.Engine.Snapshot.State);
        Assert.Equal("v1.mkv", rig.Session.Current!.File.Name);
        Assert.Equal(0, rig.Session.Index);
    }

    [Fact]
    public async Task Start_AppliesTheStartOffset_ToLongFiles()
    {
        var rig = new Rig(options: new TriageOptions { StartOffset = 0.1 });
        rig.Engine.FileDuration = TimeSpan.FromMinutes(10);

        await rig.Session.StartAsync();

        Assert.Equal(TimeSpan.FromMinutes(1), rig.Engine.Snapshot.Position);
    }

    [Fact]
    public async Task Start_DoesNotSkipTheIntroOfAShortClip()
    {
        var rig = new Rig(options: new TriageOptions { StartOffset = 0.1 });
        rig.Engine.FileDuration = TimeSpan.FromSeconds(20);

        await rig.Session.StartAsync();

        Assert.Equal(TimeSpan.Zero, rig.Engine.Snapshot.Position);
    }

    [Fact]
    public async Task Start_OfAnEmptyQueue_IsFinishedAtOnce()
    {
        var rig = new Rig(files: 0);
        var finished = 0;
        rig.Session.Finished += (_, _) => finished++;

        await rig.Session.StartAsync();

        Assert.True(rig.Session.IsFinished);
        Assert.Null(rig.Session.Current);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task Start_Twice_OpensOnce()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();

        await rig.Session.StartAsync();

        Assert.Equal(1, rig.Session.Index);
    }

    [Fact]
    public async Task Keep_ClosesThePlayerFirst_KeepsTheFile_AndOpensTheNext()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.KeepAsync();

        Assert.False(rig.Actions.EngineOpenWhenActing);
        Assert.Equal(["keep v1.mkv"], rig.Actions.Log);
        Assert.Equal(TriageDecision.Kept, rig.Session.Items[0].Decision);
        Assert.Equal(@"C:\inbox\v2.mkv", rig.Open);
        Assert.Equal(1, rig.Session.Kept);
    }

    [Fact]
    public async Task Discard_CountsTheSpaceReclaimed()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.DiscardAsync();
        await rig.Session.DiscardAsync();

        Assert.Equal(300, rig.Session.ReclaimedBytes);
        Assert.Equal(2, rig.Session.Discarded);
        Assert.Contains("reclaimed", rig.Session.ReclaimedText);
    }

    [Fact]
    public async Task Skip_MovesOn_WithoutTouchingTheFile()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.SkipAsync();

        Assert.Empty(rig.Actions.Log);
        Assert.Equal(TriageDecision.Skipped, rig.Session.Items[0].Decision);
        Assert.Equal(1, rig.Session.Skipped);
        Assert.Equal(1, rig.Session.Index);
    }

    [Fact]
    public async Task Skip_OfAFileAlreadyKept_LeavesItKept()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();
        await rig.Session.PreviousAsync();

        await rig.Session.SkipAsync();

        Assert.Equal(TriageDecision.Kept, rig.Session.Items[0].Decision);
        Assert.Equal(1, rig.Session.Index);
    }

    [Fact]
    public async Task AfterTheLastFile_TheQueueIsFinished_AndTheSummaryTellsWhatHappened()
    {
        var rig = new Rig();
        var finished = 0;
        rig.Session.Finished += (_, _) => finished++;
        await rig.Session.StartAsync();

        await rig.Session.KeepAsync();
        await rig.Session.DiscardAsync();
        await rig.Session.SkipAsync();

        Assert.True(rig.Session.IsFinished);
        Assert.Null(rig.Session.Current);
        Assert.Equal(1, finished);
        var summary = rig.Session.Summary();
        Assert.Equal((1, 1, 1, 0, 200L), (summary.Kept, summary.Discarded, summary.Skipped, summary.Undecided, summary.ReclaimedBytes));
        Assert.Equal(["v2.mkv"], summary.DiscardedFiles.Select(f => f.Name));
        Assert.False(rig.Engine.Snapshot.IsOpen);
    }

    [Fact]
    public async Task ProgressText_ReadsLikeTheSpec()
    {
        var rig = new Rig(files: 4);
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();
        await rig.Session.DiscardAsync();

        Assert.Equal("3 / 4 · 1 kept · 1 discarded · 0 skipped", rig.Session.ProgressText);
    }

    [Fact]
    public async Task AutoAdvance_WithADelay_WaitsForTheClock()
    {
        var time = new ManualTimeProvider();
        var rig = new Rig(options: new TriageOptions { StartOffset = 0, AutoAdvanceDelay = TimeSpan.FromMilliseconds(500) }, time: time);
        await rig.Session.StartAsync();

        var pending = rig.Session.KeepAsync();
        await pending;
        Assert.Equal(0, rig.Session.Index);

        time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Session.Index);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitAsync(() => rig.Session.Index == 1);
        Assert.Equal(@"C:\inbox\v2.mkv", rig.Open);
    }

    [Fact]
    public async Task ADecisionDuringTheDelay_MovesOnFirst_SoItNeverDecidesTheSameFileTwice()
    {
        var time = new ManualTimeProvider();
        var rig = new Rig(options: new TriageOptions { StartOffset = 0, AutoAdvanceDelay = TimeSpan.FromSeconds(1) }, time: time);
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();

        await rig.Session.DiscardAsync();

        Assert.Equal(["keep v1.mkv", "discard v2.mkv"], rig.Actions.Log);
        Assert.Equal(TriageDecision.Kept, rig.Session.Items[0].Decision);
        Assert.Equal(TriageDecision.Discarded, rig.Session.Items[1].Decision);
    }

    [Fact]
    public async Task AutoAdvance_Off_StaysOnTheFile()
    {
        var rig = new Rig(options: new TriageOptions { StartOffset = 0, AutoAdvanceDelay = null });
        await rig.Session.StartAsync();

        await rig.Session.KeepAsync();

        Assert.Equal(0, rig.Session.Index);
        Assert.Equal(TriageDecision.Kept, rig.Session.Current!.Decision);
    }

    [Fact]
    public async Task Undo_TakesTheDecisionBack_AndReturnsToThatFile()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.DiscardAsync();
        await rig.Session.KeepAsync();

        await rig.Session.UndoAsync();

        Assert.Equal(["discard v1.mkv", "keep v2.mkv", "unkeep v2.mkv"], rig.Actions.Log);
        Assert.Equal(TriageDecision.None, rig.Session.Items[1].Decision);
        Assert.Equal(1, rig.Session.Index);
        Assert.Equal(@"C:\inbox\v2.mkv", rig.Open);
        Assert.Equal(1, rig.Session.Discarded);
        Assert.Equal(0, rig.Session.Kept);
    }

    [Fact]
    public async Task Undo_OfADiscard_RestoresTheFile_AndPlaysItAgain()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.DiscardAsync();

        await rig.Session.UndoAsync();

        Assert.Equal("restore v1.mkv", rig.Actions.Log[^1]);
        Assert.Equal(@"C:\inbox\v1.mkv", rig.Open);
        Assert.True(rig.Engine.Snapshot.IsOpen);
        Assert.Equal(0, rig.Session.ReclaimedBytes);
    }

    [Fact]
    public async Task Undo_WalksBackThroughSeveralDecisions()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();
        await rig.Session.SkipAsync();

        await rig.Session.UndoAsync();
        await rig.Session.UndoAsync();

        Assert.Equal(0, rig.Session.Index);
        Assert.All(rig.Session.Items, i => Assert.Equal(TriageDecision.None, i.Decision));
    }

    [Fact]
    public async Task Undo_WithNothingToUndo_DoesNothing()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.UndoAsync();

        Assert.Equal(0, rig.Session.Index);
        Assert.Empty(rig.Actions.Log);
    }

    [Fact]
    public async Task Undo_AfterTheQueueEnded_ReturnsToTheLastFile()
    {
        var rig = new Rig(files: 1);
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();
        Assert.True(rig.Session.IsFinished);

        await rig.Session.UndoAsync();

        Assert.False(rig.Session.IsFinished);
        Assert.Equal("v1.mkv", rig.Session.Current!.File.Name);
    }

    [Fact]
    public async Task Undo_WhenTheRestoreFails_KeepsTheDecision_AndSaysWhy()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.DiscardAsync();
        rig.Actions.FailUndo = new IOException("recycle area is gone");

        await rig.Session.UndoAsync();

        Assert.Equal(TriageDecision.Discarded, rig.Session.Items[0].Decision);
        Assert.Contains("recycle area is gone", rig.Session.LastError);
        Assert.Equal(1, rig.Session.Index);
    }

    [Fact]
    public async Task Previous_GoesBack_AndPlaysTheFile()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();

        await rig.Session.PreviousAsync();

        Assert.Equal(0, rig.Session.Index);
        Assert.Equal(@"C:\inbox\v1.mkv", rig.Open);
    }

    [Fact]
    public async Task Previous_OnTheFirstFile_StaysPut()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.PreviousAsync();

        Assert.Equal(0, rig.Session.Index);
    }

    [Fact]
    public async Task Previous_ToADiscardedFile_ShowsItWithoutPlaying()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.DiscardAsync();

        await rig.Session.PreviousAsync();

        Assert.Equal(0, rig.Session.Index);
        Assert.False(rig.Engine.Snapshot.IsOpen);
        Assert.False(rig.Session.Current!.IsPlayable);
    }

    [Fact]
    public async Task Previous_AfterTheEnd_GoesToTheLastFile()
    {
        var rig = new Rig(files: 2);
        await rig.Session.StartAsync();
        await rig.Session.SkipAsync();
        await rig.Session.SkipAsync();

        await rig.Session.PreviousAsync();

        Assert.False(rig.Session.IsFinished);
        Assert.Equal(1, rig.Session.Index);
    }

    [Fact]
    public async Task DecidingAgain_TakesTheOldDecisionBackFirst()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        await rig.Session.KeepAsync();
        await rig.Session.PreviousAsync();

        await rig.Session.DiscardAsync();

        Assert.Equal(["keep v1.mkv", "unkeep v1.mkv", "discard v1.mkv"], rig.Actions.Log);
        Assert.Equal(TriageDecision.Discarded, rig.Session.Items[0].Decision);
        Assert.Equal(0, rig.Session.Kept);
    }

    [Fact]
    public async Task AFailedKeep_StaysOnTheFile_ShowsWhy_AndPlaysItAgain()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        rig.Actions.FailKeep = new IOException("share is read-only");

        await rig.Session.KeepAsync();

        Assert.Equal(0, rig.Session.Index);
        Assert.Equal(TriageDecision.None, rig.Session.Current!.Decision);
        Assert.Contains("share is read-only", rig.Session.LastError);
        Assert.True(rig.Engine.Snapshot.IsOpen);
        Assert.Equal(@"C:\inbox\v1.mkv", rig.Open);
    }

    [Fact]
    public async Task AFailedDiscard_DoesNotCountAsADecision()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        rig.Actions.FailDiscard = new UnauthorizedAccessException("denied");

        await rig.Session.DiscardAsync();

        Assert.Equal(0, rig.Session.Discarded);
        Assert.Equal(0, rig.Session.ReclaimedBytes);
        await rig.Session.UndoAsync(); // nothing was recorded, so there is nothing to undo
        Assert.Equal(0, rig.Session.Index);
    }

    [Fact]
    public async Task ASuccessfulDecision_ClearsTheError()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();
        rig.Actions.FailKeep = new IOException("nope");
        await rig.Session.KeepAsync();
        rig.Actions.FailKeep = null;

        await rig.Session.KeepAsync();

        Assert.Null(rig.Session.LastError);
    }

    [Fact]
    public async Task KeepWithAMove_FollowsTheFileToItsNewPlace()
    {
        var rig = new Rig();
        var moved = Video("v1.mkv") with { Location = LocationPath.Parse(@"D:\keep\v1.mkv") };
        rig.Actions.MoveTo = moved;
        await rig.Session.StartAsync();

        await rig.Session.KeepAsync();

        Assert.Equal(@"D:\keep\v1.mkv", rig.Session.Items[0].File.Location.Value);
        await rig.Session.UndoAsync();
        Assert.Equal(@"C:\inbox\v1.mkv", rig.Session.Items[0].File.Location.Value);
    }

    [Fact]
    public async Task ATagKey_TogglesTheTag_WithoutAdvancing()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.ToggleTagAsync(3);
        Assert.Contains(3, rig.Session.Current!.Tags);
        await rig.Session.ToggleTagAsync(3);

        Assert.DoesNotContain(3, rig.Session.Current!.Tags);
        Assert.Equal(0, rig.Session.Index);
        Assert.Equal(@"C:\inbox\v1.mkv", rig.Open);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task ATagKeyOutsideOneToNine_Throws(int number)
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => rig.Session.ToggleTagAsync(number));
    }

    [Fact]
    public async Task AFileThatWillNotPlay_IsReported_AndCanBeSkipped()
    {
        var rig = new Rig();
        rig.Engine.Refuse[@"C:\inbox\v1.mkv"] = "Unsupported codec";

        await rig.Session.StartAsync();

        Assert.Contains("Unsupported codec", rig.Session.LastError);
        await rig.Session.SkipAsync();
        Assert.Equal(1, rig.Session.Index);
        Assert.Equal(@"C:\inbox\v2.mkv", rig.Open);
    }

    [Fact]
    public async Task Upcoming_ListsTheNextPlayableFiles()
    {
        var rig = new Rig(files: 5);
        await rig.Session.StartAsync();

        Assert.Equal(["v2.mkv", "v3.mkv"], rig.Session.Upcoming(2).Select(f => f.Name));
        await rig.Session.DiscardAsync();
        await rig.Session.PreviousAsync();
        Assert.Equal(["v2.mkv", "v3.mkv", "v4.mkv"], rig.Session.Upcoming(3).Select(f => f.Name));
    }

    [Fact]
    public async Task Keys_ArePlaybackKeysOverTheSamePlayer()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.Keys.JumpAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), rig.Engine.Snapshot.Position);
    }

    [Fact]
    public async Task Dispose_ClosesThePlayersFile()
    {
        var rig = new Rig();
        await rig.Session.StartAsync();

        await rig.Session.DisposeAsync();

        Assert.False(rig.Engine.Snapshot.IsOpen);
    }

    [Fact]
    public async Task RapidDecisions_AreQueuedNotInterleaved()
    {
        var rig = new Rig(files: 6);
        await rig.Session.StartAsync();

        await Task.WhenAll(rig.Session.KeepAsync(), rig.Session.KeepAsync(), rig.Session.DiscardAsync());

        Assert.Equal(3, rig.Actions.Log.Count);
        Assert.Equal(3, rig.Session.Items.Count(i => i.Decision != TriageDecision.None));
        Assert.Equal(3, rig.Session.Index);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}

public sealed class TriageQueueTests
{
    private static FileEntry File(string name, long size = 10) =>
        new(name, LocationPath.Parse($@"C:\inbox\{name}"), FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Fact]
    public void FromSelection_KeepsOnlyVideos_InTheOrderGiven()
    {
        var queue = TriageQueue.FromSelection([File("b.mkv"), File("notes.txt"), File("a.mp4"), File("pic.jpg")], new TriageOptions());

        Assert.Equal(["b.mkv", "a.mp4"], queue.Select(f => f.Name));
    }

    [Fact]
    public void FromSelection_CanLeaveOutWhatIsAlreadyTriaged()
    {
        var queue = TriageQueue.FromSelection([File("a.mkv"), File("b.mkv"), File("c.mkv")], new TriageOptions { UntriagedOnly = true }, f => f.Name == "b.mkv");

        Assert.Equal(["a.mkv", "c.mkv"], queue.Select(f => f.Name));
    }

    [Fact]
    public void FromSelection_IgnoresTheTriagedFilter_WhenNotAsked()
    {
        var queue = TriageQueue.FromSelection([File("a.mkv"), File("b.mkv")], new TriageOptions(), _ => true);

        Assert.Equal(2, queue.Count);
    }

    private static FakeLocationProvider Library() => new FakeLocationProvider()
        .AddFile(@"C:\inbox\b.mkv", 20)
        .AddFile(@"C:\inbox\a.mp4", 10)
        .AddFile(@"C:\inbox\readme.txt", 1)
        .AddFile(@"C:\inbox\sub\c.avi", 30)
        .AddFile(@"C:\inbox\sub\deeper\d.mkv", 40)
        .AddLink(@"C:\inbox\loop");

    [Fact]
    public async Task FromFolder_NotRecursive_TakesOnlyTheVideosHere_ByName()
    {
        var queue = await TriageQueue.FromFolderAsync(Library(), LocationPath.Parse(@"C:\inbox"), new TriageOptions(), null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["a.mp4", "b.mkv"], queue.Select(f => f.Name));
    }

    [Fact]
    public async Task FromFolder_Recursive_TakesTheVideosBelowToo_AndIgnoresLinks()
    {
        var queue = await TriageQueue.FromFolderAsync(Library(), LocationPath.Parse(@"C:\inbox"), new TriageOptions { Recursive = true }, null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["a.mp4", "b.mkv", "c.avi", "d.mkv"], queue.Select(f => f.Name));
    }

    [Fact]
    public async Task FromFolder_CanBeSortedBySize_Largest_First()
    {
        var bySize = Comparer<FileEntry>.Create((x, y) => y.Size.CompareTo(x.Size));

        var queue = await TriageQueue.FromFolderAsync(Library(), LocationPath.Parse(@"C:\inbox"), new TriageOptions { Recursive = true }, null, bySize, null, TestContext.Current.CancellationToken);

        Assert.Equal(["d.mkv", "c.avi", "b.mkv", "a.mp4"], queue.Select(f => f.Name));
    }

    [Fact]
    public async Task FromFolder_UntriagedOnly_SkipsDecidedFiles()
    {
        var queue = await TriageQueue.FromFolderAsync(Library(), LocationPath.Parse(@"C:\inbox"), new TriageOptions { UntriagedOnly = true }, f => f.Name == "a.mp4", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["b.mkv"], queue.Select(f => f.Name));
    }

    [Fact]
    public async Task FromFolder_AnUnreadableSubfolder_IsSkipped_AndReported()
    {
        var faulty = new FaultInjectingLocationProvider(Library()).Inject(@"C:\inbox\sub", new Fault.AccessDenied());
        var skipped = new List<string>();

        var queue = await TriageQueue.FromFolderAsync(faulty, LocationPath.Parse(@"C:\inbox"), new TriageOptions { Recursive = true }, null, null, (p, _) => skipped.Add(p.Value), TestContext.Current.CancellationToken);

        Assert.Equal(["a.mp4", "b.mkv"], queue.Select(f => f.Name));
        Assert.Equal([@"C:\inbox\sub"], skipped);
    }

    [Fact]
    public async Task FromFolder_TheChosenFolderFailing_ThrowsToTheCaller()
    {
        var faulty = new FaultInjectingLocationProvider(Library()).Inject(@"C:\inbox", new Fault.AccessDenied());

        await Assert.ThrowsAnyAsync<Exception>(() => TriageQueue.FromFolderAsync(faulty, LocationPath.Parse(@"C:\inbox"), new TriageOptions(), null, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FromFolder_Cancelled_StopsWalking()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TriageQueue.FromFolderAsync(Library(), LocationPath.Parse(@"C:\inbox"), new TriageOptions { Recursive = true }, null, null, null, cts.Token));
    }
}
