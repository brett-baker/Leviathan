using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Media;
using Leviathan.Testing;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

public sealed class TriageOverlayTextTests
{
    private static FileEntry File(string path, long size = 1_500_000_000)
    {
        var location = LocationPath.Parse(path);
        return new FileEntry(location.Name, location, FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
    }

    private static MediaInfo Info(int? width = 3840, int? height = 2160, string? codec = "hevc") => new(
        "matroska", "Matroska", TimeSpan.FromMinutes(102), null, null,
        [new VideoStreamInfo(0, codec, null, width, height, 23.976, null, null, null, HdrKind.None, false, null, null)], [], [], [], new Dictionary<string, string>());

    [Fact]
    public void Describe_WithEverythingKnown_ListsSizeDurationResolutionCodecAndFolder()
    {
        var text = TriageOverlayText.Describe(File(@"\\nas\inbox\a.mkv"), TimeSpan.FromMinutes(102), Info());

        Assert.Equal("a.mkv", text.Title);
        Assert.Contains("3840×2160", text.Detail);
        Assert.Contains("HEVC", text.Detail);
        Assert.Contains("1:42:00", text.Detail);
        Assert.EndsWith(@"in \\nas\inbox", text.Detail);
    }

    [Fact]
    public void Describe_BeforeTheProbeArrives_SaysWhatItKnows()
    {
        var text = TriageOverlayText.Describe(File(@"C:\inbox\a.mkv"), null, null);

        Assert.DoesNotContain("×", text.Detail);
        Assert.Contains(@"in C:\inbox", text.Detail);
    }

    [Fact]
    public void Describe_TakesTheDurationFromTheProbe_WhenThePlayerDoesNotKnowItYet() =>
        Assert.Contains("1:42:00", TriageOverlayText.Describe(File(@"C:\inbox\a.mkv"), TimeSpan.Zero, Info()).Detail);

    [Fact]
    public void Describe_LeavesOutAResolutionOrCodecTheProbeDidNotFind()
    {
        var text = TriageOverlayText.Describe(File(@"C:\inbox\a.mkv"), null, Info(width: null, height: null, codec: " "));

        Assert.DoesNotContain("×", text.Detail);
        Assert.DoesNotContain("HEVC", text.Detail);
    }
}

public sealed class TriageFromHereTests
{
    private static FileEntry File(string name)
    {
        var location = LocationPath.Parse($@"C:\inbox\{name}");
        return new FileEntry(name, location, FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
    }

    private static readonly FileEntry[] Rows = [File("a.mkv"), File("notes.txt"), File("b.mp4"), File("c.avi"), File("d.mkv")];

    [Fact]
    public void WithNothingSelected_EveryVideoOfTheViewFromTheTop()
    {
        var (queue, start) = TriageQueue.FromHere(Rows, [], new TriageOptions());

        Assert.Equal(["a.mkv", "b.mp4", "c.avi", "d.mkv"], queue.Select(f => f.Name));
        Assert.Equal(0, start);
    }

    [Fact]
    public void WithOneVideoSelected_TheWholeViewStartingAtThatVideo()
    {
        var (queue, start) = TriageQueue.FromHere(Rows, [Rows[3]], new TriageOptions());

        Assert.Equal(4, queue.Count);
        Assert.Equal("c.avi", queue[start].Name);
    }

    [Fact]
    public void WithSeveralVideosSelected_TheSelection_InTheOrderOfTheView()
    {
        var (queue, start) = TriageQueue.FromHere(Rows, [Rows[4], Rows[0], Rows[2]], new TriageOptions());

        Assert.Equal(["a.mkv", "b.mp4", "d.mkv"], queue.Select(f => f.Name));
        Assert.Equal(0, start);
    }

    [Fact]
    public void WithOnlyANonVideoSelected_TheWholeViewFromTheTop()
    {
        var (queue, start) = TriageQueue.FromHere(Rows, [Rows[1]], new TriageOptions());

        Assert.Equal(4, queue.Count);
        Assert.Equal(0, start);
    }

    [Fact]
    public void WithTheSelectedVideoFilteredOut_StartsAtTheTop()
    {
        var (queue, start) = TriageQueue.FromHere(Rows, [Rows[2]], new TriageOptions { UntriagedOnly = true }, f => f.Name == "b.mp4");

        Assert.DoesNotContain(queue, f => f.Name == "b.mp4");
        Assert.Equal(0, start);
    }
}

public sealed class TriageSessionWindowSupportTests
{
    private static FileEntry Video(string name, long size = 100)
    {
        var location = LocationPath.Parse($@"C:\inbox\{name}");
        return new FileEntry(name, location, FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
    }

    private sealed class Actions : ITriageActions
    {
        public List<string> Log { get; } = [];

        public FileEntry? MoveTo { get; set; }

        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken)
        {
            Log.Add("keep " + file.Name);
            return Task.FromResult(new TriageActionResult(() =>
            {
                Log.Add("unkeep " + file.Name);
                return Task.CompletedTask;
            }, MoveTo));
        }

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken)
        {
            Log.Add("discard " + file.Name);
            return Task.FromResult(new TriageActionResult(() =>
            {
                Log.Add("restore " + file.Name);
                return Task.CompletedTask;
            }));
        }

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<(TriageSession Session, FakeVideoEngine Engine, Actions Actions)> StartAsync(int files = 4, int startIndex = 0)
    {
        var engine = new FakeVideoEngine();
        var actions = new Actions();
        var session = new TriageSession([.. Enumerable.Range(1, files).Select(i => Video($"v{i}.mkv", i * 100))], engine, actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync(startIndex);
        return (session, engine, actions);
    }

    [Fact]
    public async Task Start_AtAnIndex_OpensThatFile()
    {
        var (session, engine, _) = await StartAsync(startIndex: 2);

        Assert.Equal(2, session.Index);
        Assert.Equal(@"C:\inbox\v3.mkv", engine.OpenPath);
    }

    [Fact]
    public async Task Start_AtAnIndexPastTheEnd_OpensTheLastFile()
    {
        var (session, _, _) = await StartAsync(files: 3, startIndex: 99);

        Assert.Equal(2, session.Index);
    }

    [Fact]
    public async Task GoTo_OpensTheFileClickedInTheQueue()
    {
        var (session, engine, _) = await StartAsync();

        await session.GoToAsync(3);

        Assert.Equal(3, session.Index);
        Assert.Equal(@"C:\inbox\v4.mkv", engine.OpenPath);
    }

    [Fact]
    public async Task GoTo_TheFileAlreadyShowing_LeavesItPlaying()
    {
        var (session, engine, _) = await StartAsync();
        await session.Keys.JumpAsync(TimeSpan.FromSeconds(30));

        await session.GoToAsync(0);

        Assert.Equal(TimeSpan.FromSeconds(30), engine.Snapshot.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public async Task GoTo_OutsideTheQueue_DoesNothing(int index)
    {
        var (session, _, _) = await StartAsync();

        await session.GoToAsync(index);

        Assert.Equal(0, session.Index);
    }

    [Fact]
    public async Task GoTo_AfterTheEnd_ReturnsToTheQueue()
    {
        var (session, _, _) = await StartAsync(files: 1);
        await session.SkipAsync();
        Assert.True(session.IsFinished);

        await session.GoToAsync(0);

        Assert.False(session.IsFinished);
    }

    [Fact]
    public async Task Restore_PutsADiscardedFileBack_AndCountsItAsUndecided()
    {
        var (session, _, actions) = await StartAsync(files: 2);
        await session.DiscardAsync();
        await session.KeepAsync();
        Assert.True(session.IsFinished);
        var discarded = session.Items[0];

        await session.RestoreAsync(discarded);

        Assert.Equal(["discard v1.mkv", "keep v2.mkv", "restore v1.mkv"], actions.Log);
        Assert.Equal(TriageDecision.None, discarded.Decision);
        Assert.Equal(0, session.Discarded);
        Assert.Equal(0, session.ReclaimedBytes);
        Assert.Equal(1, session.Summary().Undecided);
    }

    [Fact]
    public async Task Restore_OfAKeptFileThatWasMoved_PutsTheOriginalPlaceBack()
    {
        var (session, _, actions) = await StartAsync(files: 1);
        actions.MoveTo = Video("v1.mkv") with { Location = LocationPath.Parse(@"D:\keep\v1.mkv") };
        await session.KeepAsync();
        Assert.Equal(@"D:\keep\v1.mkv", session.Items[0].File.Location.Value);

        await session.RestoreAsync(session.Items[0]);

        Assert.Equal(@"C:\inbox\v1.mkv", session.Items[0].File.Location.Value);
    }

    [Fact]
    public async Task Restore_DropsThatFilesUndoHistory_SoCtrlZDoesNotUndoItTwice()
    {
        var (session, _, actions) = await StartAsync(files: 3);
        await session.DiscardAsync();
        await session.KeepAsync();

        await session.RestoreAsync(session.Items[0]);
        actions.Log.Clear();
        await session.UndoAsync();

        Assert.Equal(["unkeep v2.mkv"], actions.Log);
    }

    [Fact]
    public async Task Restore_OfAnUndecidedOrSkippedFile_DoesNothing()
    {
        var (session, _, actions) = await StartAsync();
        await session.SkipAsync();

        await session.RestoreAsync(session.Items[0]);
        await session.RestoreAsync(session.Items[3]);

        Assert.Empty(actions.Log);
        Assert.Equal(TriageDecision.Skipped, session.Items[0].Decision);
    }
}
