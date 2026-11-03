using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

public sealed class SessionStoreTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static SessionFile Sample() => new(
        SessionFile.CurrentSchemaVersion,
        new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero),
        new SessionState(
        [
            new SessionWindow(
                new WindowBounds(new ScreenRect(10, 20, 1000, 700), IsMaximized: true),
                ShowPreview: false,
                SelectedTab: 1,
                [
                    new TabState(new NavigationSnapshot([P(@"C:\a")], P(@"C:\a\b"), [P(@"C:\a\b\c")]), FolderViewSettings.Default with { Layout = ViewLayout.Thumbnails, Sort = [new SortKey("size", SortDirection.Descending)], ColumnWidths = new Dictionary<string, double> { ["name"] = 321 } }, "Inbox", "#FF8800", true, true, false, [P(@"C:\a\b\1.txt")]),
                    new TabState(new NavigationSnapshot([], P(@"\\nas\media\show"), []), FolderViewSettings.Default, null, null, false, false, false, []),
                ]),
        ]));

    [Fact]
    public async Task ASession_SurvivesAWriteAndARead_WithEverythingInIt()
    {
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");

        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        var window = Assert.Single(loaded!.State.Windows);
        Assert.True(window.Bounds.IsMaximized);
        Assert.Equal(new ScreenRect(10, 20, 1000, 700), window.Bounds.Rect);
        Assert.False(window.ShowPreview);
        Assert.Equal(1, window.SelectedTab);
        var first = window.Tabs[0];
        Assert.Equal(P(@"C:\a\b"), first.History.Current);
        Assert.Equal([P(@"C:\a")], first.History.Back);
        Assert.Equal([P(@"C:\a\b\c")], first.History.Forward);
        Assert.Equal(ViewLayout.Thumbnails, first.View.Layout);
        Assert.Equal(new SortKey("size", SortDirection.Descending), first.View.Sort[0]);
        Assert.Equal(321, first.View.ColumnWidths["name"]);
        Assert.Equal(("Inbox", "#FF8800", true, true), (first.CustomTitle, first.Accent, first.IsPinned, first.IsLocked));
        Assert.Equal([P(@"C:\a\b\1.txt")], first.Selection);
        Assert.Equal(P(@"\\nas\media\show"), window.Tabs[1].History.Current);
    }

    [Fact]
    public async Task TheFile_IsWrittenAtomically_NoTempFileLeftBehind()
    {
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");

        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.True(fs.File.Exists(@"C:\data\session.json"));
        Assert.False(fs.File.Exists(@"C:\data\session.json.tmp"));
    }

    [Fact]
    public async Task AMissingFile_LoadsAsNothing()
    {
        using var store = new JsonFileStore<SessionFile>(new MockFileSystem(), @"C:\data\session.json");

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(store.RecoveredFromCorruption);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("{\"State\":{\"Windows\":[{\"Tabs\":[{\"History\":{\"Current\":\"not a path|||\"}}]}]}}")]
    public async Task AnUnreadableFile_IsSetAside_AndTheAppStartsWithoutIt(string content)
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\data\session.json", new MockFileData(content));
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(loaded);
        Assert.True(store.RecoveredFromCorruption);
        Assert.True(fs.File.Exists(@"C:\data\session.json.corrupt"));
        Assert.False(fs.File.Exists(@"C:\data\session.json"));
    }

    [Fact]
    public async Task PropertiesFromANewerBuild_AreIgnored()
    {
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        await store.SaveAsync(Sample(), TestContext.Current.CancellationToken);
        var text = fs.File.ReadAllText(@"C:\data\session.json").Replace("\"ShowPreview\"", "\"FutureThing\": 42,\n        \"ShowPreview\"", StringComparison.Ordinal);
        fs.File.WriteAllText(@"C:\data\session.json", text);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Single(loaded.State.Windows);
    }
}

public sealed class SessionCaptureTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static WindowManager Manager() => new(TabTestSupport.Services());

    [Fact]
    public async Task CaptureThenRestore_GivesTheSameWindowsAndTabs()
    {
        var source = Manager();
        var w1 = source.OpenWindow(P(@"C:\a"), new WindowBounds(new ScreenRect(5, 6, 900, 600)));
        var second = w1.Tabs.NewTab(P(@"C:\v"));
        await second.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        w1.Tabs.SetPinned(w1.Tabs.Tabs[0], true);
        w1.Tabs.Select(second);
        w1.ShowPreview = false;
        source.OpenWindow(P(@"C:\d1"));

        var state = SessionCapture.Capture(source);
        var target = Manager();
        var opened = SessionCapture.Restore(target, state);

        Assert.Equal(2, opened);
        var restored = target.Windows[0];
        Assert.Equal(new ScreenRect(5, 6, 900, 600), restored.Bounds.Rect);
        Assert.False(restored.ShowPreview);
        Assert.Equal([P(@"C:\a"), P(@"C:\v")], restored.Tabs.Tabs.Select(t => t.History.Current));
        Assert.True(restored.Tabs.Tabs[0].IsPinned);
        Assert.Equal(P(@"C:\v"), restored.Tabs.Selected!.History.Current);
        Assert.True(restored.PlaceExplicitly);
        Assert.Equal(P(@"C:\d1"), target.Windows[1].Tabs.Selected!.History.Current);
    }

    [Fact]
    public void RestoredTabs_AreNotListedUntilTheyAreShown_SoADeadShareNeverDelaysStartup()
    {
        var source = Manager();
        var window = source.OpenWindow(P(@"C:\a"));
        window.Tabs.NewTab(P(@"\\deadnas\share"));
        var state = SessionCapture.Capture(source);

        var target = Manager();
        SessionCapture.Restore(target, state);

        Assert.All(target.Windows[0].Tabs.Tabs, t => Assert.Equal(FolderLoadState.Idle, t.Folder.State));
    }

    [Fact]
    public async Task ARestoredTab_KeepsItsBackHistoryAndSelection()
    {
        var source = Manager();
        var window = source.OpenWindow(P(@"C:\a"));
        var tab = window.Tabs.Selected!;
        await tab.ReloadAsync(CancellationToken.None);
        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        tab.Selection.Click(tab.Items.Rows.First(r => r.Name == "2.txt"));

        var target = Manager();
        SessionCapture.Restore(target, SessionCapture.Capture(source));
        var restored = target.Windows[0].Tabs.Selected!;
        await restored.ReloadAsync(CancellationToken.None);

        Assert.True(restored.History.CanGoBack);
        Assert.Equal([P(@"C:\a\b\2.txt")], restored.Selection.SelectedEntries.Select(e => e.Location));
    }

    [Fact]
    public void AWindowWithoutTabs_IsNotCaptured_AndASavedOneWithoutTabsOpensAtHome()
    {
        var manager = Manager();
        var window = manager.OpenWindow(P(@"C:\a"));
        var empty = new SessionState([new SessionWindow(window.Bounds, true, 0, [])]);

        Assert.True(empty.IsEmpty);
        var target = Manager();
        SessionCapture.Restore(target, empty);

        Assert.Equal(P(@"C:\Home"), target.Windows[0].Tabs.Selected!.History.Current);
    }

    [Fact]
    public void ASelectedIndexOutOfRange_IsClampedNotACrash()
    {
        var state = new SessionState([new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 99,
        [
            new TabState(new NavigationSnapshot([], P(@"C:\a"), []), FolderViewSettings.Default, null, null, false, false, false, []),
        ])]);

        var target = Manager();
        SessionCapture.Restore(target, state);

        Assert.Equal(P(@"C:\a"), target.Windows[0].Tabs.Selected!.History.Current);
    }

    [Fact]
    public void RestoringAWindow_RaisesWindowOpenedOnce()
    {
        var manager = Manager();
        var opened = 0;
        manager.WindowOpened += (_, _) => opened++;
        var state = new SessionState([new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 0,
        [
            new TabState(new NavigationSnapshot([], P(@"C:\a"), []), FolderViewSettings.Default, null, null, false, false, false, []),
        ])]);

        SessionCapture.Restore(manager, state);

        Assert.Equal(1, opened);
    }
}

public sealed class SessionRecorderTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Fs = new MockFileSystem();
            Time = new ManualTimeProvider();
            Windows = new WindowManager(TabTestSupport.Services());
            Store = new JsonFileStore<SessionFile>(Fs, @"C:\data\session.json");
            Recorder = new SessionRecorder(Windows, Store, new InlineUiContext(), Time);
        }

        public MockFileSystem Fs { get; }

        public ManualTimeProvider Time { get; }

        public WindowManager Windows { get; }

        public JsonFileStore<SessionFile> Store { get; }

        public SessionRecorder Recorder { get; }

        public async Task<SessionFile?> OnDiskAsync()
        {
            await Recorder.Saved;
            using var reader = new JsonFileStore<SessionFile>(Fs, @"C:\data\session.json");
            return await reader.LoadAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            Recorder.Dispose();
            Store.Dispose();
        }
    }

    [Fact]
    public async Task AChange_IsWrittenTwoSecondsLater_NotBefore()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        rig.Windows.OpenWindow(P(@"C:\a"));

        rig.Time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Null(await rig.OnDiskAsync());

        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        var saved = await rig.OnDiskAsync();

        Assert.Equal(P(@"C:\a"), Assert.Single(Assert.Single(saved!.State.Windows).Tabs).History.Current);
    }

    [Fact]
    public async Task ABurstOfChanges_IsOneWrite_AtTheEnd()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        rig.Time.Advance(TimeSpan.FromSeconds(1.5));
        window.Tabs.NewTab(P(@"C:\v"));
        rig.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Null(await rig.OnDiskAsync());

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var saved = await rig.OnDiskAsync();

        Assert.Equal(2, saved!.State.TabCount);
    }

    [Fact]
    public async Task Navigating_InATab_IsSaved()
    {
        using var rig = new Rig();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        rig.Recorder.Start();

        await window.Tabs.Selected!.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        var tab = saved!.State.Windows[0].Tabs[0];
        Assert.Equal(P(@"C:\a\b"), tab.History.Current);
        Assert.Equal([P(@"C:\a")], tab.History.Back);
    }

    [Fact]
    public async Task APinChange_AndATabSwitch_AreSaved()
    {
        using var rig = new Rig();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        var other = window.Tabs.NewTab(P(@"C:\v"));
        rig.Recorder.Start();

        window.Tabs.SetPinned(window.Tabs.Tabs[0], true);
        window.Tabs.Select(other);
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        var saved0 = saved!.State.Windows[0];
        Assert.True(saved0.Tabs[0].IsPinned);
        Assert.Equal(1, saved0.SelectedTab);
    }

    [Fact]
    public async Task AWindowThatMovesOrResizes_IsSaved()
    {
        using var rig = new Rig();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        rig.Recorder.Start();

        window.Bounds = new WindowBounds(new ScreenRect(40, 50, 800, 600), true);
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        Assert.Equal(new WindowBounds(new ScreenRect(40, 50, 800, 600), true), saved!.State.Windows[0].Bounds);
    }

    [Fact]
    public async Task AWindowOpenedLater_IsWatchedToo()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        rig.Time.Advance(SessionRecorder.Delay);
        await rig.Recorder.Saved;

        window.Tabs.NewTab(P(@"C:\v"));
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        Assert.Equal(2, saved!.State.TabCount);
    }

    [Fact]
    public async Task SaveFinal_WritesAtOnce_AndLaterClosesDoNotEmptyTheSession()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        window.Tabs.NewTab(P(@"C:\v"));

        await rig.Recorder.SaveFinalAsync();
        rig.Windows.Close(window);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        var saved = await rig.OnDiskAsync();

        Assert.Equal(2, saved!.State.TabCount);
    }

    [Fact]
    public async Task ClosingOneOfTwoWindows_LeavesTheOtherInTheSession()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var keep = rig.Windows.OpenWindow(P(@"C:\a"));
        var drop = rig.Windows.OpenWindow(P(@"C:\v"));

        rig.Windows.Close(drop);
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        Assert.Equal(P(@"C:\a"), Assert.Single(saved!.State.Windows).Tabs[0].History.Current);
        Assert.NotNull(keep);
    }

    [Fact]
    public async Task AnEmptyCapture_NeverOverwritesAGoodSession()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        rig.Time.Advance(SessionRecorder.Delay);
        await rig.Recorder.Saved;

        rig.Windows.Close(window);
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        Assert.Equal(1, saved!.State.TabCount);
    }

    [Fact]
    public async Task ResumeAfterSaveFinal_SavesAgain()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        await rig.Recorder.SaveFinalAsync();
        rig.Recorder.Resume();

        window.Tabs.NewTab(P(@"C:\v"));
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        Assert.Equal(2, saved!.State.TabCount);
    }

    [Fact]
    public async Task ASessionOnDisk_RestoresIntoAFreshApp_LikeAfterACrash()
    {
        using var rig = new Rig();
        rig.Recorder.Start();
        var window = rig.Windows.OpenWindow(P(@"C:\a"));
        await window.Tabs.Selected!.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        window.Tabs.NewTab(P(@"\\nas\media\show"));
        rig.Time.Advance(SessionRecorder.Delay);
        var saved = await rig.OnDiskAsync();

        var fresh = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(fresh, saved!.State);

        Assert.Equal([P(@"C:\a\b"), P(@"\\nas\media\show")], fresh.Windows[0].Tabs.Tabs.Select(t => t.History.Current));
    }
}

public sealed class ProfileCatalogTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static SessionState State(params string[] folders) => new(
    [
        new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 0,
        [.. folders.Select(f => new TabState(new NavigationSnapshot([], P(f), []), FolderViewSettings.Default, null, null, false, false, false, []))]),
    ]);

    private static (ProfileCatalog Catalog, MockFileSystem Fs) Make()
    {
        var fs = new MockFileSystem();
        return (new ProfileCatalog(new JsonFileStore<ProfilesFile>(fs, @"C:\data\profiles.json"), new ManualTimeProvider()), fs);
    }

    [Fact]
    public async Task ASavedProfile_IsFound_IgnoringCase_AndSurvivesAReload()
    {
        var (catalog, fs) = Make();

        await catalog.SaveAsync("NAS triage", State(@"C:\a", @"\\nas\inbox"), TestContext.Current.CancellationToken);
        var reloaded = new ProfileCatalog(new JsonFileStore<ProfilesFile>(fs, @"C:\data\profiles.json"));
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("NAS triage", catalog.Find("nas TRIAGE")!.Name);
        Assert.Equal(2, reloaded.Find("NAS triage")!.State.TabCount);
    }

    [Fact]
    public async Task SavingTheSameNameAgain_ReplacesIt_KeepingItsPlaceInTheList()
    {
        var (catalog, _) = Make();
        await catalog.SaveAsync("One", State(@"C:\a"), TestContext.Current.CancellationToken);
        await catalog.SaveAsync("Two", State(@"C:\a"), TestContext.Current.CancellationToken);

        await catalog.SaveAsync("one", State(@"C:\v", @"C:\d1"), TestContext.Current.CancellationToken);

        Assert.Equal(["one", "Two"], catalog.Profiles.Select(p => p.Name));
        Assert.Equal(2, catalog.Find("One")!.State.TabCount);
    }

    [Fact]
    public async Task Delete_RemovesIt_AndSaysWhetherThereWasOne()
    {
        var (catalog, _) = Make();
        await catalog.SaveAsync("One", State(@"C:\a"), TestContext.Current.CancellationToken);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        Assert.True(await catalog.DeleteAsync("ONE", TestContext.Current.CancellationToken));
        Assert.False(await catalog.DeleteAsync("One", TestContext.Current.CancellationToken));

        Assert.Empty(catalog.Profiles);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007name")]
    public async Task BadNames_AreRefused(string name)
    {
        var (catalog, _) = Make();

        await Assert.ThrowsAsync<ProfileException>(() => catalog.SaveAsync(name, State(@"C:\a"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATooLongName_AndAnEmptySession_AreRefused()
    {
        var (catalog, _) = Make();

        await Assert.ThrowsAsync<ProfileException>(() => catalog.SaveAsync(new string('x', ProfileCatalog.MaxNameLength + 1), State(@"C:\a"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ProfileException>(() => catalog.SaveAsync("Empty", new SessionState([]), TestContext.Current.CancellationToken));
        Assert.Empty(catalog.Profiles);
    }

    [Fact]
    public async Task ACorruptProfilesFile_GivesNoProfiles_NotACrash()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\data\profiles.json", new MockFileData("{{{"));
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(fs, @"C:\data\profiles.json"));

        await catalog.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(catalog.Profiles);
    }
}

public sealed class StartupPlannerTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static SessionState State(string folder) => new(
    [
        new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 0,
        [new TabState(new NavigationSnapshot([], P(folder), []), FolderViewSettings.Default, null, null, false, false, false, [])]),
    ]);

    private static SessionFile Last(string folder) => new(SessionFile.CurrentSchemaVersion, DateTimeOffset.UnixEpoch, State(folder));

    private static async Task<ProfileCatalog> CatalogWithAsync(params string[] names)
    {
        var fs = new MockFileSystem();
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(fs, @"C:\data\profiles.json"));
        foreach (var name in names)
        {
            await catalog.SaveAsync(name, State(@"C:\v"), CancellationToken.None);
        }

        return catalog;
    }

    [Fact]
    public async Task LastSession_IsTheDefault_AndRestoresWhenThereIsOne()
    {
        var plan = StartupPlanner.Decide(new GeneralSettings(), Last(@"C:\a"), await CatalogWithAsync());

        Assert.Equal(StartupAction.LastSession, plan.Action);
        Assert.Equal(1, plan.State!.TabCount);
    }

    [Fact]
    public async Task WithNoSessionYet_TheAppOpensFresh()
    {
        Assert.Equal(StartupAction.Fresh, StartupPlanner.Decide(new GeneralSettings(), null, await CatalogWithAsync()).Action);
        Assert.Equal(StartupAction.Fresh, StartupPlanner.Decide(new GeneralSettings(), new SessionFile(1, DateTimeOffset.UnixEpoch, new SessionState([])), await CatalogWithAsync()).Action);
    }

    [Fact]
    public async Task AChosenProfile_IsOpened()
    {
        var plan = StartupPlanner.Decide(new GeneralSettings { Startup = StartupMode.Profile, StartupProfile = "nas triage" }, Last(@"C:\a"), await CatalogWithAsync("NAS triage"));

        Assert.Equal(StartupAction.Profile, plan.Action);
        Assert.Equal("NAS triage", plan.ProfileName);
        Assert.Equal(P(@"C:\v"), plan.State!.Windows[0].Tabs[0].History.Current);
    }

    [Fact]
    public async Task AChosenProfileThatIsGone_FallsBackToLastSession_AndSaysSo()
    {
        var plan = StartupPlanner.Decide(new GeneralSettings { Startup = StartupMode.Profile, StartupProfile = "Gone" }, Last(@"C:\a"), await CatalogWithAsync("Other"));

        Assert.Equal(StartupAction.LastSession, plan.Action);
        Assert.Contains("Gone", plan.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_ShowsThePicker_OnlyWhenThereIsSomethingToPick()
    {
        var ask = new GeneralSettings { Startup = StartupMode.Ask };

        Assert.Equal(StartupAction.Ask, StartupPlanner.Decide(ask, Last(@"C:\a"), await CatalogWithAsync()).Action);
        Assert.Equal(StartupAction.Ask, StartupPlanner.Decide(ask, null, await CatalogWithAsync("One")).Action);
        Assert.Equal(StartupAction.Fresh, StartupPlanner.Decide(ask, null, await CatalogWithAsync()).Action);
    }
}

public sealed class ProfileCommandTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static async Task<(WindowCommandsTests.Harness Harness, ProfileService Service)> MakeAsync()
    {
        var fs = new MockFileSystem();
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(fs, @"C:\data\profiles.json"), new ManualTimeProvider());

        // The harness opens its own window manager; the service must work on the same one.
        var holder = new WindowCommandsTests.Harness();
        await holder.Ready;
        var service = new ProfileService(holder.Windows, catalog);
        var h = new WindowCommandsTests.Harness(profiles: service);
        await h.Ready;
        return (h, new ProfileService(h.Windows, catalog));
    }

    [Fact]
    public async Task Save_StoresTheOpenWindows_AndSaysSo()
    {
        var (h, _) = await MakeAsync();
        var catalogOwner = new ProfileCatalog(new JsonFileStore<ProfilesFile>(new MockFileSystem(), @"C:\data\p.json"));
        var service = new ProfileService(h.Windows, catalogOwner);
        var commands = new WindowCommandsTests.Harness(profiles: service);
        await commands.Ready;

        var result = await commands.Run(CommandIds.ProfileSave, "NAS triage");

        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal("Saved the open windows as the profile \"NAS triage\".", commands.Shell.Messages[^1]);
        Assert.NotNull(catalogOwner.Find("nas triage"));
    }

    [Fact]
    public async Task Save_WithoutAName_IsDisabled_AndABadNameSaysWhy()
    {
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(new MockFileSystem(), @"C:\data\p.json"));
        var h = new WindowCommandsTests.Harness();
        var service = new ProfileService(h.Windows, catalog);
        var commands = new WindowCommandsTests.Harness(profiles: service);
        await commands.Ready;

        Assert.Equal(CommandResult.Disabled, await commands.Run(CommandIds.ProfileSave, "  "));
        await commands.Run(CommandIds.ProfileSave, new string('x', 100));

        Assert.Contains("at most", commands.Shell.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_Replacing_OpensTheProfilesWindows_ThenClosesTheOldOnes()
    {
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(new MockFileSystem(), @"C:\data\p.json"));
        var first = new WindowCommandsTests.Harness();
        await first.Ready;
        var service = new ProfileService(first.Windows, catalog);
        first.Windows.OpenWindow(P(@"C:"));
        await service.SaveCurrentAsync("Two windows", CancellationToken.None);
        var commands = new WindowCommandsTests.Harness(profiles: service);
        await commands.Ready;

        // Reuse the manager the service works on: open the profile in `first`.
        var closed = 0;
        first.Windows.WindowClosed += (_, _) => closed++;
        var before = first.Windows.Windows.Count;
        var opened = service.Open("TWO WINDOWS", replace: true);

        Assert.Equal(2, opened);
        Assert.Equal(before, closed);
        Assert.Equal(2, first.Windows.Windows.Count);
    }

    [Fact]
    public async Task Open_InAddition_KeepsTheCurrentWindows()
    {
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(new MockFileSystem(), @"C:\data\p.json"));
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var service = new ProfileService(h.Windows, catalog);
        await service.SaveCurrentAsync("One", CancellationToken.None);
        var before = h.Windows.Windows.Count;

        service.Open("One", replace: false);

        Assert.Equal(before + 1, h.Windows.Windows.Count);
    }

    [Fact]
    public async Task OpeningAProfileThatIsGone_SaysSo()
    {
        var catalog = new ProfileCatalog(new JsonFileStore<ProfilesFile>(new MockFileSystem(), @"C:\data\p.json"));
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var service = new ProfileService(h.Windows, catalog);

        Assert.Throws<ProfileException>(() => service.Open("Nope", replace: true));
    }

    [Fact]
    public async Task Open_AndDelete_AreDisabled_ForAProfileThatDoesNotExist()
    {
        var (h, _) = await MakeAsync();

        Assert.False(h.Commands.CanExecute(CommandIds.ProfileOpen, new CommandContext("Nope")));
        Assert.False(h.Commands.CanExecute(CommandIds.ProfileDelete, new CommandContext("Nope")));
    }
}
