using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class ColumnLayoutTests
{
    [Fact]
    public void Ctor_DefaultColumns_ShowNameTagsSizeModified()
    {
        var layout = new ColumnLayout(FolderViewSettings.DefaultColumns);

        Assert.Equal(["name", "tags", "size", "modified"], layout.Ids);
    }

    [Fact]
    public void Ctor_NameMissing_NameIsAddedFirst_UnknownIdsAreSkipped()
    {
        var layout = new ColumnLayout(["size", "bogus", "size"]);

        Assert.Equal(["name", "size"], layout.Ids);
    }

    [Fact]
    public void Resize_ClampsToMinAndMax_AndRaisesWidthsOnly()
    {
        var layout = new ColumnLayout(["name", "size"]);
        var structure = new List<bool>();
        layout.Changed += (_, s) => structure.Add(s);

        layout.Resize("size", 5);
        Assert.Equal(ColumnLayout.MinWidth, layout.Slots[1].Width);
        layout.Resize("size", 5000);
        Assert.Equal(ColumnLayout.MaxWidth, layout.Slots[1].Width);
        layout.Resize("size", 5000);

        Assert.Equal([false, false], structure);
    }

    [Fact]
    public void Move_ReordersColumns_AndRaisesStructure()
    {
        var layout = new ColumnLayout(["name", "size", "modified"]);
        bool? structure = null;
        layout.Changed += (_, s) => structure = s;

        layout.Move("modified", 1);

        Assert.Equal(["name", "modified", "size"], layout.Ids);
        Assert.True(structure);
    }

    [Fact]
    public void Move_PastTheEnd_GoesLast()
    {
        var layout = new ColumnLayout(["name", "size", "modified"]);

        layout.Move("name", 99);

        Assert.Equal(["size", "modified", "name"], layout.Ids);
    }

    [Fact]
    public void SetVisible_Name_CannotBeHidden()
    {
        var layout = new ColumnLayout(["name", "size"]);

        layout.SetVisible("name", false);

        Assert.True(layout.IsVisible("name"));
    }

    [Fact]
    public void SetVisible_HideThenShow_ReturnsWithItsWidthAtTheEnd()
    {
        var layout = new ColumnLayout(["name", "size", "modified"]);
        layout.Resize("size", 222);

        layout.SetVisible("size", false);
        Assert.Equal(["name", "modified"], layout.Ids);
        layout.SetVisible("size", true);

        Assert.Equal(["name", "modified", "size"], layout.Ids);
        Assert.Equal(222, layout.Slots[2].Width);
    }

    [Fact]
    public void Widths_IncludeHiddenColumns_SoTheyRoundTrip()
    {
        var layout = new ColumnLayout(["name", "size"]);
        layout.Resize("size", 200);
        layout.SetVisible("size", false);

        var again = new ColumnLayout(["name", "size"], layout.Widths);

        Assert.Equal(200, again.Slots[1].Width);
    }

    [Fact]
    public void AutoFit_UsesTheWidestTextPlusPadding()
    {
        var layout = new ColumnLayout(["name", "size"]);

        layout.AutoFit("size", ["1 KB", "512 MB", ""], text => text.Length * 7);

        Assert.Equal(6 * 7 + ColumnLayout.CellPadding, layout.Slots[1].Width);
    }

    [Fact]
    public void AutoFit_ExtraWidthForTheIcon_IsAdded()
    {
        var layout = new ColumnLayout(["name"]);

        layout.AutoFit("name", ["abc"], text => text.Length * 10, extra: 30);

        Assert.Equal(30 + 30 + ColumnLayout.CellPadding, layout.Slots[0].Width);
    }

    [Fact]
    public void IndexAt_FindsTheColumnUnderAnX()
    {
        var layout = new ColumnLayout(["name", "size", "modified"]);
        layout.Resize("name", 100);
        layout.Resize("size", 100);

        Assert.Equal(0, layout.IndexAt(99));
        Assert.Equal(1, layout.IndexAt(100));
        Assert.Equal(2, layout.IndexAt(10_000));
        Assert.Equal(200, layout.OffsetOf("modified"));
    }

    [Fact]
    public async Task Tab_ColumnChanges_StayAfterNavigatingAndAreInTheCurrentView()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), TabTestSupport.P(@"C:\a"));
        tab.Columns.SetVisible("created", true);
        tab.Columns.Resize("name", 500);

        await tab.NavigateAsync(TabTestSupport.P(@"C:\a\b"), CancellationToken.None);

        Assert.True(tab.Columns.IsVisible("created"));
        Assert.Contains("created", tab.CurrentView().Columns);
        Assert.Equal(500, tab.CurrentView().ColumnWidths["name"]);
    }
}

public sealed class FolderSizeTrackerTests
{
    private sealed class FakeSource : IFolderSizeSource
    {
        public Dictionary<LocationPath, FolderSizeInfo> Known { get; } = [];

        public List<IReadOnlyList<Leviathan.Core.Model.FileEntry>> Requests { get; } = [];

        public bool Local { get; set; } = true;

        public event EventHandler<IReadOnlyList<LocationPath>>? Computed;

        public bool CanCompute(LocationPath folder) => Local;

        public FolderSizeInfo? TryGet(LocationPath folder) => Known.TryGetValue(folder, out var info) ? info : null;

        public void Request(IReadOnlyList<Leviathan.Core.Model.FileEntry> folders) => Requests.Add(folders);

        public void Raise(params LocationPath[] folders) => Computed?.Invoke(this, folders);
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static async Task<(TabViewModel Tab, FakeSource Source)> OpenAsync(Action<FakeSource>? setup = null)
    {
        var source = new FakeSource();
        setup?.Invoke(source);
        var services = TabTestSupport.Services() with { FolderSizes = source };
        var tab = new TabViewModel(services, P(@"C:\a"));
        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        return (tab, source);
    }

    [Fact]
    public async Task Loaded_RequestsTheFoldersWithoutAKnownSize()
    {
        var (_, source) = await OpenAsync();

        var request = Assert.Single(source.Requests);
        Assert.Equal([P(@"C:\a\b\c")], request.Select(e => e.Location));
    }

    [Fact]
    public async Task Loaded_FreshKnownSize_IsNotRequestedAgain()
    {
        var (_, source) = await OpenAsync(s => s.Known[P(@"C:\a\b\c")] = new FolderSizeInfo(2048));

        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task Loaded_StaleSize_IsRequestedAgainButStillShown()
    {
        var (tab, source) = await OpenAsync(s => s.Known[P(@"C:\a\b\c")] = new FolderSizeInfo(2048, IsStale: true));

        var folder = tab.Items.Rows.First(r => r.Name == "c");
        Assert.Single(source.Requests);
        Assert.Equal("2 KB", tab.Sizes.TextFor(folder));
        Assert.True(tab.Sizes.IsStale(folder));
    }

    [Fact]
    public async Task TextFor_Folder_ShowsPlaceholderThenSizeThenLowerBound()
    {
        var (tab, source) = await OpenAsync();
        var folder = tab.Items.Rows.First(r => r.Name == "c");
        Assert.Equal("…", tab.Sizes.TextFor(folder));

        source.Known[folder.Location] = new FolderSizeInfo(1024 * 1024, IsLowerBound: true);

        Assert.Equal("≥ 1 MB", tab.Sizes.TextFor(folder));
    }

    [Fact]
    public async Task TextFor_File_IsItsOwnSize()
    {
        var (tab, _) = await OpenAsync();

        Assert.Equal("2 bytes", tab.Sizes.TextFor(tab.Items.Rows.First(r => r.Name == "2.txt")));
    }

    [Fact]
    public async Task Computed_ForARequestedFolder_RaisesChanged_OthersAreIgnored()
    {
        var (tab, source) = await OpenAsync();
        var changed = new List<LocationPath>();
        tab.Sizes.Changed += (_, folders) => changed.AddRange(folders);

        source.Raise(P(@"C:\elsewhere"));
        source.Raise(P(@"C:\a\b\c"));

        Assert.Equal([P(@"C:\a\b\c")], changed);
    }

    [Fact]
    public async Task NotLocal_FoldersAreNeitherRequestedNorShownAsPending()
    {
        var (tab, source) = await OpenAsync(s => s.Local = false);

        Assert.Empty(source.Requests);
        Assert.Equal(string.Empty, tab.Sizes.TextFor(tab.Items.Rows.First(r => r.Name == "c")));
    }

    [Fact]
    public async Task NoSource_FoldersShowNoSize()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\a"));
        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);

        Assert.Equal(string.Empty, tab.Sizes.TextFor(tab.Items.Rows.First(r => r.Name == "c")));
    }

    [Fact]
    public void Begin_ManyFolders_RequestsAtMostTheLimit()
    {
        var source = new FakeSource();
        var tracker = new FolderSizeTracker(source, new InlineUiContext());
        var rows = Enumerable.Range(0, FolderSizeTracker.MaxRequested + 30)
            .Select(i => new Leviathan.Core.Model.FileEntry($"d{i}", P($@"C:\x\d{i}"), Leviathan.Core.Model.FileEntryKind.Folder, 0, default, default, FileAttributes.Directory))
            .ToList();

        tracker.Begin(rows);

        Assert.Equal(FolderSizeTracker.MaxRequested, Assert.Single(source.Requests).Count);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static Leviathan.Core.Model.FileEntry Folder(string path) =>
        new(path[(path.LastIndexOf('\\') + 1)..], P(path), Leviathan.Core.Model.FileEntryKind.Folder, 0, default, default, FileAttributes.Directory);

    [Fact]
    public void TextFor_FolderOnAShare_ShowsItsAge_ALocalFolderDoesNot()
    {
        var source = new FakeSource();
        source.Known[P(@"Y:\media")] = new FolderSizeInfo(1024, ComputedAt: Now.AddDays(-3), IsNetwork: true);
        source.Known[P(@"C:\local")] = new FolderSizeInfo(1024, ComputedAt: Now.AddDays(-3));
        var tracker = new FolderSizeTracker(source, new InlineUiContext(), new ManualTimeProvider(Now));

        Assert.Equal("1 KB · 3 d", tracker.TextFor(Folder(@"Y:\media")));
        Assert.Equal("1 KB", tracker.TextFor(Folder(@"C:\local")));
    }

    [Fact]
    public void DetailFor_SaysWhatIsInsideAndWhenItWasCounted_AndStaysEmptyWhenUnknown()
    {
        var source = new FakeSource();
        source.Known[P(@"Y:\media")] = new FolderSizeInfo(2048, IsLowerBound: true, IsStale: true, Files: 1234, Folders: 1, ComputedAt: Now.AddHours(-5), IsNetwork: true);
        var tracker = new FolderSizeTracker(source, new InlineUiContext(), new ManualTimeProvider(Now));

        Assert.Equal("at least 2 KB · 1,234 files in 1 folder · out of date, counted 5 hours ago", tracker.DetailFor(P(@"Y:\media")));
        Assert.Equal(string.Empty, tracker.DetailFor(P(@"Y:\unknown")));
    }

    [Fact]
    public void RequestNow_AsksOnceForAFolderWithoutAFreshSize()
    {
        var source = new FakeSource();
        source.Known[P(@"C:\done")] = new FolderSizeInfo(1);
        var tracker = new FolderSizeTracker(source, new InlineUiContext());

        tracker.RequestNow(Folder(@"C:\done"));
        tracker.RequestNow(Folder(@"C:\new"));
        tracker.RequestNow(Folder(@"C:\new"));

        Assert.Equal([P(@"C:\new")], Assert.Single(source.Requests).Select(e => e.Location));
    }

    [Fact]
    public void TextFor_Metrics_ShowCountsWithSeparators_AndStayBlankForFilesAndUnknownItems()
    {
        var source = new FakeSource();
        source.Known[P(@"C:\full")] = new FolderSizeInfo(5000, Files: 12345, Folders: 80, OwnBytes: 2048, OwnFiles: 3, OwnFolders: 2);
        source.Known[P(@"C:\old")] = new FolderSizeInfo(5000, Files: 7, Folders: 1, OwnFiles: 3);
        source.Known[P(@"C:\busy")] = new FolderSizeInfo(5000, IsLowerBound: true, Files: 9, Folders: 2);
        var tracker = new FolderSizeTracker(source, new InlineUiContext());
        var file = new Leviathan.Core.Model.FileEntry("a.txt", P(@"C:\a.txt"), Leviathan.Core.Model.FileEntryKind.File, 5, default, default, FileAttributes.Normal);

        Assert.Equal("2 KB", tracker.TextFor(Folder(@"C:\full"), FolderMetric.OwnBytes));
        Assert.Equal("5", tracker.TextFor(Folder(@"C:\full"), FolderMetric.Items));
        Assert.Equal("12,345", tracker.TextFor(Folder(@"C:\full"), FolderMetric.TotalFiles));
        Assert.Equal("80", tracker.TextFor(Folder(@"C:\full"), FolderMetric.TotalFolders));
        Assert.Equal(string.Empty, tracker.TextFor(Folder(@"C:\old"), FolderMetric.Items));
        Assert.Equal("≥ 9", tracker.TextFor(Folder(@"C:\busy"), FolderMetric.TotalFiles));
        Assert.Equal(string.Empty, tracker.TextFor(file, FolderMetric.TotalFiles));
        Assert.Equal("5 bytes", tracker.TextFor(file, FolderMetric.TotalBytes));
    }

    [Fact]
    public void ProgressFor_FollowsTheSource_ForFoldersOnly()
    {
        var source = new ProgressSource { Progress = new FolderProgress(true, 0.25) };
        var tracker = new FolderSizeTracker(source, new InlineUiContext());
        var file = new Leviathan.Core.Model.FileEntry("a.txt", P(@"C:\a.txt"), Leviathan.Core.Model.FileEntryKind.File, 5, default, default, FileAttributes.Normal);

        Assert.Equal(new FolderProgress(true, 0.25), tracker.ProgressFor(Folder(@"C:\x")));
        Assert.Null(tracker.ProgressFor(file));
        source.Progress = null;
        Assert.Null(tracker.ProgressFor(Folder(@"C:\x")));
    }

    private sealed class ProgressSource : IFolderSizeSource
    {
        public FolderProgress? Progress { get; set; }

        public event EventHandler<IReadOnlyList<LocationPath>>? Computed
        {
            add { }
            remove { }
        }

        public bool CanCompute(LocationPath folder) => true;

        public FolderSizeInfo? TryGet(LocationPath folder) => null;

        public void Request(IReadOnlyList<Leviathan.Core.Model.FileEntry> folders)
        {
        }

        public FolderProgress? ProgressOf(LocationPath folder) => Progress;
    }
}
