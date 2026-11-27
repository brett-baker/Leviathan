using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Triage.Learning;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// What triage hands to the learning: every decision with its reject reason, kept for good, and the examples that remain once taken-back
/// decisions and skips are set aside.
/// </summary>
public sealed class TriageLearningHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static FileEntry Video(string path, long size = 5000)
    {
        var location = LocationPath.Parse(path);
        return new FileEntry(location.Name, location, FileEntryKind.File, size, T0, T0, FileAttributes.Normal);
    }

    private sealed class Actions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TriageSession Session(params string[] paths) =>
        new([.. paths.Select(p => Video(p))], new FakeVideoEngine(), new Actions(), new TriageOptions { StartOffset = 0 });

    private static TriageRecorder Record(TriageSession session, FakeTriageHistory history) =>
        new(session, new TriageJournal(new MockFileSystem(), @"C:\data\j.jsonl"), string.Empty, () => null, history, new ManualTimeProvider(T0));

    // ---- the session carries the reason ----

    [Fact]
    public async Task Discard_WithAReason_KeepsItOnTheItem()
    {
        var session = Session(@"Z:\a.mp4", @"Z:\b.mp4");
        await session.StartAsync();

        await session.DiscardAsync("short");

        Assert.Equal("short", session.Items[0].RejectReasonId);
        Assert.Equal(TriageDecision.Discarded, session.Items[0].Decision);
    }

    [Fact]
    public async Task Discard_WithoutAReason_HasNone()
    {
        var session = Session(@"Z:\a.mp4");
        await session.StartAsync();

        await session.DiscardAsync();

        Assert.Null(session.Items[0].RejectReasonId);
    }

    [Fact]
    public async Task Keep_NeverCarriesAReason()
    {
        var session = Session(@"Z:\a.mp4");
        await session.StartAsync();

        await session.KeepAsync();

        Assert.Null(session.Items[0].RejectReasonId);
    }

    [Fact]
    public async Task Undo_TakesTheReasonBackToo()
    {
        var session = Session(@"Z:\a.mp4", @"Z:\b.mp4");
        await session.StartAsync();
        await session.DiscardAsync("short");

        await session.UndoAsync();

        Assert.Equal(TriageDecision.None, session.Items[0].Decision);
        Assert.Null(session.Items[0].RejectReasonId);
    }

    [Fact]
    public async Task DecidingAgain_ReplacesTheReason()
    {
        var session = Session(@"Z:\a.mp4", @"Z:\b.mp4");
        await session.StartAsync();
        await session.DiscardAsync("short");
        await session.PreviousAsync();

        await session.DiscardAsync("seen");

        Assert.Equal("seen", session.Items[0].RejectReasonId);
    }

    [Fact]
    public async Task Restore_ClearsTheReason_AndIsRecorded()
    {
        var session = Session(@"Z:\a.mp4");
        var history = new FakeTriageHistory();
        using var recorder = Record(session, history);
        await session.StartAsync();
        await session.DiscardAsync("short");

        await session.RestoreAsync(session.Items[0]);
        await recorder.Flushed;

        Assert.Null(session.Items[0].RejectReasonId);
        Assert.Equal([TriageDecision.Discarded, TriageDecision.None], history.Records.Select(r => r.Decision));
    }

    // ---- the recorder writes the history ----

    [Fact]
    public async Task Recorder_WritesEachDecision_WithReasonSizeAndTime()
    {
        var session = new TriageSession([Video(@"Z:\a.mp4", 123), Video(@"Z:\b.mp4", 456)], new FakeVideoEngine(), new Actions(), new TriageOptions { StartOffset = 0 });
        var history = new FakeTriageHistory();
        using var recorder = Record(session, history);
        await session.StartAsync();

        await session.DiscardAsync("short");
        await session.KeepAsync();
        await recorder.Flushed;

        var records = history.Records;
        Assert.Equal(2, records.Count);
        Assert.Equal(new TriageDecisionRecord(T0, @"Z:\a.mp4", TriageDecision.Discarded, "short", 123, T0.UtcTicks), records[0]);
        Assert.Equal(TriageDecision.Kept, records[1].Decision);
        Assert.Null(records[1].ReasonId);
    }

    [Fact]
    public async Task Recorder_AHistoryThatCannotBeWritten_DoesNotStopTheTriage_OrTheJournal()
    {
        var session = Session(@"Z:\a.mp4", @"Z:\b.mp4");
        var history = new FakeTriageHistory { FailWith = new IOException("disk full") };
        var journal = new TriageJournal(new MockFileSystem(), @"C:\data\j.jsonl");
        using var recorder = new TriageRecorder(session, journal, string.Empty, () => null, history);
        await session.StartAsync();

        await session.DiscardAsync("short");
        await session.KeepAsync();
        await recorder.Flushed;

        Assert.Equal("disk full", recorder.LastError);
        Assert.Equal(2, journal.Replay().Count);
    }

    // ---- examples ----

    private static TriageDecisionRecord Rec(string path, TriageDecision decision, int minute, string? reason = null) =>
        new(T0.AddMinutes(minute), path, decision, reason, 1, 1);

    [Fact]
    public void Examples_AreTheLatestDecisionOfEachFile()
    {
        var examples = TriageHistoryReplay.Examples(
        [
            Rec(@"Z:\a.mp4", TriageDecision.Discarded, 0, "short"),
            Rec(@"Z:\b.mp4", TriageDecision.Kept, 1),
            Rec(@"Z:\a.mp4", TriageDecision.Kept, 2),
        ]);

        Assert.Equal([TriageDecision.Kept, TriageDecision.Kept], examples.Select(e => e.Decision));
        Assert.Equal([@"Z:\a.mp4", @"Z:\b.mp4"], examples.Select(e => e.Path));
    }

    [Fact]
    public void Examples_LeaveOutWhatWasTakenBack_OrSkipped()
    {
        var examples = TriageHistoryReplay.Examples(
        [
            Rec(@"Z:\a.mp4", TriageDecision.Discarded, 0),
            Rec(@"Z:\a.mp4", TriageDecision.None, 1),
            Rec(@"Z:\b.mp4", TriageDecision.Skipped, 2),
            Rec(@"Z:\c.mp4", TriageDecision.Discarded, 3, "seen"),
        ]);

        var only = Assert.Single(examples);
        Assert.Equal(@"Z:\c.mp4", only.Path);
        Assert.Equal("seen", only.ReasonId);
    }

    [Fact]
    public void Examples_TreatPathsCaseInsensitively()
    {
        var examples = TriageHistoryReplay.Examples(
        [
            Rec(@"Z:\A.mp4", TriageDecision.Discarded, 0),
            Rec(@"z:\a.MP4", TriageDecision.Kept, 1),
        ]);

        Assert.Equal(TriageDecision.Kept, Assert.Single(examples).Decision);
    }
}
