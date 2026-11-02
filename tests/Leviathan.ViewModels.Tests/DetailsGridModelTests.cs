using Leviathan.Core.Paths;
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
}
