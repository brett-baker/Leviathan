using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Transfers;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Tests;

public sealed class TriageFileActionsTests
{
    private static FileEntry Video(string path)
    {
        var location = LocationPath.Parse(path);
        return new FileEntry(location.Name, location, FileEntryKind.File, 5000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
    }

    private sealed class FakeTags : ITagStore
    {
        public Dictionary<string, HashSet<int>> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Writes { get; } = [];

        public Exception? FailWith { get; set; }

        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(Tags.TryGetValue(path, out var t) ? new HashSet<int>(t) : new HashSet<int>());

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                return Task.FromException(FailWith);
            }

            Writes.Add($"{path} = [{string.Join(',', tags.Order())}]");
            Tags[path] = [.. tags];
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFiles : IUndoActions
    {
        public List<string> Log { get; } = [];

        public string[] Problems { get; set; } = [];

        public bool Empty { get; set; }

        public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken)
        {
            Log.Add("move " + string.Join(";", moves.Select(m => $"{m.From} -> {m.To}")));
            return Task.FromResult(Empty ? new StepResult([], Problems) : new StepResult(moves, Problems));
        }

        public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            Log.Add("delete " + string.Join(";", paths));
            return Task.FromResult(Empty ? new StepResult([], Problems) : new StepResult([.. paths.Select(p => new ItemMove(p, @"C:\$Recycle.Bin\S-1\$R" + Path.GetFileName(p)))], Problems));
        }
    }

    private sealed class FakeTransfers : ITransferService
    {
        public List<TransferRequest> Requests { get; } = [];

        public TransferOutcome Outcome { get; set; } = new(true, "queued");

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Outcome);
        }
    }

    private static (TriageFileActions Actions, FakeFiles Files, FakeTags Tags, FakeTransfers Transfers) Make(string? keepFolder = null, bool withTransfers = true)
    {
        var files = new FakeFiles();
        var tags = new FakeTags();
        var transfers = new FakeTransfers();
        return (new TriageFileActions(files, tags, () => new TriageKeepOptions(keepFolder), withTransfers ? transfers : null), files, tags, transfers);
    }

    [Fact]
    public async Task Keep_PutsTheGreenTagOn_WithoutMovingAnything_WhenThereIsNoKeepFolder()
    {
        var (actions, files, tags, _) = Make();

        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Equal([TagIds.Green], tags.Tags[@"C:\inbox\a.mkv"]);
        Assert.Empty(files.Log);
        Assert.Null(result.Moved);
    }

    [Fact]
    public async Task Keep_KeepsTheTagsTheFileAlreadyHad()
    {
        var (actions, _, tags, _) = Make();
        tags.Tags[@"C:\inbox\a.mkv"] = [2, 5];

        await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Equal([2, 4, 5], tags.Tags[@"C:\inbox\a.mkv"].Order());
    }

    [Fact]
    public async Task UndoOfAKeep_TakesTheTagBackToWhatItWas()
    {
        var (actions, _, tags, _) = Make();
        tags.Tags[@"C:\inbox\a.mkv"] = [2];
        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        await result.Undo!();

        Assert.Equal([2], tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task Keep_WithAKeepFolderOnTheSameDrive_RenamesTheFile_AndReportsWhereItWent()
    {
        var (actions, files, tags, transfers) = Make(@"C:\keep");

        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Equal([@"move C:\inbox\a.mkv -> C:\keep\a.mkv"], files.Log);
        Assert.Equal(@"C:\keep\a.mkv", result.Moved!.Location.Value);
        Assert.Equal([TagIds.Green], tags.Tags[@"C:\inbox\a.mkv"]);
        Assert.Empty(transfers.Requests);
    }

    [Fact]
    public async Task UndoOfAKeepWithAMove_MovesTheFileHome_AndRestoresTheTags()
    {
        var (actions, files, tags, _) = Make(@"C:\keep");
        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);
        files.Log.Clear();

        await result.Undo!();

        Assert.Equal([@"move C:\keep\a.mkv -> C:\inbox\a.mkv"], files.Log);
        Assert.Empty(tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task Keep_WhenTheMoveFails_TakesTheTagOffAgain_AndSaysWhy()
    {
        var (actions, files, tags, _) = Make(@"C:\keep");
        files.Empty = true;
        files.Problems = ["Something named 'a.mkv' already exists in keep."];

        var ex = await Assert.ThrowsAsync<IOException>(() => actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None));

        Assert.Contains("already exists", ex.Message);
        Assert.Empty(tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task Keep_WhenTheKeepFolderIsTheFilesOwnFolder_DoesNotMove()
    {
        var (actions, files, _, _) = Make(@"C:\inbox\");

        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Empty(files.Log);
        Assert.Null(result.Moved);
    }

    [Fact]
    public async Task Keep_WithAKeepFolderOnAnotherDrive_QueuesAMoveJob_AndDoesNotWaitForIt()
    {
        var (actions, files, tags, transfers) = Make(@"D:\keep");

        var result = await actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Empty(files.Log);
        var request = Assert.Single(transfers.Requests);
        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.Equal(@"C:\inbox\a.mkv", request.Sources[0].Value);
        Assert.Equal(@"D:\keep", request.Destination.Value);
        Assert.Null(result.Moved);
        Assert.Equal([TagIds.Green], tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task Keep_WhenTheQueuedMoveIsRefused_TakesTheTagOffAndSaysWhy()
    {
        var (actions, _, tags, transfers) = Make(@"D:\keep");
        transfers.Outcome = new TransferOutcome(false, "Not enough space on D:.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None));

        Assert.Contains("Not enough space", ex.Message);
        Assert.Empty(tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task Keep_AcrossDrivesWithNoTransferService_IsRefused_WithoutLeavingATag()
    {
        var (actions, _, tags, _) = Make(@"D:\keep", withTransfers: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None));

        Assert.Empty(tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task Keep_WhenTheTagCannotBeWritten_FailsBeforeMovingAnything()
    {
        var (actions, files, tags, _) = Make(@"C:\keep");
        tags.FailWith = new IOException("This location cannot hold tags.");

        await Assert.ThrowsAsync<IOException>(() => actions.KeepAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None));

        Assert.Empty(files.Log);
    }

    [Fact]
    public async Task SameVolume_KnowsDrivesAndShares()
    {
        Assert.True(TriageFileActions.SameVolume(@"C:\a\b.mkv", @"c:\keep"));
        Assert.False(TriageFileActions.SameVolume(@"C:\a\b.mkv", @"D:\keep"));
        Assert.True(TriageFileActions.SameVolume(@"\\nas\inbox\a\b.mkv", @"\\NAS\inbox\keep"));
        Assert.False(TriageFileActions.SameVolume(@"\\nas\inbox\a\b.mkv", @"\\nas\media\keep"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Discard_SoftDeletes_AndReportsNothingMoved()
    {
        var (actions, files, _, _) = Make();

        var result = await actions.DiscardAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Equal([@"delete C:\inbox\a.mkv"], files.Log);
        Assert.Null(result.Moved);
        Assert.NotNull(result.Undo);
    }

    [Fact]
    public async Task UndoOfADiscard_PutsTheFileBackFromTheBin()
    {
        var (actions, files, _, _) = Make();
        var result = await actions.DiscardAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);
        files.Log.Clear();

        await result.Undo!();

        Assert.Equal([@"move C:\$Recycle.Bin\S-1\$Ra.mkv -> C:\inbox\a.mkv"], files.Log);
    }

    [Fact]
    public async Task Discard_WhenTheDeleteFails_Throws_WithTheWorkersReason()
    {
        var (actions, files, _, _) = Make();
        files.Empty = true;
        files.Problems = ["'a.mkv' is in use by another program."];

        var ex = await Assert.ThrowsAsync<IOException>(() => actions.DiscardAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None));

        Assert.Contains("in use", ex.Message);
    }

    [Fact]
    public async Task UndoOfADiscard_WhenSomethingTookTheName_Throws_SoTheSessionKeepsTheDecision()
    {
        var (actions, files, _, _) = Make();
        var result = await actions.DiscardAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);
        files.Empty = true;
        files.Problems = ["Something named 'a.mkv' already exists in inbox."];

        await Assert.ThrowsAsync<IOException>(() => result.Undo!());
    }

    [Fact]
    public async Task ToggleTag_FlipsTheTag_AndSaysWhetherItIsOn()
    {
        var (actions, _, tags, _) = Make();
        var file = Video(@"C:\inbox\a.mkv");

        Assert.True(await actions.ToggleTagAsync(file, 3, CancellationToken.None));
        Assert.Equal([3], tags.Tags[file.Location.Value]);
        Assert.False(await actions.ToggleTagAsync(file, 3, CancellationToken.None));
        Assert.Empty(tags.Tags[file.Location.Value]);
    }

    [Fact]
    public async Task ClearTags_RemovesThemAll()
    {
        var (actions, _, tags, _) = Make();
        tags.Tags[@"C:\inbox\a.mkv"] = [1, 4, 6];

        await actions.ClearTagsAsync(Video(@"C:\inbox\a.mkv"), CancellationToken.None);

        Assert.Empty(tags.Tags[@"C:\inbox\a.mkv"]);
    }

    [Fact]
    public async Task TheSession_UsesTheRealActions_ForADiscardAndItsUndo()
    {
        var (actions, files, _, _) = Make();
        var engine = new Leviathan.Testing.FakeVideoEngine();
        var session = new TriageSession([Video(@"C:\inbox\a.mkv"), Video(@"C:\inbox\b.mkv")], engine, actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync();

        await session.DiscardAsync();
        await session.UndoAsync();

        Assert.Equal(2, files.Log.Count);
        Assert.StartsWith("delete", files.Log[0], StringComparison.Ordinal);
        Assert.StartsWith("move", files.Log[1], StringComparison.Ordinal);
        Assert.Equal(TriageDecision.None, session.Items[0].Decision);
        Assert.Equal(@"C:\inbox\a.mkv", engine.OpenPath);
    }
}

public sealed class MediaSettingsTests
{
    [Fact]
    public void Defaults_AreTenPercent_AdvanceAtOnce_NoKeepFolder()
    {
        var options = new MediaSettings().ToTriageOptions();

        Assert.Equal(0.10, options.StartOffset);
        Assert.Equal(TimeSpan.Zero, options.AutoAdvanceDelay);
        Assert.Equal(string.Empty, new MediaSettings().TriageKeepFolder);
    }

    [Fact]
    public void AutoAdvanceOff_MeansStayOnTheFile() => Assert.Null(new MediaSettings { TriageAutoAdvance = false }.ToTriageOptions().AutoAdvanceDelay);

    [Fact]
    public void Clamped_KeepsValuesInRange_AndTrimsTheFolder()
    {
        var c = new MediaSettings { TriageStartOffsetPercent = 500, TriageAdvanceDelayMs = -5, TriageKeepFolder = "  D:\\keep  " }.Clamped();

        Assert.Equal(90, c.TriageStartOffsetPercent);
        Assert.Equal(0, c.TriageAdvanceDelayMs);
        Assert.Equal("D:\\keep", c.TriageKeepFolder);
    }

    [Fact]
    public void ToTriageOptions_ClampsTheDelayToOneSecond() =>
        Assert.Equal(TimeSpan.FromSeconds(1), new MediaSettings { TriageAdvanceDelayMs = 9000 }.ToTriageOptions().AutoAdvanceDelay);
}
