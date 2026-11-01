using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Tests;

public sealed class TabMenuCommandsTests
{
    private static LocationPath P(string path) => TabTestSupport.P(path);

    private static async Task<WindowCommandsTests.Harness> ThreeTabs()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        await h.Run(CommandIds.TabNew, @"C:\v");
        await h.Run(CommandIds.TabNew, @"C:\a\b");
        return h;
    }

    private static string[] Titles(WindowCommandsTests.Harness h) => [.. h.Window.Tabs.Tabs.Select(t => t.Title)];

    [Fact]
    public async Task TogglePin_PinsTheGivenTab_MovesItLeft_AndTogglesBack()
    {
        var h = await ThreeTabs();
        var last = h.Window.Tabs.Tabs[2];

        await h.Run(CommandIds.TabTogglePin, parameter: last);
        Assert.True(last.IsPinned);
        Assert.Equal(["b", "a", "v"], Titles(h));

        await h.Run(CommandIds.TabTogglePin, parameter: last);
        Assert.False(last.IsPinned);
    }

    [Fact]
    public async Task TogglePin_WithoutAParameter_UsesTheSelectedTab()
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabTogglePin);

        Assert.True(h.Tab.IsPinned);
    }

    [Fact]
    public async Task ToggleLock_Toggles()
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabToggleLock);
        Assert.True(h.Tab.IsLocked);
        await h.Run(CommandIds.TabToggleLock);
        Assert.False(h.Tab.IsLocked);
    }

    [Fact]
    public async Task ALockedTab_OpensAFolderInANewTab_AndStaysWhereItIs()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var locked = h.Tab;
        await h.Run(CommandIds.TabToggleLock);

        await locked.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        await WaitUntil(() => h.Window.Tabs.Tabs.Count == 2);

        Assert.Equal(P(@"C:\a"), locked.History.Current);
        Assert.Equal(P(@"C:\a\b"), h.Window.Tabs.Selected!.History.Current);
        Assert.NotSame(locked, h.Window.Tabs.Selected);
    }

    [Fact]
    public async Task ALockedTab_ThatMovedToAnotherWindow_NoLongerOpensTabsInTheFirst()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        await h.Run(CommandIds.TabNew, @"C:\v");
        var moving = h.Tab;
        await h.Run(CommandIds.TabToggleLock);
        var other = h.Windows.OpenWindow(P(@"C:\a"));

        h.Windows.Merge(h.Window, moving, other);
        await moving.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        await Task.Delay(50);

        Assert.Single(h.Window.Tabs.Tabs);
    }

    [Fact]
    public async Task Rename_SetsAndClearsTheLabel_AndLimitsItsLength()
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabRename, "NAS inbox");
        Assert.Equal("NAS inbox", h.Tab.Title);

        await h.Run(CommandIds.TabRename, new string('x', 100));
        Assert.Equal(60, h.Tab.Title.Length);

        await h.Run(CommandIds.TabRename, "   ");
        Assert.Equal("b", h.Tab.Title);
    }

    [Theory]
    [InlineData("#ff8800", "#FF8800")]
    [InlineData("  #00AAFF ", "#00AAFF")]
    public async Task Color_AcceptsHex_AndClearsOnEmpty(string value, string expected)
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabColor, value);
        Assert.Equal(expected, h.Tab.Accent);

        await h.Run(CommandIds.TabColor, "");
        Assert.Null(h.Tab.Accent);
    }

    [Theory]
    [InlineData("orange")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    public async Task Color_RejectsOtherText_WithAMessage(string value)
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabColor, value);

        Assert.Null(h.Tab.Accent);
        Assert.Single(h.Shell.Messages);
    }

    [Fact]
    public async Task CloseOthers_AndCloseRight_RespectPinnedTabs_AndAreDisabledWhenThereIsNothingToClose()
    {
        var h = await ThreeTabs();
        var first = h.Window.Tabs.Tabs[0];
        h.Window.Tabs.SetPinned(first, true);

        await h.Run(CommandIds.TabCloseRight, parameter: h.Window.Tabs.Tabs[1]);
        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabCloseRight, parameter: h.Window.Tabs.Tabs[1]));

        await h.Run(CommandIds.TabNew, @"C:\v");
        await h.Run(CommandIds.TabCloseOthers, parameter: h.Tab);

        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
        Assert.Contains(first, h.Window.Tabs.Tabs);
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabCloseOthers, parameter: h.Tab));
    }

    [Fact]
    public async Task MoveLeftAndRight_NudgeOnePlace_WithinTheirZone()
    {
        var h = await ThreeTabs();
        var second = h.Window.Tabs.Tabs[1];

        await h.Run(CommandIds.TabMoveLeft, parameter: second);
        Assert.Equal(["v", "a", "b"], Titles(h));
        await h.Run(CommandIds.TabMoveRight, parameter: second);
        Assert.Equal(["a", "v", "b"], Titles(h));
    }

    [Fact]
    public async Task Move_PlacesATabAtTheIndex()
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabMove, parameter: new TabMove(h.Window.Tabs.Tabs[2], 0));

        Assert.Equal(["b", "a", "v"], Titles(h));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabMove));
    }

    [Fact]
    public async Task TearOff_MakesTheTabTheOnlyTabOfANewPlacedWindow()
    {
        var h = await ThreeTabs();
        var tab = h.Window.Tabs.Tabs[1];

        await h.Run(CommandIds.TabTearOff, parameter: new TabTearOff(tab, 900, 200));

        Assert.Equal(2, h.Windows.Windows.Count);
        var torn = h.Windows.Windows[1];
        Assert.Same(tab, torn.Tabs.Selected);
        Assert.True(torn.PlaceExplicitly);
        Assert.Equal(900, torn.Bounds.Rect.X);
        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
    }

    [Fact]
    public async Task TearOff_OfTheOnlyTab_DoesNothing()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.TabTearOff, parameter: new TabTearOff(h.Tab, 10, 10));

        Assert.Single(h.Windows.Windows);
    }

    [Fact]
    public async Task Drop_FromAnotherWindow_MergesAtTheIndex_AndClosesTheEmptiedWindow()
    {
        var h = await ThreeTabs();
        var other = h.Windows.OpenWindow(P(@"C:\v"));
        var dragged = other.Tabs.Tabs[0];

        await h.Run(CommandIds.TabDrop, parameter: new TabDrop(dragged, 1));

        Assert.Same(dragged, h.Window.Tabs.Tabs[1]);
        Assert.Single(h.Windows.Windows);
    }

    [Fact]
    public async Task Drop_FromTheSameWindow_ReordersWithinTheStrip()
    {
        var h = await ThreeTabs();

        await h.Run(CommandIds.TabDrop, parameter: new TabDrop(h.Window.Tabs.Tabs[0], null));

        Assert.Equal(["v", "b", "a"], Titles(h));
    }

    [Fact]
    public async Task OpenInNewTab_Background_KeepsTheSelection_AndListsTheFolder()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var original = h.Tab;

        await h.Run(CommandIds.TabOpenInNewTab, @"C:\v");

        Assert.Equal(2, h.Window.Tabs.Tabs.Count);
        Assert.Same(original, h.Tab);
        Assert.Equal(FolderLoadState.Loaded, h.Window.Tabs.Tabs[1].Folder.State);
    }

    [Fact]
    public async Task OpenInNewTab_Foreground_SelectsTheNewTab()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.TabOpenInNewTab, @"C:\v", parameter: true);

        Assert.Equal(P(@"C:\v"), h.Tab.History.Current);
    }

    [Fact]
    public async Task OpenInNewTab_WithoutALocation_IsDisabled()
    {
        var h = new WindowCommandsTests.Harness();

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabOpenInNewTab));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(5);
        }
    }
}
