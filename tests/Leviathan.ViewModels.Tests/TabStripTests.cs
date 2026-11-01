using Leviathan.Core.Paths;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Tests;

public sealed class TabStripTests
{
    private static LocationPath P(string path) => TabTestSupport.P(path);

    private static TabStripViewModel Strip() => new(TabTestSupport.Services());

    private static string[] Titles(TabStripViewModel strip) => [.. strip.Tabs.Select(t => t.Title)];

    [Fact]
    public void NewTab_OpensAtHomeByDefault_NextToTheSelectedTab_AndSelectsIt()
    {
        var strip = Strip();
        var first = strip.NewTab(P(@"C:\a"));
        var last = strip.NewTab(P(@"C:\v"));
        strip.Select(first);

        var middle = strip.NewTab();

        Assert.Equal(["a", "Home", "v"], Titles(strip));
        Assert.Same(middle, strip.Selected);
        Assert.Same(last, strip.Tabs[2]);
    }

    [Fact]
    public void NewTab_InTheBackground_KeepsTheSelection()
    {
        var strip = Strip();
        var first = strip.NewTab(P(@"C:\a"));

        strip.NewTab(P(@"C:\v"), select: false);

        Assert.Same(first, strip.Selected);
        Assert.Equal(2, strip.Tabs.Count);
    }

    [Fact]
    public async Task Duplicate_CopiesLocationAndHistory_RightAfterTheOriginal_Unpinned()
    {
        var strip = Strip();
        var tab = strip.NewTab(P(@"C:\a"));
        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        strip.SetPinned(tab, true);

        var copy = strip.Duplicate(tab);

        Assert.Equal(P(@"C:\a\b"), copy.History.Current);
        Assert.True(copy.History.CanGoBack);
        Assert.False(copy.IsPinned);
        Assert.Equal(1, strip.Tabs.IndexOf(copy));
        Assert.Same(copy, strip.Selected);
    }

    [Fact]
    public void Close_RemovesTheTab_SelectsTheLastUsed_AndCanBeReopenedWithHistory()
    {
        var strip = Strip();
        var a = strip.NewTab(P(@"C:\a"));
        var v = strip.NewTab(P(@"C:\v"));
        var d = strip.NewTab(P(@"C:\d1"));
        strip.Select(a);
        strip.Select(d);

        Assert.True(strip.Close(d));
        Assert.Same(a, strip.Selected);
        Assert.True(strip.CanReopen);

        var reopened = strip.Reopen();

        Assert.Equal("d1", reopened?.Title);
        Assert.Equal(2, strip.Tabs.IndexOf(reopened!));
        Assert.Same(reopened, strip.Selected);
        Assert.False(strip.CanReopen);
        Assert.Same(v, strip.Tabs[1]);
    }

    [Fact]
    public void Reopen_WithNothingClosed_ReturnsNull()
    {
        Assert.Null(Strip().Reopen());
    }

    [Fact]
    public void ClosedTabStack_KeepsOnlyTheLast25()
    {
        var strip = Strip();
        strip.NewTab(P(@"C:\a"));
        for (var i = 0; i < 30; i++)
        {
            var tab = strip.NewTab(P(@"C:\d1"));
            strip.Close(tab);
        }

        var reopened = 0;
        while (strip.Reopen() is not null)
        {
            reopened++;
        }

        Assert.Equal(TabStripViewModel.ClosedTabCapacity, reopened);
    }

    [Fact]
    public void ClosingTheLastTab_RaisesLastTabClosed_OrResetsToHome()
    {
        var strip = Strip();
        var closedRaised = false;
        strip.LastTabClosed += (_, _) => closedRaised = true;
        var only = strip.NewTab(P(@"C:\a"));

        strip.Close(only);
        Assert.True(closedRaised);
        Assert.Empty(strip.Tabs);

        var other = Strip();
        other.LastTabResetsToHome = true;
        var tab = other.NewTab(P(@"C:\a"));
        other.Close(tab);
        Assert.Single(other.Tabs);
        Assert.Equal(P(@"C:\Home"), tab.History.Current);
    }

    [Fact]
    public void PinnedTabs_SitLeft_CannotBeClosedByShortcut_AndSurviveCloseOthers()
    {
        var strip = Strip();
        var a = strip.NewTab(P(@"C:\a"));
        var v = strip.NewTab(P(@"C:\v"));
        var d = strip.NewTab(P(@"C:\d1"));

        strip.SetPinned(d, true);
        Assert.Equal(["d1", "a", "v"], Titles(strip));

        Assert.False(strip.Close(d));
        Assert.Equal(1, strip.CloseOthers(v));
        Assert.Equal(["d1", "v"], Titles(strip));
        Assert.True(strip.Close(d, force: true));
        Assert.DoesNotContain(a, strip.Tabs);
    }

    [Fact]
    public void SetPinned_Unpin_MovesToTheStartOfTheUnpinnedGroup()
    {
        var strip = Strip();
        var a = strip.NewTab(P(@"C:\a"));
        var v = strip.NewTab(P(@"C:\v"));
        strip.NewTab(P(@"C:\d1"));
        strip.SetPinned(a, true);
        strip.SetPinned(v, true);

        strip.SetPinned(a, false);

        Assert.Equal(["v", "a", "d1"], Titles(strip));
    }

    [Fact]
    public void Move_StaysInsideItsZone_AndNudgeMovesOnePlace()
    {
        var strip = Strip();
        var a = strip.NewTab(P(@"C:\a"));
        var v = strip.NewTab(P(@"C:\v"));
        var d = strip.NewTab(P(@"C:\d1"));
        strip.SetPinned(a, true);

        strip.Move(d, 0);
        Assert.Equal(["a", "d1", "v"], Titles(strip));

        strip.Move(a, 2);
        Assert.Equal(["a", "d1", "v"], Titles(strip));

        strip.Nudge(v, -1);
        Assert.Equal(["a", "v", "d1"], Titles(strip));
    }

    [Fact]
    public void CloseToRight_ClosesUnpinnedTabsAfterTheGivenOne()
    {
        var strip = Strip();
        strip.NewTab(P(@"C:\a"));
        var v = strip.NewTab(P(@"C:\v"));
        strip.NewTab(P(@"C:\d1"));
        strip.NewTab(P(@"C:\d2"));

        Assert.Equal(2, strip.CloseToRight(v));
        Assert.Equal(["a", "v"], Titles(strip));
    }

    [Fact]
    public void Cycle_WrapsInStripOrder_AndFollowsMruWhenEnabled()
    {
        var strip = Strip();
        var a = strip.NewTab(P(@"C:\a"));
        var v = strip.NewTab(P(@"C:\v"));
        var d = strip.NewTab(P(@"C:\d1"));

        strip.Cycle(forward: true);
        Assert.Same(a, strip.Selected);
        strip.Cycle(forward: false);
        Assert.Same(d, strip.Selected);

        strip.UseMruOrder = true;
        strip.Select(a);
        strip.Select(v);
        strip.Cycle(forward: true);
        Assert.Same(a, strip.Selected);
        strip.Cycle(forward: true);
        Assert.Same(v, strip.Selected);
    }

    [Theory]
    [InlineData(1, "a")]
    [InlineData(3, "d1")]
    [InlineData(8, "d1")]
    [InlineData(9, "d1")]
    public void SelectByNumber_Ctrl1To8GoToThatTab_Ctrl9GoesToTheLast(int number, string expected)
    {
        var strip = Strip();
        strip.NewTab(P(@"C:\a"));
        strip.NewTab(P(@"C:\v"));
        strip.NewTab(P(@"C:\d1"));
        strip.Select(strip.Tabs[1]);

        strip.SelectByNumber(number);

        Assert.Equal(expected, strip.Selected?.Title);
    }

    [Fact]
    public void DetachAndAttach_MoveATabBetweenStrips_WithoutTouchingTheClosedStack()
    {
        var from = Strip();
        var to = Strip();
        from.NewTab(P(@"C:\a"));
        var moving = from.NewTab(P(@"C:\v"));
        to.NewTab(P(@"C:\d1"));

        Assert.True(from.Detach(moving));
        to.Attach(moving, 0);

        Assert.Equal(["a"], Titles(from));
        Assert.Equal(["v", "d1"], Titles(to));
        Assert.False(from.CanReopen);
        Assert.Same(moving, to.Selected);
        Assert.Equal(P(@"C:\v"), moving.History.Current);
    }

    [Fact]
    public void Search_MatchesTitleOrPath_AllWords()
    {
        var strip = Strip();
        strip.NewTab(P(@"C:\a\b"));
        strip.NewTab(P(@"C:\v"));
        var named = strip.NewTab(P(@"C:\d1"));
        named.CustomTitle = "NAS inbox";

        Assert.Equal(["NAS inbox"], strip.Search("nas inbox").Select(t => t.Title));
        Assert.Equal(["b"], strip.Search(@"a\b").Select(t => t.Title));
        Assert.Equal(3, strip.Search("").Count);
        Assert.Empty(strip.Search("zzz"));
    }

    [Fact]
    public void Tabs_OpenedAsPinned_NeverLandAmongUnpinnedOnes()
    {
        var strip = Strip();
        var a = strip.NewTab(P(@"C:\a"));
        strip.SetPinned(a, true);
        strip.NewTab(P(@"C:\v"));
        strip.Close(a, force: true);

        var back = strip.Reopen();

        Assert.True(back?.IsPinned);
        Assert.Equal(0, strip.Tabs.IndexOf(back!));
    }
}
