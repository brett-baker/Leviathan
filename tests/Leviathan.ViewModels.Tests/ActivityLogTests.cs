using Leviathan.Diagnostics;
using Leviathan.Testing;
using Leviathan.ViewModels.Activity;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

public sealed class ActivityLogTests
{
    private sealed class MemoryStore : IActivityStore
    {
        private readonly Lock _gate = new();

        public List<ActivityEntry> Saved { get; } = [];

        public int Batches { get; private set; }

        public bool Fail { get; set; }

        public Task AppendAsync(IReadOnlyList<ActivityEntry> entries, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("disk gone");
            }

            lock (_gate)
            {
                Saved.AddRange(entries);
                Batches++;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEntry>> LoadAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<ActivityEntry>>([.. Saved.Where(e => e.Time >= since).OrderByDescending(e => e.Time).Take(limit)]);
            }
        }

        public Task PruneAsync(DateTimeOffset before, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Saved.RemoveAll(e => e.Time < before);
            }

            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Saved.Clear();
            }

            return Task.CompletedTask;
        }
    }

    private static UserAction Action(string command, string? target = null, DateTimeOffset? at = null) =>
        new(at ?? new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), command, target);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Entries_AreNewestFirst_WithWordsInsteadOfCommandIds()
    {
        var log = new ActivityLog();

        log.Add(Action("nav.go", @"C:\media\films"));
        log.Add(Action("edit.rename", @"C:\media\films\a.mkv"));

        var entries = log.Find();
        Assert.Equal(["Renamed a.mkv", "Went to films"], entries.Select(e => e.Summary));
        Assert.Equal(ActivityKind.Files, entries[0].Kind);
        Assert.Equal(ActivityKind.Navigation, entries[1].Kind);
        Assert.Equal(@"C:\media\films", entries[1].Location);
    }

    [Fact]
    public void ACommandWithoutAWordedSummary_IsListedByItsIdAndKindOfItsPrefix()
    {
        var log = new ActivityLog();

        log.Add(Action("tab.something.new"));
        log.Add(Action("odd.thing"));

        var entries = log.Find();
        Assert.Equal("odd.thing", entries[0].Summary);
        Assert.Equal(ActivityKind.Other, entries[0].Kind);
        Assert.Equal(ActivityKind.Tabs, entries[1].Kind);
    }

    [Fact]
    public void Chrome_LikeTheSidebarClickAndTheHub_IsLeftOutBecauseTheNavigationItCausesIsLogged()
    {
        var log = new ActivityLog();

        log.Add(Action("sidebar.open", @"C:\media"));
        log.Add(Action("hub.toggle"));
        log.Add(Action("address.focus"));
        log.Add(Action("nav.go", @"C:\media"));

        Assert.Equal(["Went to media"], log.Find().Select(e => e.Summary));
    }

    [Fact]
    public void ATargetThatIsNotAPath_IsShownButHasNoLocationToJumpTo()
    {
        var log = new ActivityLog();

        log.Add(Action("filter.set", "*.mkv"));

        var entry = Assert.Single(log.Find());
        Assert.Equal("Filtered by *.mkv", entry.Summary);
        Assert.Null(entry.Location);
    }

    [Fact]
    public void Find_FiltersByKind_AndSearchesSummaryTargetAndCommand_IgnoringCase()
    {
        var log = new ActivityLog();
        log.Add(Action("nav.go", @"C:\Holiday"));
        log.Add(Action("edit.delete", @"C:\Holiday\old.mp4"));
        log.Add(Action("view.details"));

        Assert.Equal(2, log.Find(new ActivityQuery(Text: "holiday")).Count);
        Assert.Single(log.Find(new ActivityQuery(ActivityKind.Files)));
        Assert.Single(log.Find(new ActivityQuery(Text: "VIEW.DETAILS")));
        Assert.Single(log.Find(new ActivityQuery(ActivityKind.Files, "old")));
        Assert.Empty(log.Find(new ActivityQuery(ActivityKind.Tabs, "old")));
    }

    [Fact]
    public void Note_RecordsWhatTheAppSaid_WithAnErrorFlagAndAPlace()
    {
        var log = new ActivityLog();

        log.Note("Copy failed: access denied", isError: true, location: @"C:\media");

        var entry = Assert.Single(log.Find());
        Assert.True(entry.IsError);
        Assert.Equal(ActivityKind.Message, entry.Kind);
        Assert.Equal(@"C:\media", entry.Location);
    }

    [Fact]
    public void Add_RaisesChanged_AndOnlyTheNewestEntriesAreKept()
    {
        var log = new ActivityLog();
        var changes = 0;
        log.Changed += (_, _) => changes++;

        for (var i = 0; i < ActivityLog.MemoryLimit + 20; i++)
        {
            log.Add(Action("nav.up"));
        }

        Assert.Equal(ActivityLog.MemoryLimit + 20, changes);
        var all = log.Find();
        Assert.Equal(ActivityLog.MemoryLimit, all.Count);
        Assert.True(all[0].Id > all[^1].Id);
    }

    [Fact]
    public void ActionTrail_FeedsTheLog_ThroughTheRegistryRecordedEvent()
    {
        var trail = new ActionTrail();
        var log = new ActivityLog();
        trail.Recorded += (_, action) => log.Add(action);

        var registry = new CommandRegistry(trail);
        trail.Record("nav.reload");

        Assert.Equal("Reloaded the folder", Assert.Single(log.Find()).Summary);
        Assert.NotNull(registry);
    }

    [Fact]
    public async Task Entries_AreWrittenToTheStoreInOneBatch_AfterAMoment_NotOnTheCallersThread()
    {
        var time = new ManualTimeProvider();
        var store = new MemoryStore();
        var log = new ActivityLog(store, time);

        log.Add(Action("nav.up"));
        log.Add(Action("nav.back"));
        log.Add(Action("nav.forward"));
        Assert.Empty(store.Saved);

        time.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => store.Saved.Count == 3);

        Assert.Equal(1, store.Batches);
        log.Dispose();
    }

    [Fact]
    public void Dispose_WritesWhatIsStillWaiting()
    {
        var store = new MemoryStore();
        var log = new ActivityLog(store, new ManualTimeProvider());
        log.Add(Action("nav.up"));

        log.Dispose();

        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task AFailingStore_NeverFailsAnAction_AndTheEntriesStayInMemory()
    {
        var time = new ManualTimeProvider();
        var store = new MemoryStore { Fail = true };
        var log = new ActivityLog(store, time);

        log.Add(Action("nav.up"));
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Single(log.Find());
        log.Dispose();
    }

    [Fact]
    public async Task EarlierSessions_AreLoadedOnce_ShownOnlyWhenAsked_AndOldOnesAreDropped()
    {
        var time = new ManualTimeProvider();
        var store = new MemoryStore();
        var earlier = Guid.NewGuid();
        var now = time.GetUtcNow();
        await store.AppendAsync(
        [
            new ActivityEntry(0, now.AddDays(-2), earlier, ActivityKind.Files, "edit.delete", "Deleted old.mp4", @"C:\old.mp4", null),
            new ActivityEntry(0, now.AddDays(-9), earlier, ActivityKind.Files, "edit.copy", "Copied ancient.mp4", null, null),
        ], CancellationToken.None);
        var log = new ActivityLog(store, time);
        log.Add(Action("nav.up", at: now));

        var added = await log.LoadEarlierAsync();
        var again = await log.LoadEarlierAsync();

        Assert.Equal(1, added);
        Assert.Equal(0, again);
        Assert.Equal(["Went up one folder"], log.Find().Select(e => e.Summary));
        Assert.Equal(["Went up one folder", "Deleted old.mp4"], log.Find(new ActivityQuery(IncludeEarlier: true)).Select(e => e.Summary));
        await UntilAsync(() => store.Saved.Count == 1);
        log.Dispose();
    }

    [Fact]
    public async Task Clear_EmptiesTheLog_AndTheStore()
    {
        var store = new MemoryStore();
        var log = new ActivityLog(store, new ManualTimeProvider());
        log.Add(Action("nav.up"));
        log.Dispose();
        var fresh = new ActivityLog(store, new ManualTimeProvider());
        fresh.Add(Action("nav.back"));

        await fresh.ClearAsync();

        Assert.Empty(fresh.Find());
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void ExportText_ListsOldestFirst_WithTheTargetWhenTheSummaryLacksIt()
    {
        var log = new ActivityLog();
        log.Add(Action("nav.go", @"C:\media", new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero)));
        log.Note("Copy failed", isError: true);

        var text = ActivityLog.ExportText(log.Find());

        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Went to media", lines[0], StringComparison.Ordinal);
        Assert.Contains(@"(C:\media)", lines[0], StringComparison.Ordinal);
        Assert.Contains("[error] Copy failed", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ExportJson_IsAnArrayWithTheFieldsAFileNeeds()
    {
        var log = new ActivityLog();
        log.Add(Action("edit.delete", @"C:\a.txt"));

        var json = ActivityLog.ExportJson(log.Find());

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var row = document.RootElement[0];
        Assert.Equal("Files", row.GetProperty("kind").GetString());
        Assert.Equal("edit.delete", row.GetProperty("command").GetString());
        Assert.Equal(@"C:\a.txt", row.GetProperty("target").GetString());
        Assert.False(row.GetProperty("error").GetBoolean());
    }

    [Fact]
    public void RecordingAnAction_CostsWellUnderAMillisecond()
    {
        var log = new ActivityLog(new MemoryStore(), new ManualTimeProvider());
        for (var i = 0; i < 200; i++)
        {
            log.Add(Action("nav.up"));
        }

        // The best of three rounds: a busy machine can slow one round, and the claim is about what the code costs.
        var perAction = double.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < 1000; i++)
            {
                log.Add(Action("nav.go", @"C:\media\films"));
            }

            perAction = Math.Min(perAction, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / 1000);
        }

        Assert.True(perAction < 1, $"{perAction:F3} ms per action");
        log.Dispose();
    }
}
