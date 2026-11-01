using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;

namespace Leviathan.ViewModels.Tests;

public sealed class NavigationHistoryTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void New_History_CannotGoBackOrForward()
    {
        var history = new NavigationHistory(P(@"C:\a"));

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.GoBack());
        Assert.Null(history.GoForward());
    }

    [Fact]
    public void Navigate_AddsBackEntry_ClearsForward_AndReportsFolderLeft()
    {
        var history = new NavigationHistory(P(@"C:\a"));
        history.Navigate(P(@"C:\b"));
        history.GoBack();

        var result = history.Navigate(P(@"C:\c"));

        Assert.Equal(P(@"C:\c"), result?.Location);
        Assert.Equal(P(@"C:\a"), result?.SelectOnArrival);
        Assert.False(history.CanGoForward);
        Assert.Equal([P(@"C:\a")], history.BackList);
    }

    [Fact]
    public void Navigate_ToCurrentFolder_DoesNothing()
    {
        var history = new NavigationHistory(P(@"C:\a"));

        Assert.Null(history.Navigate(P(@"c:\A")));

        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void GoBack_SelectsTheFolderJustLeft_AndForwardReturns()
    {
        var history = new NavigationHistory(P(@"C:\a"));
        history.Navigate(P(@"C:\a\b"));

        var back = history.GoBack();
        var forward = history.GoForward();

        Assert.Equal(P(@"C:\a"), back?.Location);
        Assert.Equal(P(@"C:\a\b"), back?.SelectOnArrival);
        Assert.Equal(P(@"C:\a\b"), forward?.Location);
        Assert.Equal(P(@"C:\a"), forward?.SelectOnArrival);
    }

    [Fact]
    public void GoBack_SeveralSteps_JumpsAndKeepsTheSkippedEntriesInForward()
    {
        var history = new NavigationHistory(P(@"C:\1"));
        history.Navigate(P(@"C:\2"));
        history.Navigate(P(@"C:\3"));
        history.Navigate(P(@"C:\4"));

        var result = history.GoBack(3);

        Assert.Equal(P(@"C:\1"), result?.Location);
        Assert.Equal([P(@"C:\2"), P(@"C:\3"), P(@"C:\4")], history.ForwardList);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void GoForward_SeveralSteps_JumpsAndKeepsSkippedEntriesInBack()
    {
        var history = new NavigationHistory(P(@"C:\1"));
        history.Navigate(P(@"C:\2"));
        history.Navigate(P(@"C:\3"));
        history.Navigate(P(@"C:\4"));
        history.GoBack(3);

        var result = history.GoForward(3);

        Assert.Equal(P(@"C:\4"), result?.Location);
        Assert.Equal([P(@"C:\3"), P(@"C:\2"), P(@"C:\1")], history.BackList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void GoBackAndForward_OutOfRangeSteps_ReturnNull(int steps)
    {
        var history = new NavigationHistory(P(@"C:\1"));
        history.Navigate(P(@"C:\2"));

        Assert.Null(history.GoBack(steps));
        Assert.Null(history.GoForward(steps));
        Assert.Equal(P(@"C:\2"), history.Current);
    }

    [Fact]
    public void GoUp_MovesToParent_SelectsChildLeft_AndIsInHistory()
    {
        var history = new NavigationHistory(P(@"C:\a\b"));

        var result = history.GoUp();

        Assert.Equal(P(@"C:\a"), result?.Location);
        Assert.Equal(P(@"C:\a\b"), result?.SelectOnArrival);
        Assert.Equal(P(@"C:\a\b"), history.GoBack()?.Location);
    }

    [Fact]
    public void GoUp_AtRoot_ReturnsNull_AndUpListIsEmpty()
    {
        var history = new NavigationHistory(P(@"C:\"));

        Assert.False(history.CanGoUp);
        Assert.Null(history.GoUp());
        Assert.Empty(history.UpList);
    }

    [Fact]
    public void UpList_ListsEveryAncestorNearestFirst()
    {
        var history = new NavigationHistory(P(@"C:\a\b\c"));

        Assert.Equal([P(@"C:\a\b"), P(@"C:\a"), P(@"C:\")], history.UpList);
    }

    [Fact]
    public void Replace_ChangesCurrentWithoutAddingHistory()
    {
        var history = new NavigationHistory(P(@"C:\a"));

        history.Replace(P(@"C:\b"));

        Assert.Equal(P(@"C:\b"), history.Current);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Navigate_BeyondCapacity_DropsOldestEntries()
    {
        var history = new NavigationHistory(P(@"C:\start"));
        for (var i = 0; i < NavigationHistory.Capacity + 10; i++)
        {
            history.Navigate(P(@"C:\f" + i));
        }

        Assert.Equal(NavigationHistory.Capacity, history.BackList.Count);
        Assert.DoesNotContain(P(@"C:\start"), history.BackList);
        Assert.Equal(P(@"C:\f" + (NavigationHistory.Capacity + 8)), history.BackList[0]);
    }

    [Fact]
    public void Snapshot_ThenFromSnapshot_CopiesHistoryIndependently()
    {
        var history = new NavigationHistory(P(@"C:\1"));
        history.Navigate(P(@"C:\2"));
        history.Navigate(P(@"C:\3"));
        history.GoBack();

        var copy = NavigationHistory.FromSnapshot(history.Snapshot());
        copy.Navigate(P(@"C:\9"));

        Assert.Equal(P(@"C:\2"), history.Current);
        Assert.Equal([P(@"C:\3")], history.ForwardList);
        Assert.Equal(P(@"C:\9"), copy.Current);
        Assert.Equal([P(@"C:\2"), P(@"C:\1")], copy.BackList);
        Assert.False(copy.CanGoForward);
    }
}
