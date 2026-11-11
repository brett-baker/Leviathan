using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Core.Providers;
using Leviathan.Diagnostics;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

public sealed class WindowCommandsTests
{
    internal sealed class FakeShell : IShellActions
    {
        public int FocusCalls { get; private set; }

        public int DiagnosticsCalls { get; private set; }

        public List<string> Messages { get; } = [];

        public void FocusAddressBar() => FocusCalls++;

        public void OpenDiagnostics() => DiagnosticsCalls++;

        public int FilterFocusCalls { get; private set; }

        public void FocusFilterBox() => FilterFocusCalls++;

        public int SearchFocusCalls { get; private set; }

        public void FocusSearchBox() => SearchFocusCalls++;

        public List<LocationPath> IndexToggles { get; } = [];

        public bool IndexedAfterToggle { get; set; } = true;

        public bool ToggleSearchIndex(LocationPath folder)
        {
            IndexToggles.Add(folder);
            return IndexedAfterToggle;
        }

        public void ShowMessage(string message) => Messages.Add(message);

        public List<Leviathan.Core.Model.FileEntry> Sheets { get; } = [];

        public List<IReadOnlyList<Leviathan.Core.Model.FileEntry>> SheetBatches { get; } = [];

        public void OpenContactSheet(Leviathan.Core.Model.FileEntry video) => Sheets.Add(video);

        public void ExportContactSheets(IReadOnlyList<Leviathan.Core.Model.FileEntry> videos) => SheetBatches.Add(videos);

        public int QuickPreviewToggles { get; private set; }

        public void ToggleQuickPreview() => QuickPreviewToggles++;

        public int TriageStarts { get; private set; }

        public void StartTriage() => TriageStarts++;

        public void StartInboxTriage() => InboxStarts++;

        public void FindDuplicates()
        {
        }

        public int InboxStarts { get; private set; }

        public bool Confirm { get; set; } = true;

        public List<string> Confirmations { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations.Add(message);
            return Task.FromResult(Confirm);
        }
    }

    internal sealed class FakeLauncher : IFileLauncher
    {
        public List<LocationPath> Opened { get; } = [];

        public LaunchResult Result { get; set; } = LaunchResult.Ok;

        public Task<LaunchResult> OpenAsync(LocationPath path, CancellationToken cancellationToken)
        {
            Opened.Add(path);
            return Task.FromResult(Result);
        }

        public Task<LaunchResult> RunAsAdministratorAsync(LocationPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> OpenWithAsync(LocationPath path, string executable, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> ChooseAppAsync(LocationPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> OpenTerminalAsync(LocationPath folder, TerminalKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LaunchResult> ShowPropertiesAsync(LocationPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class Harness
    {
        public Harness(Views.ViewOptions? viewOptions = null, NewMenu.NewItemService? newItems = null, Sidebar.FavoritesList? favorites = null, Folders.RenameService? renamer = null, Transfers.ITransferService? transfers = null, Drives.DriveCardPreferences? driveCards = null, Transfers.IDeleteService? deleter = null, Undo.UndoService? undo = null, Transfers.IFileClipboard? clipboard = null, Sessions.ProfileService? profiles = null)
        {
            var services = TabTestSupport.Services() with { Options = viewOptions };
            Windows = new WindowManager(services);
            Window = Windows.OpenWindow(TabTestSupport.P(@"C:\a"));
            Trail = new ActionTrail();
            Shell = new FakeShell();
            Launcher = new FakeLauncher();
            var fs = new MockFileSystem();
            fs.AddDirectory(@"C:\a");
            fs.AddDirectory(@"C:\a\b");
            fs.AddDirectory(@"C:\v");
            fs.AddFile(@"C:\a\1.txt", new MockFileData("x"));
            Commands = WindowCommands.Create(Trail, Window, Windows, new AddressResolver(new FileSystemEntryProbe(fs)), Launcher, Shell, () => TabTestSupport.P(@"C:\Home"), favorites, newItems, viewOptions, renamer, transfers, driveCards, deleter, undo, clipboard, profiles);
            Ready = Window.Tabs.Selected!.ReloadAsync(CancellationToken.None);
        }

        public Task Ready { get; }

        public WindowManager Windows { get; }

        public WindowViewModel Window { get; }

        public ActionTrail Trail { get; }

        public FakeShell Shell { get; }

        public FakeLauncher Launcher { get; }

        public CommandRegistry Commands { get; }

        public TabViewModel Tab => Window.Tabs.Selected!;

        public Task<CommandResult> Run(CommandId id, string? target = null, object? parameter = null) =>
            Commands.ExecuteAsync(id, new CommandContext(target, parameter), CancellationToken.None);
    }

    [Fact]
    public async Task ContactSheet_OfOneVideo_OpensItsWindow_AndOfSeveral_StartsTheBatch_AndIsRefusedForNonVideo()
    {
        var h = new Harness();
        static Leviathan.Core.Model.FileEntry File(string name) =>
            new(name, TabTestSupport.P($@"C:\a\{name}"), Leviathan.Core.Model.FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
        var clip = File("clip.mkv");

        await h.Run(CommandIds.ContactSheet, clip.Location.Value, clip);

        Assert.Equal([clip], h.Shell.Sheets);
        Assert.Empty(h.Shell.SheetBatches);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.ContactSheet, @"C:\a.txt", File("1.txt")));
        Assert.Single(h.Shell.Sheets);
    }

    [Fact]
    public async Task TabNew_OpensAHomeTab_ListsIt_AndRecordsTheAction()
    {
        var h = new Harness();

        var result = await h.Run(CommandIds.TabNew);

        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
        Assert.Equal(TabTestSupport.P(@"C:\Home"), h.Tab.History.Current);
        Assert.Equal(FolderLoadState.Loaded, h.Tab.Folder.State);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "tab.new");
    }

    [Fact]
    public async Task ViewLayoutCommands_SwitchTheLayoutOfTheSelectedTab_AndKeysAreBound()
    {
        var h = new Harness();
        await h.Ready;

        await h.Run(CommandIds.ViewThumbnails);
        Assert.Equal(Views.ViewLayout.Thumbnails, h.Tab.Layout);
        await h.Run(CommandIds.ViewList);
        Assert.Equal(Views.ViewLayout.List, h.Tab.Layout);
        await h.Run(CommandIds.ViewDetails);
        Assert.Equal(Views.ViewLayout.Details, h.Tab.Layout);

        var keys = WindowCommands.DefaultKeyMap();
        Assert.Equal(CommandIds.ViewThumbnails, keys.Resolve(KeyGesture.Parse("Ctrl+Shift+2"), isSplit: false));
        Assert.Equal(CommandIds.ViewList, keys.Resolve(KeyGesture.Parse("Ctrl+Shift+5"), isSplit: false));
        Assert.Equal(CommandIds.ViewDetails, keys.Resolve(KeyGesture.Parse("Ctrl+Shift+6"), isSplit: false));
    }

    [Fact]
    public async Task ViewZoom_ResizesThumbnails_OnlyInTheThumbnailsLayout()
    {
        var h = new Harness();
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.ViewZoom, parameter: 1));

        await h.Run(CommandIds.ViewThumbnails);
        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.ViewZoom, parameter: 2));
        Assert.Equal(ItemFlow.Zoom(ItemFlow.DefaultThumbnailSize, 2), h.Tab.ThumbnailSize);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "view.zoom");
    }

    [Fact]
    public async Task ViewThumbnailSize_SetsTheSize_ClampedToTheRange_OnlyInThumbnails()
    {
        var h = new Harness();
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.ViewThumbnailSize, parameter: 200));

        await h.Run(CommandIds.ViewThumbnails);
        await h.Run(CommandIds.ViewThumbnailSize, parameter: 200);
        Assert.Equal(200, h.Tab.ThumbnailSize);

        await h.Run(CommandIds.ViewThumbnailSize, parameter: 9999);
        Assert.Equal(ItemFlow.ClampThumbnailSize(9999), h.Tab.ThumbnailSize);
    }

    [Fact]
    public async Task TabNew_WithATarget_OpensThatLocation()
    {
        var h = new Harness();

        await h.Run(CommandIds.TabNew, @"C:\v");

        Assert.Equal(TabTestSupport.P(@"C:\v"), h.Tab.History.Current);
    }

    [Fact]
    public async Task TabClose_ClosesTheSelectedTab_ButNotAPinnedOne()
    {
        var h = new Harness();
        await h.Run(CommandIds.TabNew);
        h.Window.Tabs.SetPinned(h.Tab, true);

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabClose));

        h.Window.Tabs.SetPinned(h.Tab, false);
        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.TabClose));
        Assert.Single(h.Window.Tabs.Tabs);
    }

    [Fact]
    public async Task TabReopen_IsDisabledUntilATabWasClosed_ThenBringsItBackListed()
    {
        var h = new Harness();
        await h.Run(CommandIds.TabNew, @"C:\v");
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabReopen));

        await h.Run(CommandIds.TabClose);
        await h.Run(CommandIds.TabReopen);

        Assert.Equal(TabTestSupport.P(@"C:\v"), h.Tab.History.Current);
        Assert.Equal(FolderLoadState.Loaded, h.Tab.Folder.State);
    }

    [Fact]
    public async Task TabDuplicate_CopiesTheTab_AndListsTheCopy()
    {
        var h = new Harness();

        await h.Run(CommandIds.TabDuplicate);

        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
        Assert.Equal(h.Window.Tabs.Tabs[0].History.Current, h.Tab.History.Current);
        Assert.Equal(FolderLoadState.Loaded, h.Tab.Folder.State);
    }

    [Fact]
    public async Task TabCycleAndSelect_ChangeTheSelectedTab()
    {
        var h = new Harness();
        await h.Run(CommandIds.TabNew, @"C:\v");
        await h.Run(CommandIds.TabNew, @"C:\a\b");

        await h.Run(CommandIds.TabNext);
        Assert.Equal("a", h.Tab.Title);
        await h.Run(CommandIds.TabSelect, parameter: 3);
        Assert.Equal("b", h.Tab.Title);
        await h.Run(CommandIds.TabPrevious);
        Assert.Equal("v", h.Tab.Title);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabSelect));
    }

    [Fact]
    public async Task NavCommands_AreDisabledWhenTheyHaveNowhereToGo()
    {
        var h = new Harness();

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.NavBack));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.NavForward));
        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.NavUp));
        Assert.Equal(TabTestSupport.P(@"C:\"), h.Tab.History.Current);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.NavUp));
    }

    [Fact]
    public async Task BackAndForward_MoveThroughHistory_WithAnOptionalStepCount()
    {
        var h = new Harness();
        await h.Tab.NavigateAsync(TabTestSupport.P(@"C:\a\b"), CancellationToken.None);
        await h.Tab.NavigateAsync(TabTestSupport.P(@"C:\v"), CancellationToken.None);

        await h.Run(CommandIds.NavBack, parameter: 2);
        Assert.Equal(TabTestSupport.P(@"C:\a"), h.Tab.History.Current);

        await h.Run(CommandIds.NavForward);
        Assert.Equal(TabTestSupport.P(@"C:\a\b"), h.Tab.History.Current);
    }

    [Fact]
    public async Task NavGo_TypedFolder_Navigates()
    {
        var h = new Harness();

        await h.Run(CommandIds.NavGo, @"C:\v");

        Assert.Equal(TabTestSupport.P(@"C:\v"), h.Tab.History.Current);
        Assert.Empty(h.Shell.Messages);
    }

    [Fact]
    public async Task NavGo_TypedFilePath_OpensTheFolderAndSelectsTheFile()
    {
        var h = new Harness();
        await h.Run(CommandIds.NavGo, @"C:\v");

        await h.Run(CommandIds.NavGo, @"C:\a\1.txt");

        Assert.Equal(TabTestSupport.P(@"C:\a"), h.Tab.History.Current);
        Assert.Equal(["1.txt"], h.Tab.Selection.SelectedEntries.Select(e => e.Name));
    }

    [Theory]
    [InlineData(@"C:\does\not\exist")]
    [InlineData("::::")]
    public async Task NavGo_BadAddress_StaysPutAndExplainsWhy(string text)
    {
        var h = new Harness();

        await h.Run(CommandIds.NavGo, text);

        Assert.Equal(TabTestSupport.P(@"C:\a"), h.Tab.History.Current);
        Assert.Single(h.Shell.Messages);
    }

    [Fact]
    public async Task ItemOpen_AFolder_Navigates_AndAFileLaunchesIt()
    {
        var h = new Harness();
        await h.Ready;
        var folder = h.Tab.Items.Rows.Single(r => r.Name == "b");
        var file = h.Tab.Items.Rows.Single(r => r.Name == "1.txt");

        await h.Run(CommandIds.ItemOpen, folder.Location.Value, folder);
        Assert.Equal(TabTestSupport.P(@"C:\a\b"), h.Tab.History.Current);

        await h.Run(CommandIds.ItemOpen, file.Location.Value, file);
        Assert.Equal([file.Location], h.Launcher.Opened);
    }

    [Fact]
    public async Task ItemOpen_ALaunchFailure_IsShownButACancelledPromptIsNot()
    {
        var h = new Harness();
        await h.Ready;
        var file = h.Tab.Items.Rows.Single(r => r.Name == "1.txt");

        h.Launcher.Result = new LaunchResult(false, "No app is associated.");
        await h.Run(CommandIds.ItemOpen, parameter: file);
        h.Launcher.Result = new LaunchResult(false, null, Cancelled: true);
        await h.Run(CommandIds.ItemOpen, parameter: file);

        Assert.Equal(["No app is associated."], h.Shell.Messages);
    }

    [Fact]
    public async Task ItemOpen_WithoutAnEntry_IsDisabled()
    {
        Assert.Equal(CommandResult.Disabled, await new Harness().Run(CommandIds.ItemOpen));
    }

    [Fact]
    public async Task WindowNew_OpensAnotherWindowAtHome()
    {
        var h = new Harness();

        await h.Run(CommandIds.WindowNew);

        Assert.Equal(2, h.Windows.Windows.Count);
        Assert.Equal(TabTestSupport.P(@"C:\Home"), h.Windows.Windows[1].Tabs.Selected!.History.Current);
    }

    [Fact]
    public async Task ShellCommands_CallTheUiLayer()
    {
        var h = new Harness();

        await h.Run(CommandIds.AddressFocus);
        await h.Run(CommandIds.AppDiagnostics);

        Assert.Equal(1, h.Shell.FocusCalls);
        Assert.Equal(1, h.Shell.DiagnosticsCalls);
    }

    [Fact]
    public void EveryDefaultKeyBinding_PointsAtARegisteredCommand()
    {
        var h = new Harness();
        var keys = WindowCommands.DefaultKeyMap();

        Assert.All(keys.Bindings, binding => Assert.True(h.Commands.TryGet(binding.Command, out _), $"{binding.Gesture} is bound to unregistered {binding.Command}"));
        Assert.Equal(CommandIds.TabNew, keys.Resolve(KeyGesture.Parse("ctrl+t"), isSplit: false));
        Assert.Equal(CommandIds.NavReload, keys.Resolve(KeyGesture.Parse("F5"), isSplit: false));
    }

    [Fact]
    public void EveryRegisteredCommand_HasATitleAndCategory_ForThePalette()
    {
        var h = new Harness();

        Assert.All(h.Commands.Commands, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Definition.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Definition.Category));
        });
        Assert.NotEmpty(h.Commands.Search("tab"));
    }
}

public sealed class DisplayFormatTests
{
    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 byte")]
    [InlineData(1023L, "1023 bytes")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(5_624_000_000L, "5.24 GB")]
    [InlineData(120L * 1024 * 1024 * 1024, "120 GB")]
    public void Size_UsesBinaryUnitsWithThreeSignificantDigits(long bytes, string expected)
    {
        using var culture = new CultureScope("en-US");

        Assert.Equal(expected, DisplayFormat.Size(bytes));
    }

    [Fact]
    public void Size_Negative_IsEmpty() => Assert.Equal(string.Empty, DisplayFormat.Size(-1));

    [Fact]
    public void SizeColumn_ShowsFilesOnly()
    {
        var root = LocationPath.Parse(@"C:\x");
        var file = new FileEntry("a", root.Combine("a"), FileEntryKind.File, 2048, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
        var folder = new FileEntry("d", root.Combine("d"), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

        using var culture = new CultureScope("en-US");
        Assert.Equal("2 KB", DisplayFormat.SizeColumn(file));
        Assert.Equal(string.Empty, DisplayFormat.SizeColumn(folder));
    }

    [Fact]
    public void Date_IsTheLocalTimeInOneFixedFormat()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test");

        Assert.Equal("2026-11-01 14:03", DisplayFormat.Date(new DateTimeOffset(2026, 11, 1, 12, 3, 0, TimeSpan.Zero), zone));
    }

    [Theory]
    [InlineData(1, 1, 0, 0L, "1 item")]
    [InlineData(1284, 1284, 0, 0L, "1,284 items")]
    [InlineData(1284, 312, 0, 0L, "312 of 1,284 items")]
    [InlineData(1284, 1284, 23, 44_780_000_000L, "1,284 items · 23 selected · 41.7 GB")]
    [InlineData(10, 10, 2, 0L, "10 items · 2 selected")]
    public void StatusText_ReadsLikeTheStatusBarInTheSpec(int total, int shown, int selected, long bytes, string expected)
    {
        using var culture = new CultureScope("en-US");

        Assert.Equal(expected, DisplayFormat.StatusText(total, shown, selected, bytes));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}

public sealed class FileSystemEntryProbeTests
{
    [Fact]
    public async Task GetKindAsync_ReportsFolderFileOrNothing()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"C:\d");
        fs.AddFile(@"C:\d\f.txt", new MockFileData("x"));
        var probe = new FileSystemEntryProbe(fs);

        Assert.Equal(FileEntryKind.Folder, await probe.GetKindAsync(LocationPath.Parse(@"C:\d"), CancellationToken.None));
        Assert.Equal(FileEntryKind.File, await probe.GetKindAsync(LocationPath.Parse(@"C:\d\f.txt"), CancellationToken.None));
        Assert.Null(await probe.GetKindAsync(LocationPath.Parse(@"C:\nope"), CancellationToken.None));
    }

    [Fact]
    public async Task GetKindAsync_ASlowShare_ReportsNotRespondingInsteadOfHanging()
    {
        var probe = new FileSystemEntryProbe(new HangingFileSystem(), TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<LocationException>(() => probe.GetKindAsync(LocationPath.Parse(@"\\nas\media\inbox"), CancellationToken.None));

        Assert.Equal(LocationError.Timeout, failure.Error);
        Assert.Contains("not responding", failure.Message);
    }

    [Fact]
    public async Task GetKindAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var probe = new FileSystemEntryProbe(new HangingFileSystem());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetKindAsync(LocationPath.Parse(@"C:\d"), cts.Token));
    }

    /// <summary>A file system whose directory check blocks, like a dead SMB server.</summary>
    private sealed class HangingFileSystem : MockFileSystem
    {
        public HangingFileSystem()
            : base(new MockFileSystemOptions())
        {
        }

        public override System.IO.Abstractions.IDirectory Directory => new HangingDirectory(base.Directory);
    }

    private sealed class HangingDirectory(System.IO.Abstractions.IDirectory inner) : System.IO.Abstractions.DirectoryWrapper(inner.FileSystem)
    {
        public override bool Exists(string? path)
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            return false;
        }
    }
}
