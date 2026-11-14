using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Compare;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.Sync;

namespace Leviathan.ViewModels.Tests;

/// <summary>Saved sync pairs, their schedule and their row in Favorites (spec §15.5).</summary>
public sealed class SyncPairTests
{
    private const string File = @"C:\Data\sync-pairs.json";
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static SyncPair Pair(string name = "Photos", int every = 0, SyncMode mode = SyncMode.TwoWay, bool auto = false, DateTimeOffset? checkedAt = null) =>
        new() { Name = name, Left = @"D:\Photos", Right = @"\\nas\photos", Mode = mode, CheckEveryMinutes = every, AutoRun = auto, Created = Now.AddDays(-10), LastChecked = checkedAt };

    [Fact]
    public void Store_SavesAndReloads_EveryField()
    {
        var disk = new MockFileSystem();
        var pair = Pair("Photos", every: 60, mode: SyncMode.UpdateLeftToRight, auto: true) with { By = CompareBy.Checksum, ToleranceSeconds = 5, LastResult = "up to date", LastChecked = Now };

        new SyncPairStore(disk, File).Save(pair);
        var back = new SyncPairStore(disk, File).Items;

        Assert.Equal(pair, Assert.Single(back));
    }

    [Fact]
    public void Store_SavingTheSameIdTwice_ReplacesIt_AndRemoveForgetsIt()
    {
        var store = new SyncPairStore(new MockFileSystem(), File);
        var pair = Pair();

        store.Save(pair);
        store.Save(pair with { Name = "Renamed" });

        Assert.Equal("Renamed", Assert.Single(store.Items).Name);
        Assert.Equal("Renamed", store.Find(pair.Id)!.Name);
        store.Remove(pair.Id);
        Assert.Empty(store.Items);
        Assert.Null(store.Find(pair.Id));
    }

    [Fact]
    public void Store_RecordCheck_NotesWhenAndWhat_AndIgnoresAnUnknownPair()
    {
        var store = new SyncPairStore(new MockFileSystem(), File);
        var pair = Pair();
        store.Save(pair);

        store.RecordCheck(pair.Id, Now, "3 changes waiting");
        store.RecordCheck(Guid.NewGuid(), Now, "nope");

        Assert.Equal((Now, "3 changes waiting"), (store.Items[0].LastChecked, store.Items[0].LastResult));
        Assert.Single(store.Items);
    }

    [Fact]
    public void Store_RaisesChanged_OnEveryChange()
    {
        var store = new SyncPairStore(new MockFileSystem(), File);
        var count = 0;
        store.Changed += (_, _) => count++;
        var pair = Pair();

        store.Save(pair);
        store.RecordCheck(pair.Id, Now, "x");
        store.Remove(pair.Id);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Store_AWrite_LeavesNoTempFile_AndACorruptFileIsAnEmptyList()
    {
        var disk = new MockFileSystem();
        new SyncPairStore(disk, File).Save(Pair());
        Assert.False(disk.File.Exists(File + ".tmp"));

        disk.File.WriteAllText(File, "{ this is not json");

        Assert.Empty(new SyncPairStore(disk, File).Items);
    }

    [Fact]
    public void Locations_RoundTripAnId_AndRefuseEverythingElse()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, SyncPairLocations.IdOf(SyncPairLocations.For(id)));
        Assert.Null(SyncPairLocations.IdOf(LocationPath.Parse(@"C:\folder")));
        Assert.Null(SyncPairLocations.IdOf(LocationPath.Parse("shell:syncpair")));
        Assert.Null(SyncPairLocations.IdOf(LocationPath.Parse(@"shell:syncpair\not-a-guid")));
    }

    [Fact]
    public void Due_NeedsAnInterval_AndEnoughTimeSinceTheLastCheck()
    {
        var never = Pair("never", every: 0);
        var fresh = Pair("fresh", every: 60, checkedAt: Now.AddMinutes(-30));
        var stale = Pair("stale", every: 60, checkedAt: Now.AddMinutes(-60));
        var unchecked1 = Pair("unchecked", every: 1440);

        var due = SyncSchedule.Due([never, fresh, stale, unchecked1], Now);

        Assert.Equal(["stale", "unchecked"], due.Select(p => p.Name));
    }

    [Fact]
    public void Due_AFreshlySavedPair_WaitsOneInterval_BeforeItsFirstCheck()
    {
        var justSaved = Pair("new", every: 60) with { Created = Now.AddMinutes(-5) };

        Assert.Empty(SyncSchedule.Due([justSaved], Now));
        Assert.Single(SyncSchedule.Due([justSaved], Now.AddMinutes(56)));
    }

    [Theory]
    [InlineData(SyncMode.UpdateLeftToRight, true, true)]
    [InlineData(SyncMode.UpdateRightToLeft, true, true)]
    [InlineData(SyncMode.UpdateLeftToRight, false, false)]
    [InlineData(SyncMode.MirrorLeftToRight, true, false)]
    [InlineData(SyncMode.MirrorRightToLeft, true, false)]
    [InlineData(SyncMode.TwoWay, true, false)]
    public void OnlyAnUpdate_MayRunWithNobodyLooking(SyncMode mode, bool auto, bool allowed)
    {
        Assert.Equal(allowed, SyncSchedule.MayRunUnattended(Pair(mode: mode, auto: auto)));
    }

    [Theory]
    [InlineData(0, "Never")]
    [InlineData(15, "Every 15 minutes")]
    [InlineData(60, "Every hour")]
    [InlineData(360, "Every 6 hours")]
    [InlineData(1440, "Every day")]
    public void Describe_UsesWords(int minutes, string expected)
    {
        Assert.Equal(expected, SyncSchedule.Describe(minutes));
    }

    [Fact]
    public void Summarize_SaysWhatIsWaiting()
    {
        var change = new SyncAction("a", SyncActionKind.CopyToRight, false, 1, false, false, "x");
        var conflict = new CompareEntry("c", false, null, null, CompareStatus.Different);

        Assert.Equal("up to date", SyncSchedule.Summarize(new SyncPlan(SyncMode.TwoWay, [], [], [])));
        Assert.Equal("1 change waiting", SyncSchedule.Summarize(new SyncPlan(SyncMode.TwoWay, [change], [], [])));
        Assert.Equal("2 changes waiting, 1 conflict", SyncSchedule.Summarize(new SyncPlan(SyncMode.TwoWay, [change, change], [conflict], [])));
    }

    [Fact]
    public void Favorites_ListsEachPairUnderItsOwnHeading_WithWhatTheLastCheckFound()
    {
        var pair = Pair("Photos") with { LastResult = "3 changes waiting" };

        var sections = SidebarSections.Build([], [], [], [], syncPairs: [pair]);

        var favorites = sections.Single(s => s.Title == "Favorites");
        var group = Assert.Single(favorites.Groups);
        Assert.Equal(SidebarSections.SyncPairsTitle, group.Title);
        var place = Assert.Single(group.Places);
        Assert.Equal("Photos", place.Title);
        Assert.Equal(pair.Id, SyncPairLocations.IdOf(place.Location));
        Assert.Contains("3 changes waiting", place.Detail);
        Assert.True(place.IsLeaf);
    }

    [Fact]
    public void Favorites_WithNoPairs_HasNoSyncHeading()
    {
        Assert.Empty(SidebarSections.Build([], [], [], []).Single(s => s.Title == "Favorites").Groups);
    }
}
