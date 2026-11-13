using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Transfers;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Tests;

/// <summary>Closing triage offers to move the kept files together to the other pane (spec §10.1).</summary>
public sealed class TriageHandOffTests
{
    private static FileEntry Video(string path)
    {
        var location = LocationPath.Parse(path);
        return new FileEntry(location.Name, location, FileEntryKind.File, 5000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
    }

    private sealed class Actions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task<TriageSession> RunAsync(params (string Path, TriageDecision Decision)[] files)
    {
        var session = new TriageSession([.. files.Select(f => Video(f.Path))], new FakeVideoEngine(), new Actions(), new TriageOptions { StartOffset = 0 });
        await session.StartAsync();
        foreach (var (_, decision) in files)
        {
            switch (decision)
            {
                case TriageDecision.Kept:
                    await session.KeepAsync();
                    break;
                case TriageDecision.Discarded:
                    await session.DiscardAsync();
                    break;
                default:
                    await session.SkipAsync();
                    break;
            }
        }

        return session;
    }

    [Fact]
    public async Task Plan_ListsOnlyTheKeptFiles_ForTheOtherPane()
    {
        var session = await RunAsync((@"C:\inbox\a.mkv", TriageDecision.Kept), (@"C:\inbox\b.mkv", TriageDecision.Discarded), (@"C:\inbox\c.mkv", TriageDecision.Kept), (@"C:\inbox\d.mkv", TriageDecision.Skipped));

        var plan = TriageHandOff.Plan(session, new MediaSettings { TriageMoveWhen = TriageMoveMode.AtEnd }, @"D:\library");

        Assert.NotNull(plan);
        Assert.Equal(["a.mkv", "c.mkv"], plan.Files.Select(f => f.Name));
        Assert.Equal(@"D:\library", plan.Folder);
        Assert.True(plan.Ask);
    }

    [Fact]
    public async Task Plan_WhenMovingOnKeep_OffersNothing()
    {
        var session = await RunAsync((@"C:\inbox\a.mkv", TriageDecision.Kept));

        Assert.Null(TriageHandOff.Plan(session, new MediaSettings { TriageMoveWhen = TriageMoveMode.OnKeep }, @"D:\library"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Plan_WithNoDestination_OffersNothing(string? destination)
    {
        var session = await RunAsync((@"C:\inbox\a.mkv", TriageDecision.Kept));

        Assert.Null(TriageHandOff.Plan(session, new MediaSettings { TriageMoveWhen = TriageMoveMode.AtEnd }, destination));
    }

    [Fact]
    public async Task Plan_WithNothingKept_OffersNothing()
    {
        var session = await RunAsync((@"C:\inbox\a.mkv", TriageDecision.Discarded), (@"C:\inbox\b.mkv", TriageDecision.Skipped));

        Assert.Null(TriageHandOff.Plan(session, new MediaSettings { TriageMoveWhen = TriageMoveMode.AtEnd }, @"D:\library"));
    }

    [Fact]
    public async Task Plan_LeavesOutFilesThatAreAlreadyInTheDestination()
    {
        var session = await RunAsync((@"D:\library\a.mkv", TriageDecision.Kept), (@"C:\inbox\b.mkv", TriageDecision.Kept));

        var plan = TriageHandOff.Plan(session, new MediaSettings { TriageMoveWhen = TriageMoveMode.AtEnd }, @"d:\LIBRARY\");

        Assert.Equal(["b.mkv"], plan!.Files.Select(f => f.Name));
    }

    [Fact]
    public async Task Plan_AfterAlwaysMove_DoesNotAsk()
    {
        var session = await RunAsync((@"C:\inbox\a.mkv", TriageDecision.Kept));

        var plan = TriageHandOff.Plan(session, new MediaSettings { TriageMoveWhen = TriageMoveMode.AtEnd, TriageAskBeforeMove = false }, @"D:\library");

        Assert.False(plan!.Ask);
    }

    [Fact]
    public void MediaSettings_DefaultToMovingOnKeep_AndAskingWhenMovingAtTheEnd()
    {
        var media = new MediaSettings();

        Assert.Equal(TriageMoveMode.OnKeep, media.TriageMoveWhen);
        Assert.True(media.TriageAskBeforeMove);
    }

    // The actions: batch move and tag-only keep ---------------------------------------------------------------------------

    private sealed class Files : IUndoActions
    {
        public List<string> Log { get; } = [];

        public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken)
        {
            Log.Add(string.Join(";", moves.Select(m => $"{m.From} -> {m.To}")));
            return Task.FromResult(new StepResult(moves, []));
        }

        public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => Task.FromResult(new StepResult([], []));
    }

    private sealed class Tags : ITagStore
    {
        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Transfers : ITransferService
    {
        public List<TransferRequest> Requests { get; } = [];

        public TransferOutcome Outcome { get; set; } = new(true, "queued");

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Outcome);
        }
    }

    [Fact]
    public async Task MoveKept_OnTheSameDrive_RenamesEveryFileInOneCall()
    {
        var files = new Files();
        var actions = new TriageFileActions(files, new Tags(), () => new TriageKeepOptions(null), new Transfers());

        var result = await actions.MoveKeptAsync([Video(@"C:\inbox\a.mkv"), Video(@"C:\inbox\b.mkv")], @"C:\library", CancellationToken.None);

        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.Queued);
        Assert.Equal([@"C:\inbox\a.mkv -> C:\library\a.mkv;C:\inbox\b.mkv -> C:\library\b.mkv"], files.Log);
    }

    [Fact]
    public async Task MoveKept_OnAnotherDrive_QueuesOneMoveJobForAll()
    {
        var transfers = new Transfers();
        var actions = new TriageFileActions(new Files(), new Tags(), () => new TriageKeepOptions(null), transfers);

        var result = await actions.MoveKeptAsync([Video(@"C:\inbox\a.mkv"), Video(@"C:\inbox\b.mkv")], @"\\nas\library", CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(2, result.Queued);
        var request = Assert.Single(transfers.Requests);
        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.Equal(2, request.Sources.Count);
        Assert.Equal(@"\\nas\library", request.Destination.Value);
    }

    [Fact]
    public async Task MoveKept_WithFilesOnBothKindsOfDrive_RenamesTheNearOnesAndQueuesTheRest()
    {
        var files = new Files();
        var transfers = new Transfers();
        var actions = new TriageFileActions(files, new Tags(), () => new TriageKeepOptions(null), transfers);

        var result = await actions.MoveKeptAsync([Video(@"D:\inbox\a.mkv"), Video(@"C:\inbox\b.mkv")], @"D:\library", CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Queued);
    }

    [Fact]
    public async Task MoveKept_WhenTheQueueRefuses_SaysSo_AndTheFileStaysPut()
    {
        var transfers = new Transfers { Outcome = new TransferOutcome(false, "The worker is not running.") };
        var actions = new TriageFileActions(new Files(), new Tags(), () => new TriageKeepOptions(null), transfers);

        var result = await actions.MoveKeptAsync([Video(@"C:\inbox\a.mkv")], @"\\nas\library", CancellationToken.None);

        Assert.Equal(0, result.Queued);
        Assert.Contains("The worker is not running.", result.Describe(@"\\nas\library"));
    }

    [Fact]
    public async Task MoveKept_SkipsAFileThatIsAlreadyThere()
    {
        var files = new Files();
        var actions = new TriageFileActions(files, new Tags(), () => new TriageKeepOptions(null), null);

        var result = await actions.MoveKeptAsync([Video(@"C:\library\a.mkv")], @"C:\library", CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Empty(files.Log);
    }

    [Fact]
    public async Task Keep_WhenMovingAtTheEnd_OnlyTags_EvenWithAFolder()
    {
        var files = new Files();
        var actions = new TriageFileActions(files, new Tags(), () => new TriageKeepOptions(@"C:\library", MoveOnKeep: false), new Transfers());

        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Null(result.Moved);
        Assert.Empty(files.Log);
    }

    [Fact]
    public void Describe_SaysWhatHappened()
    {
        Assert.Equal("Moved 3 kept files to library", new TriageMoveResult(3, 0, []).Describe(@"D:\library"));
        Assert.Equal("Moving 1 kept file to library in the queue", new TriageMoveResult(0, 1, []).Describe(@"D:\library\"));
        Assert.Equal("No kept files to move.", new TriageMoveResult(0, 0, []).Describe(@"D:\library"));
        Assert.Contains("(and 1 more problem)", new TriageMoveResult(0, 0, ["a", "b"]).Describe(@"D:\library"));
    }
}
