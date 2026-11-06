using Leviathan.Contracts;
using Leviathan.Testing;
using Leviathan.ViewModels.Hub;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

/// <summary>The Soft-delete bin tab (spec §12.5, §16.1) and its automatic purge schedule.</summary>
public sealed class BinModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeBin : IBinService
    {
        public BinListing Listing { get; set; } = new([], []);

        public Exception? Failure { get; set; }

        public List<IReadOnlyList<BinEntryInfo>> Restored { get; } = [];

        public List<IReadOnlyList<BinEntryInfo>> Purged { get; } = [];

        public int PurgedOld { get; private set; }

        public Task<BinListing> ListAsync(CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Listing) : Task.FromException<BinListing>(Failure);

        public Task<TransferOutcome> RestoreAsync(IReadOnlyList<BinEntryInfo> items, CancellationToken cancellationToken)
        {
            Restored.Add(items);
            return Task.FromResult(new TransferOutcome(true, "Restoring…"));
        }

        public Task<TransferOutcome> PurgeAsync(IReadOnlyList<BinEntryInfo> items, CancellationToken cancellationToken)
        {
            Purged.Add(items);
            return Task.FromResult(new TransferOutcome(true, "Deleting…"));
        }

        public Task<TransferOutcome> PurgeOldAsync(CancellationToken cancellationToken)
        {
            PurgedOld++;
            return Task.FromResult(new TransferOutcome(true, "Emptying…"));
        }
    }

    private static BinEntryInfo Entry(string name, int daysAgo, long size = 10, bool folder = false) =>
        new($@"\\nas\media\show\{name}", $@"\\nas\media\.leviathan-trash\20261101-1\{name}", Now.AddDays(-daysAgo), size, folder);

    private static async Task<(BinModel Model, FakeBin Bin)> LoadedAsync(params BinEntryInfo[] items)
    {
        var bin = new FakeBin { Listing = new BinListing(items, []) };
        var model = new BinModel(bin, new ManualTimeProvider(Now));
        await model.RefreshAsync(CancellationToken.None);
        return (model, bin);
    }

    [Fact]
    public async Task Rows_AreNewestFirst_WithNameFolderAgeAndSize()
    {
        var (model, _) = await LoadedAsync(Entry("old.mkv", 9, 2048), Entry("new.mkv", 1, 5));

        var rows = model.Rows();

        Assert.Equal(["new.mkv", "old.mkv"], rows.Select(r => r.Name));
        Assert.Equal(@"\\nas\media\show", rows[0].Where);
        Assert.Equal("1 day ago", rows[0].Deleted);
        Assert.Equal("9 days ago", rows[1].Deleted);
        Assert.Equal("2 KB", rows[1].Size);
    }

    [Fact]
    public async Task Rows_CanBeFilteredByNameOrFolder()
    {
        var (model, _) = await LoadedAsync(Entry("alpha.mkv", 1), Entry("beta.mkv", 2));

        Assert.Equal(["beta.mkv"], model.Rows("BET").Select(r => r.Name));
        Assert.Equal(2, model.Rows("show").Count);
        Assert.Empty(model.Rows("zzz"));
    }

    [Fact]
    public async Task AFolderWhoseSizeIsUnknown_SaysFolder_AndTheTotalIsAtLeast()
    {
        var (model, _) = await LoadedAsync(Entry("a.mkv", 1, 1024), Entry("season", 2, -1, folder: true));

        Assert.Equal("Folder", model.Rows().Single(r => r.Name == "season").Size);
        Assert.Equal("2 items · at least 1 KB", model.Summary());
    }

    [Fact]
    public async Task Summary_SaysWhenThereIsNothing_AndWhichSharesCouldNotBeRead()
    {
        var bin = new FakeBin { Listing = new BinListing([], [@"\\gone\share\.leviathan-trash"]) };
        var model = new BinModel(bin, new ManualTimeProvider(Now));
        await model.RefreshAsync(CancellationToken.None);

        Assert.Equal(@"Nothing deleted from a network share is waiting here. · 1 trash folder could not be read (\\gone\share)", model.Summary());
    }

    [Fact]
    public async Task AFailedRead_IsShownAsAProblem_NotAsAnEmptyBin()
    {
        var bin = new FakeBin { Failure = new IOException("pipe closed") };
        var model = new BinModel(bin, new ManualTimeProvider(Now));

        await model.RefreshAsync(CancellationToken.None);

        Assert.Contains("could not be read", model.Summary(), StringComparison.Ordinal);
        Assert.Contains("pipe closed", model.Summary(), StringComparison.Ordinal);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task Refresh_RaisesChangedWhenItStartsAndEnds()
    {
        var (model, _) = await LoadedAsync();
        var states = new List<bool>();
        model.Changed += (_, _) => states.Add(model.IsLoading);

        await model.RefreshAsync(CancellationToken.None);

        Assert.Equal([true, false], states);
    }

    [Fact]
    public async Task RestoreAndPurge_SendTheEntriesOfTheRowsPicked_AndNothingWhenNoneAre()
    {
        var (model, bin) = await LoadedAsync(Entry("a.mkv", 1), Entry("b.mkv", 2));
        var rows = model.Rows();

        await model.RestoreAsync([rows[0]], CancellationToken.None);
        await model.PurgeAsync(rows, CancellationToken.None);
        var none = await model.RestoreAsync([], CancellationToken.None);

        Assert.Equal(rows[0].Entry, Assert.Single(Assert.Single(bin.Restored)));
        Assert.Equal(2, Assert.Single(bin.Purged).Count);
        Assert.False(none.Started);
        Assert.Single(bin.Restored);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(3 * 3600, "3 hours ago")]
    [InlineData(5 * 86400, "5 days ago")]
    public void AgeText_ReadsInWords(int seconds, string expected) =>
        Assert.Equal(expected, BinModel.AgeText(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void AgeText_AfterAMonth_IsTheDate() =>
        Assert.Matches(@"\d{1,2} \w+ 2026", BinModel.AgeText(Now.AddDays(-60), Now));

    // ---- the daily purge

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
    public async Task AutoPurge_WaitsTwoMinutes_ThenRunsOncePerDay_ReadingTheLimitsEachTime()
    {
        var time = new ManualTimeProvider(Now);
        var asked = new List<PurgePolicy>();
        var limits = new PurgePolicy(30, 100L << 30);
        using var stop = new CancellationTokenSource();
        var run = new BinPurgeSchedule(time).RunAsync(() => limits, (p, _) => { lock (asked) { asked.Add(p); } return Task.CompletedTask; }, stop.Token);

        await Task.Delay(50);
        Assert.Empty(asked);

        time.Advance(BinPurgeSchedule.FirstDelay);
        await UntilAsync(() => { lock (asked) { return asked.Count == 1; } });

        limits = new PurgePolicy(7, 0);
        time.Advance(BinPurgeSchedule.Interval);
        await UntilAsync(() => { lock (asked) { return asked.Count == 2; } });

        Assert.Equal(new PurgePolicy(7, 0), asked[1]);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AutoPurge_WithBothLimitsOff_AsksForNothing_AndAWorkerThatIsAway_IsSkippedNotFatal()
    {
        var time = new ManualTimeProvider(Now);
        var calls = 0;
        PurgePolicy limits = new(0, 0);
        using var stop = new CancellationTokenSource();
        var run = new BinPurgeSchedule(time).RunAsync(
            () => limits,
            (_, _) => { Interlocked.Increment(ref calls); throw new IOException("worker away"); },
            stop.Token);

        time.Advance(BinPurgeSchedule.FirstDelay);
        await Task.Delay(50);
        Assert.Equal(0, calls);

        limits = new PurgePolicy(30, 0);
        time.Advance(BinPurgeSchedule.Interval);
        await UntilAsync(() => Volatile.Read(ref calls) == 1);
        time.Advance(BinPurgeSchedule.Interval);
        await UntilAsync(() => Volatile.Read(ref calls) == 2);

        Assert.False(run.IsFaulted);
        await stop.CancelAsync();
        await run;
    }
}
