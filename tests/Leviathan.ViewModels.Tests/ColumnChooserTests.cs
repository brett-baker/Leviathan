using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class ColumnChooserTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void TheCatalog_FollowsTheSpecGroups_WithUniqueIds()
    {
        Assert.Equal(["File system", "Leviathan", "Media", "Image", "Audio", "Document"], ColumnCatalog.All.Select(c => c.Group).Distinct());
        Assert.Equal(ColumnCatalog.All.Count, ColumnCatalog.All.Select(c => c.Id).Distinct().Count());
        Assert.Contains(ColumnCatalog.All, c => c.Title == "Size on disk");
        Assert.Contains(ColumnCatalog.All, c => c.Title == "Frame rate");
    }

    [Fact]
    public void ACatalogColumn_IsAvailableExactlyWhenItExists_AndEveryExistingColumnIsListed()
    {
        Assert.All(ColumnCatalog.All, c => Assert.Equal(FileColumns.TryGet(c.Id, out _), c.Available));
        Assert.All(FileColumns.All, c => Assert.Contains(ColumnCatalog.All, i => i.Id == c.Id && i.Available));
    }

    [Fact]
    public void Search_MatchesTitlesAndGroups_IgnoringCase_AndDropsEmptyGroups()
    {
        var model = new ColumnChooserModel(["name"]) { Search = "DATE" };

        var groups = model.Groups();

        Assert.Equal(["File system", "Leviathan", "Image"], groups.Select(g => g.Title));
        Assert.Contains(groups[0].Columns, c => c.Title == "Date modified");
        Assert.Equal(["Duration", "Resolution"], new ColumnChooserModel(["name"]) { Search = "media" }.Groups()[0].Columns.Take(2).Select(c => c.Title));
        Assert.Empty(new ColumnChooserModel(["name"]) { Search = "zzzz" }.Groups());
    }

    [Fact]
    public void ATickedColumn_GoesToTheEnd_AndUntickingRemovesIt()
    {
        var model = new ColumnChooserModel(["name", "size"]);

        Assert.True(model.SetChecked("created", true));
        Assert.Equal(["name", "size", "created"], model.Result);
        Assert.True(model.SetChecked("size", false));
        Assert.Equal(["name", "created"], model.Result);
    }

    [Fact]
    public void NameCannotBeUnticked_AndAColumnWithoutDataCannotBeTicked()
    {
        var model = new ColumnChooserModel(["name"]);

        Assert.False(model.SetChecked("name", false));
        Assert.False(model.SetChecked("duration", true));
        Assert.False(model.SetChecked("no-such-column", true));
        Assert.False(model.SetChecked("name", true));
        Assert.Equal(["name"], model.Result);
    }

    [Fact]
    public void Move_ReordersTheChosenColumns_AndClamps()
    {
        var model = new ColumnChooserModel(["name", "size", "modified", "created"]);

        Assert.True(model.Move("created", 1));
        Assert.Equal(["name", "created", "size", "modified"], model.Result);
        Assert.True(model.Move("name", 99));
        Assert.Equal(["created", "size", "modified", "name"], model.Result);
        Assert.False(model.Move("name", 3));
        Assert.False(model.Move("duration", 0));
        Assert.Equal(model.Result, model.Order);
    }

    [Fact]
    public void Reset_GoesBackToTheDefaultSet()
    {
        var model = new ColumnChooserModel(["name", "created", "attributes"]);

        model.Reset();

        Assert.Equal(FolderViewSettings.DefaultColumns, model.Result);
    }

    [Fact]
    public void ListsWithoutName_OrWithUnknownIdsAndDuplicates_AreCleaned()
    {
        var model = new ColumnChooserModel(["size", "SIZE", "bogus", "modified"]);

        Assert.Equal(["name", "size", "modified"], model.Result);
    }

    [Fact]
    public void ChangesAreAnnounced_OnlyWhenSomethingChanged()
    {
        var model = new ColumnChooserModel(["name", "size"]);
        var changes = 0;
        model.Changed += (_, _) => changes++;

        model.SetChecked("size", true);
        model.Move("size", 1);
        model.SetChecked("created", true);

        Assert.Equal(1, changes);
    }
}

public sealed class ColumnLayoutChoiceTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void SetColumns_ChangesSetAndOrder_KeepsWidths_AndAnnouncesOnce()
    {
        var layout = new ColumnLayout(["name", "size", "modified"], new Dictionary<string, double> { ["size"] = 150 });
        var announced = new List<bool>();
        layout.Changed += (_, structure) => announced.Add(structure);

        layout.SetColumns(["name", "created", "size"]);

        Assert.Equal(["name", "created", "size"], layout.Ids);
        Assert.Equal(150, layout.Slots.Single(s => s.Column.Id == "size").Width);
        Assert.Equal([true], announced);
    }

    [Fact]
    public void SetColumns_WithTheSameColumns_AnnouncesNothing()
    {
        var layout = new ColumnLayout(["name", "size"]);
        var changes = 0;
        layout.Changed += (_, _) => changes++;

        layout.SetColumns(["name", "size"]);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void AHiddenColumnThatComesBack_HasTheWidthItHad()
    {
        var layout = new ColumnLayout(["name", "size", "modified"]);
        layout.Resize("modified", 222);
        layout.SetColumns(["name", "size"]);

        layout.SetColumns(["name", "size", "modified"]);

        Assert.Equal(222, layout.Slots.Single(s => s.Column.Id == "modified").Width);
    }

    [Fact]
    public void SetColumns_AddsNameWhenMissing_AndSkipsUnknownIds()
    {
        var layout = new ColumnLayout(["name"]);

        layout.SetColumns(["size", "bogus"]);

        Assert.Equal(["name", "size"], layout.Ids);
    }

    [Fact]
    public async Task ApplyingForThisTab_DoesNotTouchOtherFolders()
    {
        var rules = new FolderViewRules();
        var services = TabTestSupport.Services(rules: rules);
        var tab = new TabViewModel(services, P(@"C:\a"));
        var other = new TabViewModel(services, P(@"C:\v"));
        await tab.ReloadAsync(CancellationToken.None);
        await other.ReloadAsync(CancellationToken.None);

        tab.ApplyColumns(["name", "created"], forAllOfType: false);

        Assert.Equal(["name", "created"], tab.Columns.Ids);
        Assert.Equal(FolderViewSettings.DefaultColumns, other.Columns.Ids);
        Assert.Empty(rules.Snapshot());
    }

    [Fact]
    public async Task ApplyingForAllOfType_SavesTheRule_SoLaterFoldersOfThatKindUseIt()
    {
        var rules = new FolderViewRules();
        var services = TabTestSupport.Services(rules: rules);
        var tab = new TabViewModel(services, P(@"C:\a"));
        await tab.ReloadAsync(CancellationToken.None);

        tab.ApplyColumns(["name", "attributes"], forAllOfType: true);
        var later = new TabViewModel(services, P(@"C:\a"));
        await later.ReloadAsync(CancellationToken.None);

        var rule = Assert.Single(rules.Snapshot());
        Assert.Equal(ViewScope.AllFoldersOfType, rule.Scope);
        Assert.Equal(["name", "attributes"], rule.Settings.Columns);
        Assert.Equal(["name", "attributes"], later.Columns.Ids);
    }

    [Fact]
    public async Task TheCommand_AppliesTheChoice_AndNeedsAChoice()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        var refused = await h.Run(CommandIds.ViewColumns);
        await h.Run(CommandIds.ViewColumns, parameter: new ColumnChoice(["name", "size", "created"], false));

        Assert.Equal(CommandResult.Disabled, refused);
        Assert.Equal(["name", "size", "created"], h.Tab.Columns.Ids);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "view.columns");
    }
}

public sealed class ViewRulesPersistenceTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task ARuleIsWrittenWhenItChanges_AndComesBackAfterARestart()
    {
        var fs = new MockFileSystem();
        var rules = new FolderViewRules();
        using var persistence = new ViewRulesPersistence(rules, new JsonFileStore<ViewRulesFile>(fs, @"C:\data\viewrules.json"));
        persistence.Start();

        rules.Set(P(@"\\nas\media"), ViewScope.ThisFolderAndSubfolders, FolderViewSettings.Default with { Layout = ViewLayout.Thumbnails, Columns = ["name", "size"] });
        rules.SetForKind(FolderKind.Videos, FolderViewSettings.Default with { Columns = ["name", "created"] });
        await persistence.Saved;

        var fresh = new FolderViewRules();
        using var reloaded = new ViewRulesPersistence(fresh, new JsonFileStore<ViewRulesFile>(fs, @"C:\data\viewrules.json"));
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.Equal(2, fresh.Snapshot().Count);
        Assert.Equal(["name", "size"], fresh.Resolve(P(@"\\nas\media\show"), FolderKind.General).Columns);
        Assert.Equal(["name", "created"], fresh.Resolve(P(@"C:\x"), FolderKind.Videos).Columns);
    }

    [Fact]
    public async Task LoadingRules_DoesNotWriteThemBack()
    {
        var fs = new MockFileSystem();
        var seed = new FolderViewRules();
        seed.SetForKind(FolderKind.Music, FolderViewSettings.Default with { Columns = ["name", "type"] });
        using (var first = new ViewRulesPersistence(seed, new JsonFileStore<ViewRulesFile>(fs, @"C:\data\viewrules.json")))
        {
            first.Start();
            seed.SetForKind(FolderKind.Music, FolderViewSettings.Default with { Columns = ["name", "type"] });
            await first.Saved;
        }

        var stamp = fs.File.GetLastWriteTimeUtc(@"C:\data\viewrules.json");
        var fresh = new FolderViewRules();
        using var second = new ViewRulesPersistence(fresh, new JsonFileStore<ViewRulesFile>(fs, @"C:\data\viewrules.json"));
        second.Start();
        await second.LoadAsync(CancellationToken.None);
        await second.Saved;

        Assert.Equal(stamp, fs.File.GetLastWriteTimeUtc(@"C:\data\viewrules.json"));
        Assert.Single(fresh.Snapshot());
    }

    [Fact]
    public async Task ACorruptFile_MeansNoSavedRules()
    {
        var fs = new MockFileSystem();
        fs.AddFile(@"C:\data\viewrules.json", new MockFileData("{ nope"));
        var rules = new FolderViewRules();
        using var persistence = new ViewRulesPersistence(rules, new JsonFileStore<ViewRulesFile>(fs, @"C:\data\viewrules.json"));

        await persistence.LoadAsync(CancellationToken.None);

        Assert.Empty(rules.Snapshot());
    }
}
