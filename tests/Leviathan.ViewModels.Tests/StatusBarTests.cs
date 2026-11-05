using System.Globalization;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.StatusBar;

namespace Leviathan.ViewModels.Tests;

public sealed class StatusBarTextTests
{
    public StatusBarTextTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

    private static DriveDetails Drive(string root, long total, long free) =>
        new(LocationPath.Parse(root), "Disk", DriveKind.Fixed, StorageMedium.Ssd, "NTFS", total, free);

    [Fact]
    public void Items_ShowsFoldersAndFiles()
    {
        Assert.Equal("1,284 items (37 folders, 1,247 files)", StatusBarText.Items(1284, 37, 0));
    }

    [Fact]
    public void Items_UsesSingularsAndMentionsHidden()
    {
        Assert.Equal("2 items (1 folder, 1 file) · 12 hidden", StatusBarText.Items(2, 1, 12));
    }

    [Fact]
    public void Items_EmptyFolder() => Assert.Equal("0 items", StatusBarText.Items(0, 0, 0));

    [Fact]
    public void Selection_IsEmptyWithoutASelection_AndShowsSizeWhenKnown()
    {
        Assert.Equal(string.Empty, StatusBarText.Selection(0, 0));
        Assert.Equal("3 selected", StatusBarText.Selection(3, 0));
        Assert.Equal("23 selected · 2 KB", StatusBarText.Selection(23, 2048));
    }

    [Fact]
    public void Filter_ReportsTheMatchCount_OrNothing()
    {
        Assert.Equal(string.Empty, StatusBarText.Filter("  ", 5, 5));
        Assert.Equal("Filtered: *.mkv (312 of 1,284)", StatusBarText.Filter("*.mkv", 312, 1284));
    }

    [Fact]
    public void FreeSpace_NeedsKnownSizes()
    {
        Assert.Equal("1 KB free of 4 KB", StatusBarText.FreeSpace(Drive(@"C:\", 4096, 1024)));
        Assert.Equal(string.Empty, StatusBarText.FreeSpace(Drive(@"C:\", -1, -1)));
        Assert.Equal(string.Empty, StatusBarText.FreeSpace(null));
    }

    [Fact]
    public void Health_OnlyForNetworkLocations()
    {
        Assert.Equal(string.Empty, StatusBarText.Health(LocationPath.Parse(@"C:\a"), offline: false));
        Assert.Equal(@"\\nas\media", StatusBarText.Health(LocationPath.Parse(@"\\nas\media\x"), offline: false));
        Assert.Equal(@"\\nas\media · not responding", StatusBarText.Health(LocationPath.Parse(@"\\nas\media\x"), offline: true));
        Assert.Equal(@"\\nas\media · 1 Gb/s", StatusBarText.Health(LocationPath.Parse(@"\\nas\media\x"), offline: false, linkBitsPerSecond: 1_000_000_000));
        Assert.Equal(@"\\nas\media · not responding", StatusBarText.Health(LocationPath.Parse(@"\\nas\media"), offline: true, linkBitsPerSecond: 1_000_000_000));
        Assert.Equal(string.Empty, StatusBarText.Health(LocationPath.Parse(@"C:\a"), offline: false, linkBitsPerSecond: 1_000_000_000));
    }

    [Fact]
    public void DriveOf_MatchesByRoot_IgnoringCase()
    {
        var drives = new[] { Drive(@"C:\", 10, 5), Drive(@"D:\", 20, 5) };

        Assert.Equal(@"D:\", StatusBarText.DriveOf(LocationPath.Parse(@"d:\x\y"), drives)!.Root.Root);
        Assert.Null(StatusBarText.DriveOf(LocationPath.Parse(@"\\nas\media"), drives));
    }
}

public sealed class FolderCompositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly LocationPath Root = LocationPath.Parse(@"C:\x");

    private static FileEntry Entry(string name, FileEntryKind kind, FileAttributes attributes = FileAttributes.Normal) =>
        new(name, Root.Combine(name), kind, 1, Now, Now, attributes);

    [Fact]
    public void FolderAndHiddenCounts_FollowBatches_AndReset()
    {
        var source = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(source);

        source.AddRange([Entry("a", FileEntryKind.Folder), Entry("b.txt", FileEntryKind.File, FileAttributes.Hidden)]);
        source.AddRange([Entry("c", FileEntryKind.Folder, FileAttributes.Hidden)]);

        Assert.Equal(2, view.FolderCount);
        Assert.Equal(2, view.HiddenCount);

        source.Clear();

        Assert.Equal(0, view.FolderCount);
        Assert.Equal(0, view.HiddenCount);
    }

    [Fact]
    public void FolderCount_IsNotAffectedByTheFilter()
    {
        var source = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(source);
        source.AddRange([Entry("a", FileEntryKind.Folder), Entry("b.txt", FileEntryKind.File)]);

        view.SetFilter("b");

        Assert.Equal(1, view.ShownCount);
        Assert.Equal(1, view.FolderCount);
    }
}
