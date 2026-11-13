using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// Triage keeps nothing in memory only (spec §10.1, §3.4): decisions are journaled as they are made, a crash loses none of them, and kept
/// files waiting to be moved are offered again.
/// </summary>
public sealed class TriageJournalTests
{
    private const string Journal = @"C:\data\triage-journal.jsonl";
    private const string Store = @"C:\data\inboxes.json";

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

    private static TriageSession Session(params string[] paths) =>
        new([.. paths.Select(Video)], new FakeVideoEngine(), new Actions(), new TriageOptions { StartOffset = 0 });

    // ---- the journal ----

    [Fact]
    public void Replay_KeepsTheLatestDecisionOfEachFile()
    {
        var journal = new TriageJournal(new MockFileSystem(), Journal);
        journal.AppendDecision("inbox1", @"Z:\a.mp4", TriageDecision.Kept, null);
        journal.AppendDecision("inbox1", @"Z:\b.mp4", TriageDecision.Discarded, null);
        journal.AppendDecision("inbox1", @"Z:\a.mp4", TriageDecision.None, null);

        var records = journal.Replay();

        Assert.Equal(2, records.Count);
        Assert.Equal(TriageDecision.None, records.Single(r => r.Path == @"Z:\a.mp4").Decision);
        Assert.Equal(TriageDecision.Discarded, records.Single(r => r.Path == @"Z:\b.mp4").Decision);
    }

    [Fact]
    public void ATornLastLine_IsSkipped_AndEverythingBeforeItIsKept()
    {
        var disk = new MockFileSystem();
        var journal = new TriageJournal(disk, Journal);
        journal.AppendDecision("", @"Z:\a.mp4", TriageDecision.Kept, @"D:\library");
        disk.File.AppendAllText(Journal, "{\"k\":\"d\",\"p\":\"Z:\\\\b.mp");

        var records = journal.Replay();

        Assert.Equal([@"Z:\a.mp4"], records.Select(r => r.Path));
    }

    [Fact]
    public void AKeptFileWithAFolder_IsPendingUntilItsMoveIsRecorded()
    {
        var journal = new TriageJournal(new MockFileSystem(), Journal);
        journal.AppendDecision("", @"Z:\a.mp4", TriageDecision.Kept, @"D:\library");
        journal.AppendDecision("", @"Z:\b.mp4", TriageDecision.Kept, null);
        journal.AppendDecision("", @"Z:\c.mp4", TriageDecision.Discarded, @"D:\library");

        Assert.Equal([@"Z:\a.mp4"], journal.PendingMoves().Select(r => r.Path));

        journal.AppendMoved([@"Z:\a.mp4"]);

        Assert.Empty(journal.PendingMoves());
    }

    [Fact]
    public void AKeptFile_ThatIsLaterUndone_IsNoLongerPending()
    {
        var journal = new TriageJournal(new MockFileSystem(), Journal);
        journal.AppendDecision("", @"Z:\a.mp4", TriageDecision.Kept, @"D:\library");
        journal.AppendDecision("", @"Z:\a.mp4", TriageDecision.None, null);

        Assert.Empty(journal.PendingMoves());
    }

    [Fact]
    public void Compact_DropsWhatIsSelected_AndKeepsTheRest_IncludingMovedMarks()
    {
        var disk = new MockFileSystem();
        var journal = new TriageJournal(disk, Journal);
        journal.AppendDecision("i1", @"Z:\a.mp4", TriageDecision.Kept, null);
        journal.AppendDecision("i2", @"Z:\b.mp4", TriageDecision.Kept, @"D:\library");
        journal.AppendDecision("i2", @"Z:\c.mp4", TriageDecision.Kept, @"D:\library");
        journal.AppendMoved([@"Z:\c.mp4"]);

        journal.Compact(r => r.Scope == "i1");

        Assert.Equal([@"Z:\b.mp4", @"Z:\c.mp4"], journal.Replay().Select(r => r.Path));
        Assert.Equal([@"Z:\b.mp4"], journal.PendingMoves().Select(r => r.Path));

        journal.Compact(_ => true);

        Assert.False(disk.File.Exists(Journal));
    }

    // ---- recording a session ----

    [Fact]
    public async Task Recorder_JournalsEveryDecision_InOrder_IncludingUndo()
    {
        var journal = new TriageJournal(new MockFileSystem(), Journal);
        var session = Session(@"Z:\a.mp4", @"Z:\b.mp4", @"Z:\c.mp4");
        using var recorder = new TriageRecorder(session, journal, "inbox1", () => @"D:\library");
        await session.StartAsync();

        await session.KeepAsync();
        await session.DiscardAsync();
        await session.UndoAsync();
        await recorder.Flushed;

        var records = journal.Replay().ToDictionary(r => r.Path);
        Assert.Equal(TriageDecision.Kept, records[@"Z:\a.mp4"].Decision);
        Assert.Equal(@"D:\library", records[@"Z:\a.mp4"].MoveTo);
        Assert.Equal(TriageDecision.None, records[@"Z:\b.mp4"].Decision);
        Assert.All(records.Values, r => Assert.Equal("inbox1", r.Scope));
    }

    [Fact]
    public async Task Recorder_ASkip_IsJournaled_ButADiscardNeverCarriesAMove()
    {
        var journal = new TriageJournal(new MockFileSystem(), Journal);
        var session = Session(@"Z:\a.mp4", @"Z:\b.mp4");
        using var recorder = new TriageRecorder(session, journal, string.Empty, () => @"D:\library");
        await session.StartAsync();

        await session.SkipAsync();
        await session.DiscardAsync();
        await recorder.Flushed;

        var records = journal.Replay().ToDictionary(r => r.Path);
        Assert.Equal(TriageDecision.Skipped, records[@"Z:\a.mp4"].Decision);
        Assert.Null(records[@"Z:\b.mp4"].MoveTo);
    }

    [Fact]
    public async Task Recorder_AJournalThatCannotBeWritten_DoesNotStopTheTriage()
    {
        var disk = new MockFileSystem();
        var session = Session(@"Z:\a.mp4");
        disk.AddFile(@"C:\data", new MockFileData("a file where the folder should be"));
        using var recorder = new TriageRecorder(session, new TriageJournal(disk, Journal), string.Empty, () => null);
        await session.StartAsync();

        await session.KeepAsync();
        await recorder.Flushed;

        Assert.Equal(1, session.Kept);
    }

    // ---- crash and restart ----

    [Fact]
    public async Task AfterACrash_TheDecisionsOfTheLostSessionAreInTheInboxAgain_AndTheMovesAreStillPending()
    {
        var disk = new MockFileSystem();
        var journal = new TriageJournal(disk, Journal);
        string inboxId;
        using (var first = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal))
        {
            var inbox = await first.AddAsync("NAS", @"Z:\inbox", true, TriageOrder.Oldest, 1000, TestContext.Current.CancellationToken);
            inboxId = inbox.Id;
            var session = Session(@"Z:\inbox\a.mp4", @"Z:\inbox\b.mp4", @"Z:\inbox\c.mp4", @"Z:\inbox\d.mp4");
            using var recorder = new TriageRecorder(session, journal, inbox.Id, () => @"D:\library");
            await session.StartAsync();
            await session.KeepAsync();
            await session.DiscardAsync();
            await session.SkipAsync();
            await recorder.Flushed;

            // The process dies here: no window close, so RecordAsync never runs and inboxes.json knows nothing of this session.
        }

        using var restarted = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal);
        var inboxes = await restarted.ListAsync(TestContext.Current.CancellationToken);

        var back = Assert.Single(inboxes);
        Assert.Equal(inboxId, back.Id);
        Assert.Equal(2, restarted.RecoveredCount);
        Assert.True(back.Decided.SetEquals([@"Z:\inbox\a.mp4", @"Z:\inbox\b.mp4"]));
        Assert.Equal([@"Z:\inbox\a.mp4"], journal.PendingMoves().Select(r => r.Path));
    }

    [Fact]
    public async Task Recovery_IsSavedToTheInboxFile_SoItDoesNotNeedTheJournalAgain()
    {
        var disk = new MockFileSystem();
        var journal = new TriageJournal(disk, Journal);
        string id;
        using (var first = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal))
        {
            id = (await first.AddAsync("NAS", @"Z:\inbox", true, TriageOrder.Oldest, 1000, TestContext.Current.CancellationToken)).Id;
        }

        journal.AppendDecision(id, @"Z:\inbox\a.mp4", TriageDecision.Discarded, null);
        using (var second = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal))
        {
            await second.ListAsync(TestContext.Current.CancellationToken);
        }

        using var third = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store));
        Assert.Contains(@"Z:\inbox\a.mp4", (await third.ListAsync(TestContext.Current.CancellationToken))[0].Decided);
        Assert.Empty(journal.Replay());
    }

    [Fact]
    public async Task AnUndoneDecision_IsRemovedFromTheInbox_WhenRecovered()
    {
        var disk = new MockFileSystem();
        var journal = new TriageJournal(disk, Journal);
        string id;
        using (var first = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal))
        {
            id = (await first.AddAsync("NAS", @"Z:\inbox", true, TriageOrder.Oldest, 1000, TestContext.Current.CancellationToken)).Id;
        }

        journal.AppendDecision(id, @"Z:\inbox\a.mp4", TriageDecision.Kept, null);
        journal.AppendDecision(id, @"Z:\inbox\a.mp4", TriageDecision.None, null);

        using var restarted = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal);

        Assert.Empty((await restarted.ListAsync(TestContext.Current.CancellationToken))[0].Decided);
    }

    [Fact]
    public async Task ClosingNormally_SavesTheInbox_AndClearsItsJournalEntries_ButKeepsPendingMoves()
    {
        var disk = new MockFileSystem();
        var journal = new TriageJournal(disk, Journal);
        using var catalog = new InboxCatalog(new JsonFileStore<InboxesFile>(disk, Store), journal: journal);
        var inbox = await catalog.AddAsync("NAS", @"Z:\inbox", true, TriageOrder.Oldest, 1000, TestContext.Current.CancellationToken);
        var session = Session(@"Z:\inbox\a.mp4", @"Z:\inbox\b.mp4");
        using var recorder = new TriageRecorder(session, journal, inbox.Id, () => @"D:\library");
        await session.StartAsync();
        await session.KeepAsync();
        await session.DiscardAsync();
        await recorder.Flushed;

        await catalog.RecordAsync(inbox, session, TestContext.Current.CancellationToken);

        Assert.Equal([@"Z:\inbox\a.mp4"], journal.Replay().Select(r => r.Path));
        Assert.Equal(2, inbox.Decided.Count);
    }

    // ---- what is offered after a restart ----

    [Fact]
    public void PendingKeepMoves_GroupsByFolder_AndTreatsAGoneFileAsDealtWith()
    {
        var pending = new[]
        {
            new JournalRecord("", @"Z:\a.mp4", TriageDecision.Kept, @"D:\library", false),
            new JournalRecord("", @"Z:\b.mp4", TriageDecision.Kept, @"D:\library", false),
            new JournalRecord("", @"Z:\c.mp4", TriageDecision.Kept, @"E:\other", false),
            new JournalRecord("", @"Z:\gone.mp4", TriageDecision.Kept, @"D:\library", false),
            new JournalRecord("", @"Z:\done.mp4", TriageDecision.Kept, @"D:\library", true),
        };

        var found = PendingKeepMoves.From(pending, path => !path.Contains("gone", StringComparison.Ordinal));

        Assert.Equal(3, found.Count);
        Assert.Equal(2, found.Groups.Count);
        Assert.Equal(["a.mp4", "b.mp4"], found.Groups.Single(g => g.Folder == @"D:\library").Files.Select(f => f.Name));
        Assert.Equal([@"Z:\gone.mp4"], found.Stale);
    }

    [Fact]
    public void Handled_IsEveryFileThatLeftItsPlace_AndTheFarOnesWhenAJobWasQueued()
    {
        var files = new[] { Video(@"Z:\a.mp4"), Video(@"Z:\b.mp4"), Video(@"E:\c.mp4") };
        var plan = new TriageHandOffPlan(files, @"Z:\library", false);

        var renamedOnly = TriageHandOff.Handled(plan, new TriageMoveResult(2, 0, []), path => path.EndsWith("b.mp4", StringComparison.Ordinal));
        var withJob = TriageHandOff.Handled(plan, new TriageMoveResult(0, 1, []), _ => true);

        Assert.Equal([@"Z:\a.mp4", @"E:\c.mp4"], renamedOnly);
        Assert.Equal([@"E:\c.mp4"], withJob);
    }

    [Fact]
    public void KeepingEachFileAsItIsKept_IsTheDefault()
    {
        Assert.Equal(Leviathan.ViewModels.Settings.TriageMoveMode.OnKeep, new Leviathan.ViewModels.Settings.MediaSettings().TriageMoveWhen);
    }
}
