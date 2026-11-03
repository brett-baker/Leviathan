using System.IO.Abstractions.TestingHelpers;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void ThePages_AreTheOnesOfTheSpec_InOrder()
    {
        Assert.Equal(
            ["General", "Appearance", "Files and folders", "Tabs and windows", "Network", "Operations", "Delete", "Media and preview", "Tags", "Search and index", "Keyboard", "Advanced"],
            SettingsCatalog.Pages.Select(p => p.Title));
        Assert.Equal(SettingsCatalog.Pages.Count, SettingsCatalog.Pages.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void APageNotBuiltYet_SaysWhenItArrives_AndWhatItWillHold()
    {
        foreach (var page in SettingsCatalog.Pages.Where(p => !p.IsAvailable))
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Arrives));
            Assert.NotEmpty(page.Planned!);
        }

        foreach (var page in SettingsCatalog.Pages.Where(p => p.IsAvailable))
        {
            Assert.Null(page.Planned);
        }
    }

    [Fact]
    public void EveryEntry_BelongsToARealPage()
    {
        Assert.All(SettingsCatalog.Entries, e => Assert.Contains(SettingsCatalog.Pages, p => p.Id == e.PageId));
    }

    [Fact]
    public void Page_FallsBackToTheFirstForAnUnknownId()
    {
        Assert.Equal("files", SettingsCatalog.Page("files").Id);
        Assert.Equal(SettingsCatalog.DefaultPage, SettingsCatalog.Page("nope").Id);
        Assert.Equal(SettingsCatalog.DefaultPage, SettingsCatalog.Page(null).Id);
    }

    [Theory]
    [InlineData("hidden", "files", "Show hidden files")]
    [InlineData("HIDDEN", "files", "Show hidden files")]
    [InlineData("font awesome", "appearance", "Icon set")]
    [InlineData("startup", "general", "What to open at startup")]
    [InlineData("bandwidth", "operations", "Bandwidth limits and schedule")]
    [InlineData("ffmpeg", "media", "Where ffmpeg is")]
    public void Search_FindsASetting_ByTitleOrKeyword_IgnoringCase(string text, string page, string title)
    {
        Assert.Contains(SettingsCatalog.Search(text), e => e.PageId == page && e.Title == title);
    }

    [Fact]
    public void Search_NeedsEveryWord_AndMatchesThePageName()
    {
        Assert.Contains(SettingsCatalog.Search("files hidden"), e => e.Title == "Show hidden files");
        Assert.Empty(SettingsCatalog.Search("hidden bandwidth"));
        Assert.All(SettingsCatalog.Search("network"), e => Assert.Equal("network", e.PageId));
    }

    [Fact]
    public void Search_ListsWorkingSettingsBeforePlannedOnes()
    {
        var results = SettingsCatalog.Search("size");

        Assert.NotEmpty(results);
        var firstPlanned = results.ToList().FindIndex(e => !SettingsCatalog.Page(e.PageId).IsAvailable);
        if (firstPlanned >= 0)
        {
            Assert.All(results.Skip(firstPlanned), e => Assert.False(SettingsCatalog.Page(e.PageId).IsAvailable));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_OfNothing_FindsNothing(string? text) => Assert.Empty(SettingsCatalog.Search(text));
}

public sealed class SettingsSheetModelTests
{
    [Fact]
    public void TheSheet_StartsClosedOnTheGeneralPage()
    {
        var sheet = new SettingsSheetModel();

        Assert.False(sheet.IsOpen);
        Assert.False(sheet.IsExpanded);
        Assert.Equal("general", sheet.PageId);
        Assert.Equal(0.7, SettingsSheetModel.HeightFraction);
    }

    [Fact]
    public void Open_ShowsAPage_ClearsTheSearch_AndReopensWhereItWas()
    {
        var sheet = new SettingsSheetModel();
        sheet.Open("files");
        sheet.Close();

        sheet.Open();

        Assert.True(sheet.IsOpen);
        Assert.Equal("files", sheet.PageId);
    }

    [Fact]
    public void Open_OnAnUnknownPage_ShowsTheDefault()
    {
        var sheet = new SettingsSheetModel();

        sheet.Open("made-up");

        Assert.Equal("general", sheet.PageId);
    }

    [Fact]
    public void Close_Collapses_AndClearsTheSearch()
    {
        var sheet = new SettingsSheetModel();
        sheet.Open();
        sheet.IsExpanded = true;
        sheet.SearchText = "hidden";

        sheet.Close();

        Assert.False(sheet.IsOpen);
        Assert.False(sheet.IsExpanded);
        Assert.Equal(string.Empty, sheet.SearchText);
    }

    [Fact]
    public void Dismiss_FirstClearsTheSearch_ThenCloses_AndDoesNothingWhenClosed()
    {
        var sheet = new SettingsSheetModel();
        Assert.False(sheet.Dismiss());

        sheet.Open();
        sheet.SearchText = "hidden";
        Assert.True(sheet.Dismiss());
        Assert.True(sheet.IsOpen);
        Assert.Equal(string.Empty, sheet.SearchText);

        Assert.True(sheet.Dismiss());
        Assert.False(sheet.IsOpen);
    }

    [Fact]
    public void Searching_ReplacesThePageWithResults_UntilAPageIsChosen()
    {
        var sheet = new SettingsSheetModel();
        sheet.Open();
        var changed = new List<string?>();
        sheet.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        sheet.SearchText = "hidden";

        Assert.True(sheet.IsSearching);
        Assert.Contains(sheet.Results, r => r.Title == "Show hidden files");
        Assert.Contains("Results", changed);
        Assert.Contains("IsSearching", changed);

        sheet.ShowPage("files");
        Assert.False(sheet.IsSearching);
        Assert.Equal("files", sheet.PageId);
    }

    [Fact]
    public void Toggle_OpensAndCloses()
    {
        var sheet = new SettingsSheetModel();

        sheet.Toggle("advanced");
        Assert.True(sheet.IsOpen);
        Assert.Equal("advanced", sheet.PageId);

        sheet.Toggle();
        Assert.False(sheet.IsOpen);
    }

    [Fact]
    public async Task TheSettingsCommand_OpensTheSheetOnAPage_AndCtrlCommaIsItsShortcut()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SettingsOpen, parameter: "files");
        Assert.True(h.Window.SettingsSheet.IsOpen);
        Assert.Equal("files", h.Window.SettingsSheet.PageId);

        await h.Run(CommandIds.SettingsOpen);
        Assert.False(h.Window.SettingsSheet.IsOpen);
        Assert.Equal(CommandIds.SettingsOpen, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Ctrl+,"), isSplit: false));
    }
}

public sealed class PerformanceSettingsTests
{
    [Fact]
    public void TheDefaults_KeepWhatTheAppDoesToday()
    {
        var performance = new PerformanceSettings();

        Assert.Equal(2, performance.MaxBackgroundJobs);
        Assert.Equal(3, performance.MaxRunningJobs);
        Assert.Equal(1, performance.MaxPerDevice);
        Assert.True(performance.FolderSizesOnNetwork);
        Assert.True(performance.ThumbnailsOnNetwork);
    }

    [Fact]
    public void Clamped_KeepsEveryNumberInsideItsLimits()
    {
        var wild = new PerformanceSettings { MaxBackgroundJobs = 500, MaxRunningJobs = -3, MaxPerDevice = 99 }.Clamped();

        Assert.Equal(8, wild.MaxBackgroundJobs);
        Assert.Equal(1, wild.MaxRunningJobs);
        Assert.Equal(4, wild.MaxPerDevice);
    }

    [Fact]
    public async Task AHandEditedFile_IsClampedWhenLoaded_AndAnOldFileGetsTheDefaults()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\data\settings.json", new MockFileData("{ \"Performance\": { \"MaxBackgroundJobs\": 500, \"ThumbnailsOnNetwork\": false } }"));
        using var store = new SettingsStore(disk, @"C:\data\settings.json");

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(8, settings.Performance.MaxBackgroundJobs);
        Assert.False(settings.Performance.ThumbnailsOnNetwork);
        Assert.Equal(3, settings.Performance.MaxRunningJobs);

        var old = new MockFileSystem();
        old.AddFile(@"C:\data\settings.json", new MockFileData("{ \"Appearance\": { \"Theme\": \"Dark\" } }"));
        using var oldStore = new SettingsStore(old, @"C:\data\settings.json");
        Assert.Equal(2, (await oldStore.LoadAsync(CancellationToken.None)).Performance.MaxBackgroundJobs);
    }

    [Fact]
    public async Task AChange_IsClampedBeforeItIsSaved_AndRaisesChanged()
    {
        var disk = new MockFileSystem();
        using var store = new SettingsStore(disk, @"C:\data\settings.json");
        await store.LoadAsync(CancellationToken.None);
        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;

        var saved = await store.UpdateAsync(s => s with { Performance = s.Performance with { MaxRunningJobs = 99 } }, CancellationToken.None);

        Assert.Equal(16, saved.Performance.MaxRunningJobs);
        Assert.Equal(16, seen?.Performance.MaxRunningJobs);
    }
}
