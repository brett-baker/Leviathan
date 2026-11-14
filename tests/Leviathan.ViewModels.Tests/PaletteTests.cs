using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Palette;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>The fuzzy match and the two palettes (spec §5.3, §15.6).</summary>
public sealed class PaletteTests
{
    [Theory]
    [InlineData("ntab", "New tab", true)]
    [InlineData("new tab", "New tab", true)]
    [InlineData("NT", "new tab", true)]
    [InlineData("tabnew", "New tab", false)]
    [InlineData("xyz", "New tab", false)]
    [InlineData("newtabs", "New tab", false)]
    [InlineData("", "anything", true)]
    public void Score_RequiresTheLettersInOrder_IgnoringCaseAndSpaces(string query, string text, bool matches)
    {
        Assert.Equal(matches, FuzzyMatcher.Score(query, text) is not null);
    }

    [Fact]
    public void Score_PrefersWordStarts_Runs_AndTheStartOfTheText()
    {
        var wordStarts = FuzzyMatcher.Score("nt", "New tab")!.Value;
        var middle = FuzzyMatcher.Score("nt", "Nonstop contents")!.Value;
        var run = FuzzyMatcher.Score("tab", "New tab")!.Value;
        var scattered = FuzzyMatcher.Score("tab", "Toggle alt bar")!.Value;
        var prefix = FuzzyMatcher.Score("doc", "Documents")!.Value;
        var late = FuzzyMatcher.Score("doc", "My old docs")!.Value;

        Assert.True(wordStarts > 0 && run > scattered, "a run beats scattered letters");
        Assert.True(prefix > late, "the start of the text beats later");
        Assert.True(wordStarts >= middle);
    }

    [Fact]
    public void Score_ReportsWhereTheLettersMatched()
    {
        var matched = new List<int>();

        FuzzyMatcher.Score("ntb", "New tab", matched);

        Assert.Equal([0, 4, 6], matched);
    }

    [Fact]
    public void Score_ChoosesTheBestAlignment_NotTheFirstOne()
    {
        var matched = new List<int>();

        FuzzyMatcher.Score("fo", "a foo fob", matched);

        // "fo" at the start of a word, as a run, is better than the f of 'a foo' with a later o.
        Assert.Equal([2, 3], matched);
    }

    private static (PaletteModel Model, Harness Harness) Make(IReadOnlyList<LocationPath>? favorites = null, IReadOnlyList<LocationPath>? recent = null, IReadOnlyList<PaletteTab>? tabs = null)
    {
        var h = new Harness();
        var model = new PaletteModel(h.Commands, WindowCommands.DefaultKeyMap(), () => favorites ?? [], () => recent ?? [], () => tabs ?? []);
        return (model, h);
    }

    [Fact]
    public void Commands_ListsCommandsWithTheirShortcuts_AndLeavesOutThePalettesAndDeadOnes()
    {
        var (model, _) = Make();

        var rows = model.Search(PaletteMode.Commands, string.Empty);

        Assert.All(rows, r => Assert.Equal(PaletteKind.Command, r.Kind));
        Assert.DoesNotContain(rows, r => r.Command == CommandIds.PaletteCommands || r.Command == CommandIds.PaletteGoTo);
        Assert.DoesNotContain(rows, r => r.Command == CommandIds.ItemOpen);
        var newTab = rows.Single(r => r.Command == CommandIds.TabNew);
        Assert.Equal("New tab", newTab.Title);
        Assert.NotNull(newTab.Shortcut);
    }

    [Fact]
    public void Commands_TypingRanksTheBestMatchFirst_AndMarksTheLetters()
    {
        var (model, _) = Make();

        var rows = model.Search(PaletteMode.Commands, "new tab");

        Assert.Equal(CommandIds.TabNew, rows[0].Command);
        Assert.NotEmpty(rows[0].TitleMatches);
        Assert.All(rows, r => Assert.True(r.Score >= rows[^1].Score));
    }

    [Fact]
    public void GoTo_WithNothingTyped_ListsTabsThenFavoritesThenRecentThenCommands()
    {
        var tabs = new[] { new PaletteTab(1, "Photos", LocationPath.Parse(@"D:\Photos")) };
        var (model, _) = Make([LocationPath.Parse(@"D:\Work")], [LocationPath.Parse(@"D:\Old")], tabs);

        var rows = model.Search(PaletteMode.GoTo, string.Empty);

        Assert.Equal([PaletteKind.Tab, PaletteKind.Favorite, PaletteKind.Recent], rows.Take(3).Select(r => r.Kind));
        Assert.Contains(rows, r => r.Kind == PaletteKind.Command);
    }

    [Fact]
    public void GoTo_FindsAFolderByNameOrByPath_AndShowsEachFolderOnce()
    {
        var work = LocationPath.Parse(@"D:\Projects\Leviathan");
        var (model, _) = Make([work], [work, LocationPath.Parse(@"E:\Backups\Other")]);

        var byName = model.Search(PaletteMode.GoTo, "levi");
        var byPath = model.Search(PaletteMode.GoTo, "backups");

        var hit = Assert.Single(byName, r => r.Kind != PaletteKind.Command);
        Assert.Equal(PaletteKind.Favorite, hit.Kind);
        Assert.Equal(work, hit.Location);
        Assert.Contains(byPath, r => r.Location?.Value == @"E:\Backups\Other");
    }

    [Fact]
    public void GoTo_AFolderBeatsACommandOfTheSameScore_AndALeadingGreaterThanMeansCommandsOnly()
    {
        var (model, _) = Make([LocationPath.Parse(@"D:\Tabs")]);

        var mixed = model.Search(PaletteMode.GoTo, "tab");
        var commandsOnly = model.Search(PaletteMode.GoTo, ">tab");

        Assert.Equal(PaletteKind.Favorite, mixed[0].Kind);
        Assert.All(commandsOnly, r => Assert.Equal(PaletteKind.Command, r.Kind));
        Assert.NotEmpty(commandsOnly);
    }

    [Fact]
    public async Task RunningACommand_GoesThroughTheRegistry()
    {
        var (model, h) = Make();
        await h.Ready;
        var row = model.Search(PaletteMode.Commands, "new tab")[0];

        var result = await model.RunAsync(row, inNewTab: false, context: null, CancellationToken.None);

        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
    }

    [Fact]
    public async Task RunningAFolder_NavigatesHere_OrOpensANewTab()
    {
        var folder = TabTestSupport.P(@"C:\a\b");
        var (model, h) = Make([folder]);
        await h.Ready;
        var row = model.Search(PaletteMode.GoTo, "b")[0];

        await model.RunAsync(row with { Kind = PaletteKind.Favorite, Location = folder }, inNewTab: false, context: null, CancellationToken.None);
        Assert.Equal(folder, h.Tab.History.Current);

        var before = h.Window.Tabs.Tabs.Count;
        await model.RunAsync(row with { Kind = PaletteKind.Favorite, Location = folder }, inNewTab: true, context: null, CancellationToken.None);
        Assert.Equal(before + 1, h.Window.Tabs.Tabs.Count);
    }

    [Fact]
    public async Task RunningATab_SwitchesToIt()
    {
        var (model, h) = Make();
        await h.Ready;
        await h.Run(CommandIds.TabNew, @"C:\a\b");
        var row = new PaletteItem(PaletteKind.Tab, "first", "", null, 0, []) { TabNumber = 1 };

        await model.RunAsync(row, inNewTab: false, context: null, CancellationToken.None);

        Assert.Same(h.Window.Tabs.Tabs[0], h.Window.Tabs.Selected);
    }

    [Fact]
    public async Task TheKeys_OpenTheRightPalette()
    {
        var h = new Harness();
        await h.Ready;
        var keys = WindowCommands.DefaultKeyMap();

        await h.Run(CommandIds.PaletteCommands);
        await h.Run(CommandIds.PaletteGoTo);

        Assert.Equal([PaletteMode.Commands, PaletteMode.GoTo], h.Shell.Palettes);
        Assert.Equal(CommandIds.PaletteCommands, keys.Resolve(KeyGesture.Parse("Ctrl+Shift+P"), false));
        Assert.Equal(CommandIds.PaletteGoTo, keys.Resolve(KeyGesture.Parse("Ctrl+P"), false));
    }
}
