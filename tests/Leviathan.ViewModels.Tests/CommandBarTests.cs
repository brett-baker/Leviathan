using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.CommandBar;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.NewMenu;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class ContextActionsTests
{
    private static FileEntry File(string name) =>
        new(name, LocationPath.Parse(@"C:\x").Combine(name), FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, LocationPath.Parse(@"C:\x").Combine(name), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    [Theory]
    [InlineData("a.mkv", SelectionClass.Video)]
    [InlineData("a.MP4", SelectionClass.Video)]
    [InlineData("a.jpg", SelectionClass.Image)]
    [InlineData("a.flac", SelectionClass.Audio)]
    [InlineData("a.pdf", SelectionClass.Document)]
    [InlineData("a.txt", SelectionClass.Document)]
    [InlineData("a.zip", SelectionClass.Archive)]
    [InlineData("a.exe", SelectionClass.Executable)]
    [InlineData("a.ps1", SelectionClass.Executable)]
    [InlineData("a.lnk", SelectionClass.Shortcut)]
    [InlineData("a.iso", SelectionClass.DiskImage)]
    [InlineData("a.xyz", SelectionClass.OtherFile)]
    [InlineData("noextension", SelectionClass.OtherFile)]
    public void Classify_OneFile_FollowsItsExtension(string name, SelectionClass expected) =>
        Assert.Equal(expected, ContextActions.Classify([File(name)]));

    [Fact]
    public void Classify_NothingFolderAndSeveral_AreTheirOwnClasses()
    {
        Assert.Equal(SelectionClass.Nothing, ContextActions.Classify([]));
        Assert.Equal(SelectionClass.Folder, ContextActions.Classify([Folder("d")]));
        Assert.Equal(SelectionClass.Several, ContextActions.Classify([File("a.mkv"), File("b.mkv")]));
        Assert.Equal(SelectionClass.Several, ContextActions.Classify([Folder("d"), File("a.txt")]));
    }

    [Fact]
    public void Wanted_EverySelectionClassHasActions_AndEachButtonHasATooltip()
    {
        foreach (var selection in Enum.GetValues<SelectionClass>())
        {
            var wanted = ContextActions.Wanted(selection);
            Assert.NotEmpty(wanted);
            Assert.All(wanted, b =>
            {
                Assert.False(string.IsNullOrWhiteSpace(b.Title));
                Assert.False(string.IsNullOrEmpty(b.Glyph));
                Assert.StartsWith(b.Title, b.Tooltip, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public void BarButton_Tooltip_GivesNameAndShortcut()
    {
        Assert.Equal("Copy (Ctrl+C)", new BarButton(CommandIds.EditCopy, "Copy", "\uE8C8", "Ctrl+C").Tooltip);
        Assert.Equal("Select none", new BarButton(CommandIds.SelectNone, "Select none", "\uE8E6").Tooltip);
    }

    [Fact]
    public void For_ShowsOnlyActionsWhoseCommandExists()
    {
        var h = new WindowCommandsTests.Harness();

        var nothing = ContextActions.For(SelectionClass.Nothing, h.Commands);
        var video = ContextActions.For(SelectionClass.Video, h.Commands);

        Assert.Contains(nothing, b => b.Command == CommandIds.SelectAll);
        Assert.Contains(nothing, b => b.Command == CommandIds.TabNew);
        Assert.Contains(nothing, b => b.Command == CommandIds.SpaceMap);
        Assert.Equal([CommandIds.ItemOpen, CommandIds.TriageFromHere, CommandIds.ContactSheet], video.Select(b => b.Command));
        Assert.Equal([CommandIds.BulkRename, CommandIds.ContactSheet], ContextActions.For(SelectionClass.Several, h.Commands).Select(b => b.Command));
    }

    [Fact]
    public void ContextOf_HandsEachCommandWhatItNeeds()
    {
        var folder = Folder("d");
        var file = File("a.exe");
        var here = LocationPath.Parse(@"C:\here");

        var open = ContextActions.ContextOf(new BarButton(CommandIds.TabOpenInNewTab, "Open in new tab", "x"), [folder], here);
        var run = ContextActions.ContextOf(new BarButton(CommandIds.RunAsAdministrator, "Run", "x"), [file], here);
        var terminal = ContextActions.ContextOf(new BarButton(CommandIds.TerminalHere, "Terminal", "x"), [], here);

        Assert.Equal(folder.Location.Value, open.Target);
        Assert.Equal(true, open.Parameter);
        Assert.Same(file, run.Parameter);
        Assert.Equal(here.Value, terminal.Target);
    }
}

public sealed class CommandBarCommandsTests
{
    private sealed class MemoryStorage : IFavoritesStorage
    {
        public IReadOnlyList<FavoriteEntry> Load() => [];

        public void Save(IReadOnlyList<FavoriteEntry> favorites)
        {
        }
    }

    private sealed class FakeCatalog(params NewItemTemplate[] templates) : INewItemCatalog
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<NewItemTemplate>>([.. ShellNewCatalog.BuiltIn, .. templates]);
        }
    }

    private static (WindowCommandsTests.Harness Harness, MockFileSystem Disk, FakeCatalog Catalog) WithNewItems(Views.ViewOptions? options = null)
    {
        var disk = new MockFileSystem();
        disk.AddDirectory(@"C:\a");
        var catalog = new FakeCatalog();
        var harness = new WindowCommandsTests.Harness(options, new NewItemService(catalog, new NewItemCreator(disk)));
        return (harness, disk, catalog);
    }

    [Fact]
    public async Task SelectCommands_SelectAllInvertAndClear()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SelectAll);
        Assert.Equal(h.Tab.Items.Rows.Count, h.Tab.Selection.SelectedEntries.Count);
        await h.Run(CommandIds.SelectInvert);
        Assert.Empty(h.Tab.Selection.SelectedEntries);
        await h.Run(CommandIds.SelectInvert);
        await h.Run(CommandIds.SelectNone);
        Assert.Empty(h.Tab.Selection.SelectedEntries);
        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.SelectByPattern, "*.txt"));
        Assert.Equal("1 item matches", h.Shell.Messages[^1]);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.SelectByPattern, " "));
        Assert.Equal(CommandIds.SelectAll, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Ctrl+A"), isSplit: false));
    }

    [Fact]
    public async Task NewFolder_CreatesTheFolderInTheCurrentFolder_AndNumbersTheNext()
    {
        var (h, disk, _) = WithNewItems();
        await h.Ready;

        await h.Run(CommandIds.NewFolder);
        await h.Run(CommandIds.NewFolder);

        Assert.True(disk.Directory.Exists(@"C:\a\New folder"));
        Assert.True(disk.Directory.Exists(@"C:\a\New folder (2)"));
        Assert.Equal("Created New folder (2)", h.Shell.Messages[^1]);
        Assert.Equal(CommandIds.NewFolder, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Ctrl+Shift+N"), isSplit: false));
    }

    [Fact]
    public async Task NewItem_CreatesTheTemplate_AndAnUnknownTemplateIsReported()
    {
        var (h, disk, catalog) = WithNewItems();
        await h.Ready;

        await h.Run(CommandIds.NewItem, "builtin:txt");
        await h.Run(CommandIds.NewItem, "ext:nothing");

        Assert.True(disk.File.Exists(@"C:\a\New Text Document.txt"));
        Assert.Contains("Could not create it", h.Shell.Messages[^1], StringComparison.Ordinal);
        Assert.Equal(1, catalog.Calls);
    }

    [Fact]
    public async Task NewFolder_SkipsNamesThatFilesAlreadyTake()
    {
        var (h, disk, _) = WithNewItems();
        await h.Ready;
        disk.AddFile(@"C:\a\New folder", new MockFileData("x"));
        disk.AddFile(@"C:\a\New folder (2)", new MockFileData("x"));

        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.NewFolder));

        Assert.True(disk.Directory.Exists(@"C:\a\New folder (3)"));
    }

    [Fact]
    public async Task NewCommands_AreDisabledWithoutAServiceOrOffTheLocalDisk()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.NewFolder));

        var (withService, _, _) = WithNewItems();
        await withService.Ready;
        await withService.Tab.NavigateAsync(LocationPath.Parse("sftp://host/dir"), CancellationToken.None);
        Assert.Equal(CommandResult.Disabled, await withService.Run(CommandIds.NewFolder));
    }

    [Fact]
    public async Task NewItemService_ReadsTheCatalogOnce_UntilRefreshed()
    {
        var catalog = new FakeCatalog();
        var service = new NewItemService(catalog, new NewItemCreator(new MockFileSystem()));

        await service.TemplatesAsync();
        await service.TemplatesAsync();
        service.Refresh();
        await service.TemplatesAsync();

        Assert.Equal(2, catalog.Calls);
    }

    [Fact]
    public async Task Sort_ChoosesColumnAndDirectionSeparately_AndHeaderClicksStillFlip()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SortBy, "size");
        Assert.Equal(new SortKey("size"), h.Tab.Items.Sort[0]);
        await h.Run(CommandIds.SortBy, "size");
        Assert.Equal(SortDirection.Ascending, h.Tab.Items.Sort[0].Direction);
        await h.Run(CommandIds.SortDirection, "desc");
        Assert.Equal(new SortKey("size", SortDirection.Descending), h.Tab.Items.Sort[0]);
        await h.Run(CommandIds.SortBy, "modified");
        Assert.Equal(new SortKey("modified", SortDirection.Descending), h.Tab.Items.Sort[0]);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.SortBy, "nonsense"));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.SortDirection, "sideways"));
    }

    [Fact]
    public async Task GroupAndFolderPlacement_ChangeTheTabsView()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.GroupBy, "type");
        Assert.Equal("type", h.Tab.Items.GroupBy);
        await h.Run(CommandIds.GroupBy, string.Empty);
        Assert.Null(h.Tab.Items.GroupBy);
        await h.Run(CommandIds.FolderPlacement, "last");
        Assert.Equal(FolderPlacement.Last, h.Tab.Items.Folders);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.FolderPlacement, "middle"));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.GroupBy, "attributes"));
    }

    [Fact]
    public async Task Filter_NarrowsTheRows_AndNavigatingClearsIt()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        await h.Tab.NavigateAsync(LocationPath.Parse(@"C:\v"), CancellationToken.None);
        Assert.Equal(3, h.Tab.Items.Rows.Count);

        await h.Run(CommandIds.FilterSet, "*.mkv");

        Assert.Equal(["x.mkv", "y.mkv"], h.Tab.Items.Rows.Select(r => r.Name));
        Assert.Equal("*.mkv", h.Tab.FilterText);

        await h.Tab.NavigateAsync(LocationPath.Parse(@"C:\a"), CancellationToken.None);
        Assert.Equal(string.Empty, h.Tab.FilterText);
        Assert.Equal(2, h.Tab.Items.Rows.Count);

        await h.Run(CommandIds.FilterFocus);
        Assert.Equal(1, h.Shell.FilterFocusCalls);
    }

    [Fact]
    public async Task ViewHidden_TogglesTheSharedOption_AndTheTabListsWithIt()
    {
        var options = new Views.ViewOptions();
        var h = new WindowCommandsTests.Harness(options);
        await h.Ready;
        Assert.False(h.Tab.Folder.Options.IncludeHidden);

        await h.Run(CommandIds.ViewHidden);

        Assert.True(options.ShowHidden);
        Assert.True(h.Tab.Folder.Options.IncludeHidden);
        await h.Run(CommandIds.ViewHidden);
        Assert.False(h.Tab.Folder.Options.IncludeHidden);
        Assert.Equal(CommandResult.Disabled, await new WindowCommandsTests.Harness().Run(CommandIds.ViewHidden));
    }

    [Fact]
    public async Task PreviewPane_TogglesTheWindowsPane_AltP()
    {
        var h = new WindowCommandsTests.Harness();
        Assert.True(h.Window.ShowPreview);

        await h.Run(CommandIds.PreviewPane);

        Assert.False(h.Window.ShowPreview);
        Assert.Equal(CommandIds.PreviewPane, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Alt+P"), isSplit: false));
    }

    [Fact]
    public async Task FavoriteAdd_PinsTheFolderInTarget_AndDoesNotUnpinAnExistingOne()
    {
        var favorites = new FavoritesList(new MemoryStorage());
        var h = new WindowCommandsTests.Harness(favorites: favorites);
        await h.Ready;

        await h.Run(CommandIds.FavoriteAdd, @"C:\a\b");
        await h.Run(CommandIds.FavoriteAdd, @"C:\a\b");

        Assert.True(favorites.Contains(LocationPath.Parse(@"C:\a\b")));
        Assert.Equal("b is already in Favorites", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task TabReloadAndSelect_SelectsTheItem_AfterTheListingEnds()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var wanted = h.Tab.Items.Rows.First(r => r.Name == "1.txt").Location;
        h.Tab.Selection.Clear();

        await h.Tab.ReloadAndSelectAsync(wanted, CancellationToken.None);

        Assert.Equal([wanted], h.Tab.Selection.SelectedEntries.Select(e => e.Location));
    }

    [Fact]
    public async Task ClipboardCommands_AreNotRegisteredYet_SoTheirButtonsStayDisabled()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        foreach (var id in new[] { CommandIds.EditCut, CommandIds.EditCopy, CommandIds.EditPaste, CommandIds.EditDelete, CommandIds.EditRename, CommandIds.EditUndo, CommandIds.EditRedo })
        {
            Assert.False(h.Commands.CanExecute(id));
        }
    }
}
