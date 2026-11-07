using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

public sealed class SidebarTreeTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class FakeSource : ISubfolderSource
    {
        public Dictionary<string, string[]> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, TaskCompletionSource<IReadOnlyList<LocationPath>>> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, LocationError> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Listed { get; } = [];

        public Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken)
        {
            Listed.Add(location.Value);
            if (Failures.TryGetValue(location.Value, out var error))
            {
                throw new LocationException(error, location, "failed");
            }

            if (Pending.TryGetValue(location.Value, out var pending))
            {
                return pending.Task.WaitAsync(cancellationToken);
            }

            IReadOnlyList<LocationPath> result = Folders.TryGetValue(location.Value, out var names) ? [.. names.Select(location.Combine)] : [];
            return Task.FromResult(result);
        }
    }

    private static (SidebarTree Tree, FakeSource Source, ManualTimeProvider Time) Make()
    {
        var source = new FakeSource();
        source.Folders[@"C:\"] = ["Users", "Windows", "apps"];
        source.Folders[@"C:\Users"] = ["brett"];
        source.Folders[@"C:\Users\brett"] = ["Documents"];
        var time = new ManualTimeProvider();
        var tree = new SidebarTree(source, time);
        tree.SetSections(
        [
            new SidebarSection("Favorites", [], "Press Ctrl+D to pin a folder"),
            new SidebarSection("Common folders", [new SidebarPlace("Documents", P(@"C:\Users\brett\Documents"), SidebarIcon.Documents)]),
            new SidebarSection("Local", [new SidebarPlace("Local Disk (C:)", P(@"C:\"), SidebarIcon.Drive, "1 GB free", 0.5)]),
            new SidebarSection("Network", [new SidebarPlace("Data (Z:)", P(@"\\nas\data"), SidebarIcon.NetworkDrive)]),
            new SidebarSection("Internet", []),
        ]);
        return (tree, source, time);
    }

    private static string[] Titles(SidebarTree tree) => [.. tree.Rows.Select(r => r.Title)];

    private static SidebarNode Row(SidebarTree tree, string title) => tree.Rows.First(r => r.Title == title);

    [Fact]
    public void SetSections_ShowsHeadersAndPlaces_AndDropsEmptySectionsWithoutText()
    {
        var (tree, _, _) = Make();

        Assert.Equal(
            ["Favorites", "Press Ctrl+D to pin a folder", "Common folders", "Documents", "Local", "Local Disk (C:)", "Network", "Data (Z:)"],
            Titles(tree));
        Assert.Equal(0, Row(tree, "Local").Depth);
        Assert.Equal(1, Row(tree, "Local Disk (C:)").Depth);
        Assert.True(Row(tree, "Local Disk (C:)").CanExpand);
    }

    [Fact]
    public async Task UpdateChildren_AFolderCreatedInAnExpandedFolder_AppearsInOrder_WithoutListingAnything()
    {
        var (tree, source, _) = Make();
        var drive = Row(tree, "Local Disk (C:)");
        await tree.ExpandAsync(drive, CancellationToken.None);
        var listed = source.Listed.Count;

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\Users"), P(@"C:\Windows"), P(@"C:\apps"), P(@"C:\New folder")]);

        var rows = Titles(tree);
        var at = Array.IndexOf(rows, "Local Disk (C:)");
        Assert.Equal(["apps", "New folder", "Users", "Windows"], rows[(at + 1)..(at + 5)]);
        Assert.Equal(2, Row(tree, "New folder").Depth);
        Assert.True(Row(tree, "New folder").CanExpand);
        Assert.Equal(listed, source.Listed.Count);
    }

    [Fact]
    public async Task UpdateChildren_ARemovedFolder_LeavesTheTree_AndKeepsTheStateOfTheOthers()
    {
        var (tree, _, _) = Make();
        var drive = Row(tree, "Local Disk (C:)");
        await tree.ExpandAsync(drive, CancellationToken.None);
        var users = Row(tree, "Users");
        await tree.ExpandAsync(users, CancellationToken.None);

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\Users"), P(@"C:\Windows")]);

        Assert.DoesNotContain("apps", Titles(tree));
        Assert.Same(users, Row(tree, "Users"));
        Assert.True(users.IsExpanded);
        Assert.Contains("brett", Titles(tree));
    }

    [Fact]
    public async Task UpdateChildren_ARenamedFolder_ReplacesTheOldRow()
    {
        var (tree, _, _) = Make();
        await tree.ExpandAsync(Row(tree, "Local Disk (C:)"), CancellationToken.None);

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\Users"), P(@"C:\Windows"), P(@"C:\tools")]);

        Assert.DoesNotContain("apps", Titles(tree));
        Assert.Contains("tools", Titles(tree));
    }

    [Fact]
    public async Task UpdateChildren_TheLastSubfolderGone_RemovesTheArrow_AndANewOneBringsItBack()
    {
        var (tree, source, _) = Make();
        source.Folders[@"C:\"] = ["only"];
        var drive = Row(tree, "Local Disk (C:)");
        await tree.ExpandAsync(drive, CancellationToken.None);

        await tree.UpdateChildrenAsync(P(@"C:\"), []);
        Assert.False(drive.CanExpand);
        Assert.False(drive.IsExpanded);
        Assert.DoesNotContain("only", Titles(tree));

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\fresh")]);
        Assert.True(drive.CanExpand);
        await tree.ExpandAsync(drive, CancellationToken.None);
        Assert.Contains("fresh", Titles(tree));
    }

    [Fact]
    public async Task UpdateChildren_ACollapsedButLoadedFolder_GetsItsChildrenForWhenItIsOpened()
    {
        var (tree, _, _) = Make();
        var drive = Row(tree, "Local Disk (C:)");
        await tree.ExpandAsync(drive, CancellationToken.None);
        tree.Collapse(drive);

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\Users"), P(@"C:\added")]);
        Assert.DoesNotContain("added", Titles(tree));
        await tree.ExpandAsync(drive, CancellationToken.None);

        Assert.Contains("added", Titles(tree));
        Assert.DoesNotContain("apps", Titles(tree));
    }

    [Fact]
    public async Task UpdateChildren_AFolderNeverOpenedInTheTree_OnlyLearnsWhetherItHasAnArrow()
    {
        var (tree, source, _) = Make();
        var drive = Row(tree, "Local Disk (C:)");
        var listed = source.Listed.Count;

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\Users")]);

        Assert.False(drive.IsExpanded);
        Assert.True(drive.CanExpand);
        Assert.Equal(listed, source.Listed.Count);
        Assert.DoesNotContain("Users", Titles(tree));
    }

    [Fact]
    public async Task UpdateChildren_TheSameFolders_ChangeNothing()
    {
        var (tree, _, _) = Make();
        await tree.ExpandAsync(Row(tree, "Local Disk (C:)"), CancellationToken.None);
        var changes = 0;
        tree.Rows.CollectionChanged += (_, _) => changes++;

        await tree.UpdateChildrenAsync(P(@"C:\"), [P(@"C:\apps"), P(@"C:\Users"), P(@"C:\Windows")]);

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Expand_ShowsASpinnerAtOnce_ThenTheFoldersInOrder()
    {
        var (tree, source, _) = Make();
        var gate = new TaskCompletionSource<IReadOnlyList<LocationPath>>();
        source.Pending[@"C:\"] = gate;
        var drive = Row(tree, "Local Disk (C:)");

        var expanding = tree.ExpandAsync(drive, CancellationToken.None);

        Assert.Contains("Loading…", Titles(tree));
        gate.SetResult([P(@"C:\Windows"), P(@"C:\apps"), P(@"C:\Users")]);
        await expanding;
        var rows = Titles(tree);
        Assert.DoesNotContain("Loading…", rows);
        var at = Array.IndexOf(rows, "Local Disk (C:)");
        Assert.Equal(["apps", "Users", "Windows"], rows[(at + 1)..(at + 4)]);
        Assert.Equal(2, Row(tree, "apps").Depth);
        Assert.True(drive.IsExpanded);
    }

    [Fact]
    public async Task Expand_OfAFolderWithNoSubfolders_RemovesItsArrow()
    {
        var (tree, source, _) = Make();
        source.Folders[@"C:\"] = ["empty"];
        var drive = Row(tree, "Local Disk (C:)");
        await tree.ExpandAsync(drive, CancellationToken.None);

        var empty = Row(tree, "empty");
        Assert.True(empty.CanExpand);
        await tree.ExpandAsync(empty, CancellationToken.None);

        Assert.False(empty.CanExpand);
        Assert.False(empty.IsExpanded);
        Assert.Equal(["empty"], tree.Rows.Where(r => r.Depth == 2).Select(r => r.Title));
    }

    [Fact]
    public async Task Expand_ThatTimesOut_ShowsNotRespondingWithRetry_AndRetryListsAgain()
    {
        var (tree, source, time) = Make();
        source.Pending[@"C:\"] = new TaskCompletionSource<IReadOnlyList<LocationPath>>();
        var drive = Row(tree, "Local Disk (C:)");

        var expanding = tree.ExpandAsync(drive, CancellationToken.None);
        time.Advance(SidebarTree.ListingTimeout + TimeSpan.FromMilliseconds(1));
        await expanding;

        var retry = Row(tree, "Not responding — Retry");
        Assert.True(retry.IsRetry);
        Assert.DoesNotContain("Loading…", Titles(tree));

        source.Pending.Clear();
        await tree.RetryAsync(retry, CancellationToken.None);

        Assert.DoesNotContain("Not responding — Retry", Titles(tree));
        Assert.Contains("Windows", Titles(tree));
        Assert.Equal(2, source.Listed.Count(l => l == @"C:\"));
    }

    [Theory]
    [InlineData(LocationError.AccessDenied, "Access denied — Retry")]
    [InlineData(LocationError.AuthenticationRequired, "Sign-in required — Retry")]
    [InlineData(LocationError.Unreachable, "Not responding — Retry")]
    public async Task Expand_ThatFails_SaysWhy(LocationError error, string message)
    {
        var (tree, source, _) = Make();
        source.Failures[@"C:\"] = error;

        await tree.ExpandAsync(Row(tree, "Local Disk (C:)"), CancellationToken.None);

        Assert.Contains(message, Titles(tree));
    }

    [Fact]
    public async Task Collapse_DuringALoad_CancelsItAndLeavesNoRows()
    {
        var (tree, source, _) = Make();
        source.Pending[@"C:\"] = new TaskCompletionSource<IReadOnlyList<LocationPath>>();
        var drive = Row(tree, "Local Disk (C:)");
        var expanding = tree.ExpandAsync(drive, CancellationToken.None);

        tree.Collapse(drive);
        await expanding;

        Assert.False(drive.IsExpanded);
        Assert.DoesNotContain("Loading…", Titles(tree));
        Assert.Equal(8, tree.Rows.Count);
    }

    [Fact]
    public async Task Expand_OfAHugeFolder_InsertsEveryRowInOrder_AndCollapseRemovesThemAll()
    {
        var (tree, source, _) = Make();
        source.Folders[@"C:\"] = [.. Enumerable.Range(0, 250).Select(i => "d" + i.ToString("000"))];
        var drive = Row(tree, "Local Disk (C:)");
        var before = tree.Rows.Count;

        await tree.ExpandAsync(drive, CancellationToken.None);

        Assert.Equal(before + 250, tree.Rows.Count);
        var at = tree.Rows.IndexOf(drive);
        Assert.Equal(Enumerable.Range(0, 250).Select(i => "d" + i.ToString("000")), tree.Rows.Skip(at + 1).Take(250).Select(r => r.Title));
        Assert.Equal("Network", tree.Rows[at + 251].Title);

        tree.Collapse(drive);

        Assert.Equal(before, tree.Rows.Count);
    }

    [Fact]
    public async Task Collapse_HidesNestedRows_AndExpandingAgainDoesNotListAgain()
    {
        var (tree, source, _) = Make();
        var drive = Row(tree, "Local Disk (C:)");
        await tree.ExpandAsync(drive, CancellationToken.None);
        await tree.ExpandAsync(Row(tree, "Users"), CancellationToken.None);
        Assert.Contains("brett", Titles(tree));

        tree.Collapse(drive);
        Assert.DoesNotContain("brett", Titles(tree));
        await tree.ExpandAsync(drive, CancellationToken.None);

        Assert.Contains("brett", Titles(tree));
        Assert.Single(source.Listed, @"C:\");
    }

    [Fact]
    public async Task Sync_ExpandsLocalFoldersDownToTheCurrentFolder_AndSelectsIt()
    {
        var (tree, _, _) = Make();

        await tree.SyncAsync(P(@"C:\Users\brett"), CancellationToken.None);

        Assert.Equal(@"C:\Users\brett", tree.Selected!.Location!.Value);
        Assert.True(tree.Selected.IsSelected);
        Assert.Contains("brett", Titles(tree));
        Assert.True(Row(tree, "Users").IsExpanded);
    }

    [Fact]
    public async Task Sync_BelowAHiddenFolder_ShowsTheHiddenFolderAndFollowsThePathThroughIt()
    {
        var (tree, source, _) = Make();
        // AppData is hidden, so the source never lists it under brett; its own children are listed as usual.
        source.Folders[@"C:\Users\brett\AppData"] = ["Local"];
        source.Folders[@"C:\Users\brett\AppData\Local"] = ["Temp"];
        source.Folders[@"C:\Users\brett\AppData\Local\Temp"] = ["thumbs"];

        await tree.SyncAsync(P(@"C:\Users\brett\AppData\Local\Temp\thumbs"), CancellationToken.None);

        Assert.Equal(@"C:\Users\brett\AppData\Local\Temp\thumbs", tree.Selected!.Location!.Value);
        Assert.Contains("AppData", Titles(tree));
        Assert.Contains("thumbs", Titles(tree));
    }

    [Fact]
    public async Task Sync_TheRevealedHiddenFolder_SurvivesAListingThatDoesNotHaveIt_ButGoesWhenTheViewLeaves()
    {
        var (tree, source, _) = Make();
        source.Folders[@"C:\Users\brett\AppData"] = ["Local"];
        await tree.SyncAsync(P(@"C:\Users\brett\AppData\Local"), CancellationToken.None);

        await tree.UpdateChildrenAsync(P(@"C:\Users\brett"), [P(@"C:\Users\brett\Documents")]);
        Assert.Contains("AppData", Titles(tree));

        await tree.SyncAsync(P(@"C:\Users\brett"), CancellationToken.None);
        await tree.UpdateChildrenAsync(P(@"C:\Users\brett"), [P(@"C:\Users\brett\Documents")]);
        Assert.DoesNotContain("AppData", Titles(tree));
    }

    [Fact]
    public async Task Sync_SelectsTheCommonFolderEntryRatherThanItsPlaceUnderTheDrive()
    {
        var (tree, source, _) = Make();

        await tree.SyncAsync(P(@"C:\Users\brett\Documents"), CancellationToken.None);

        Assert.Equal("Common folders", tree.Selected!.Parent!.Title);
        Assert.Empty(source.Listed);
    }

    [Fact]
    public async Task Sync_MovingToAnotherFolder_ChangesTheSelection_AndRaisesTheEvent()
    {
        var (tree, _, _) = Make();
        var raised = 0;
        tree.SelectionChanged += (_, _) => raised++;
        await tree.SyncAsync(P(@"C:\Users"), CancellationToken.None);
        var first = tree.Selected;

        await tree.SyncAsync(P(@"C:\Windows"), CancellationToken.None);

        Assert.False(first!.IsSelected);
        Assert.Equal("Windows", tree.Selected!.Title);
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Sync_OnANetworkPath_NeverListsTheShare_AndSelectsTheDeepestKnownNode()
    {
        var (tree, source, _) = Make();

        await tree.SyncAsync(P(@"\\nas\data\media\inbox"), CancellationToken.None);

        Assert.Empty(source.Listed);
        Assert.Equal("Data (Z:)", tree.Selected!.Title);
    }

    [Fact]
    public async Task Sync_OutsideEveryPlace_ClearsTheSelection()
    {
        var (tree, _, _) = Make();
        await tree.SyncAsync(P(@"C:\Users"), CancellationToken.None);

        await tree.SyncAsync(P(@"Q:\nowhere"), CancellationToken.None);

        Assert.Null(tree.Selected);
    }

    [Fact]
    public async Task ExpandingASection_ShowsItsPlaces_WithoutListing()
    {
        var (tree, source, _) = Make();
        var header = Row(tree, "Local");
        tree.Collapse(header);
        Assert.DoesNotContain("Local Disk (C:)", Titles(tree));

        await tree.ToggleAsync(header, CancellationToken.None);

        Assert.Contains("Local Disk (C:)", Titles(tree));
        Assert.Empty(source.Listed);
    }
}

public sealed class FavoritesAndSectionsTests
{
    private sealed class MemoryStorage : IFavoritesStorage
    {
        public List<FavoriteEntry> Saved { get; set; } = [];

        public bool Fail { get; set; }

        public IReadOnlyList<FavoriteEntry> Load() => Saved;

        public void Save(IReadOnlyList<FavoriteEntry> favorites)
        {
            if (Fail)
            {
                throw new IOException("disk full");
            }

            Saved = [.. favorites];
        }
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void Toggle_PinsThenUnpins_SavesEachTime_AndRaisesChanged()
    {
        var storage = new MemoryStorage();
        var list = new FavoritesList(storage);
        var raised = 0;
        list.Changed += (_, _) => raised++;

        Assert.True(list.Toggle(P(@"D:\Exports")));
        Assert.True(list.Contains(P(@"d:\exports")));
        Assert.Single(storage.Saved);
        Assert.False(list.Toggle(P(@"D:\Exports")));

        Assert.Empty(storage.Saved);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Toggle_WhenSavingFails_StillPinsForThisRun()
    {
        var list = new FavoritesList(new MemoryStorage { Fail = true });

        Assert.True(list.Toggle(P(@"D:\Exports")));

        Assert.True(list.Contains(P(@"D:\Exports")));
    }

    [Fact]
    public void Favorites_AreLoadedFromStorage_AndCanBeRenamed()
    {
        var storage = new MemoryStorage { Saved = [new FavoriteEntry(P(@"D:\Exports"))] };
        var list = new FavoritesList(storage);

        list.Rename(P(@"D:\Exports"), "  Deliveries ");

        Assert.Equal("Deliveries", list.Items[0].Name);
        Assert.Equal("Deliveries", storage.Saved[0].Name);
    }

    [Fact]
    public void Build_PutsMappedDrivesUnderNetwork_AndLeavesOutInternetWhenThereIsNoSyncClient()
    {
        DriveDetails Drive(string root, DriveKind kind, string? unc = null, bool responsive = true) =>
            new(P(root), string.Empty, kind, StorageMedium.Ssd, "NTFS", 1_000_000_000_000, 250_000_000_000, unc, responsive);

        var sections = SidebarSections.Build(
            [new FavoriteEntry(P(@"D:\Exports"))],
            [new SidebarPlace("Home", P(@"C:\Users\brett"), SidebarIcon.Home)],
            [Drive(@"C:\", DriveKind.Fixed), Drive(@"Z:\", DriveKind.Network, @"\\nas\data"), Drive(@"Y:\", DriveKind.Network, @"\\nas\gone", responsive: false)],
            []);

        Assert.Equal(["Favorites", "Common folders", "Tags", "Local", "Network", "Internet"], sections.Select(s => s.Title));
        Assert.Equal(["Local Disk (C:)"], sections[3].Places.Select(p => p.Title));
        Assert.Equal(["Network Drive (Z:)", "Network Drive (Y:)"], sections[4].Places.Select(p => p.Title));
        Assert.Equal(@"\\nas\data", sections[4].Places[0].Detail);
        Assert.Equal("Not responding", sections[4].Places[1].Detail);
        Assert.Equal(0.75, sections[3].Places[0].UsedFraction!.Value, 3);
        Assert.Empty(sections[5].Places);
        Assert.Null(sections[5].EmptyText);
    }
}
