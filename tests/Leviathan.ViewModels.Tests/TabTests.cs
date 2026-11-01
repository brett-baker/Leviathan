using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

internal sealed class InlineUiContext : IUiContext
{
    public void Post(Action action) => action();
}

internal static class TabTestSupport
{
    public static LocationPath P(string path) => LocationPath.Parse(path);

    public static FakeLocationProvider Tree() => new FakeLocationProvider()
        .AddFile(@"C:\Home\readme.txt", 1)
        .AddFile(@"C:\a\1.txt", 1).AddFile(@"C:\a\b\2.txt", 2).AddFolder(@"C:\a\b\c")
        .AddFile(@"C:\v\x.mkv", 1).AddFile(@"C:\v\y.mkv", 1).AddFile(@"C:\v\z.mp4", 1)
        .AddFolder(@"C:\d1").AddFolder(@"C:\d2").AddFolder(@"C:\d3");

    public static TabServices Services(ILocationProvider? provider = null, FolderViewRules? rules = null) =>
        new(new LocationProviderRegistry([provider ?? Tree()]), new InlineUiContext(), rules ?? new FolderViewRules(), TimeProvider.System, () => P(@"C:\Home"));
}

public sealed class TabTests
{
    private static LocationPath P(string path) => TabTestSupport.P(path);

    [Fact]
    public async Task NavigateAsync_ListsTheFolder_UpdatesTitleAndAddress()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\a"));

        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);

        Assert.Equal("b", tab.Title);
        Assert.Equal(P(@"C:\a\b"), tab.Address.Location);
        Assert.Equal(FolderLoadState.Loaded, tab.Folder.State);
        Assert.Equal(["c", "2.txt"], tab.Items.Rows.Select(r => r.Name));
        Assert.True(tab.History.CanGoBack);
    }

    [Fact]
    public async Task GoBackAsync_SelectsTheFolderYouCameFrom()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\a"));
        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);

        await tab.GoBackAsync(CancellationToken.None);

        Assert.Equal(P(@"C:\a"), tab.History.Current);
        Assert.Equal(["b"], tab.Selection.SelectedEntries.Select(e => e.Name));
    }

    [Fact]
    public async Task GoUpAsync_SelectsTheChildJustLeft_AndBackReturns()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\a\b"));
        await tab.ReloadAsync(CancellationToken.None);

        await tab.GoUpAsync(CancellationToken.None);
        Assert.Equal(["b"], tab.Selection.SelectedEntries.Select(e => e.Name));

        Assert.False(tab.History.CanGoForward);
        await tab.GoBackAsync(CancellationToken.None);
        Assert.Equal(P(@"C:\a\b"), tab.History.Current);
    }

    [Fact]
    public async Task GoUpAsync_AtRoot_DoesNothing()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\"));

        await tab.GoUpAsync(CancellationToken.None);

        Assert.Equal(P(@"C:\"), tab.History.Current);
    }

    [Fact]
    public async Task LockedTab_NavigationOpensANewTabInstead()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\a")) { IsLocked = true };
        LocationPath? requested = null;
        tab.OpenInNewTabRequested += (_, l) => requested = l;

        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        await tab.GoUpAsync(CancellationToken.None);

        Assert.Equal(P(@"C:\"), requested);
        Assert.Equal(P(@"C:\a"), tab.History.Current);
        Assert.False(tab.History.CanGoBack);
    }

    [Fact]
    public async Task SavedFolderView_AppliesOnNavigation_ThenTheKindRuleAfterTheListing()
    {
        var rules = new FolderViewRules();
        rules.Set(P(@"C:\a"), ViewScope.ThisFolderAndSubfolders, new FolderViewSettings { Layout = ViewLayout.Thumbnails });
        rules.SetForKind(FolderKind.Videos, new FolderViewSettings { Layout = ViewLayout.List });
        var tab = new TabViewModel(TabTestSupport.Services(rules: rules), P(@"C:\Home"));

        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        Assert.Equal(ViewLayout.Thumbnails, tab.Layout);

        await tab.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        Assert.Equal(ViewLayout.List, tab.Layout);
    }

    [Fact]
    public async Task SetLayoutAndSort_HoldUntilTheNextNavigation()
    {
        var tab = new TabViewModel(TabTestSupport.Services(), P(@"C:\v"));
        await tab.ReloadAsync(CancellationToken.None);

        tab.SetLayout(ViewLayout.Thumbnails);
        tab.SortBy("size");
        Assert.Equal(ViewLayout.Thumbnails, tab.Layout);

        await tab.ReloadAsync(CancellationToken.None);
        Assert.Equal(ViewLayout.Thumbnails, tab.Layout);
        Assert.Equal("size", tab.Items.Sort[0].Column);

        await tab.NavigateAsync(P(@"C:\a"), CancellationToken.None);
        Assert.Equal(ViewLayout.Details, tab.Layout);
        Assert.Equal("name", tab.Items.Sort[0].Column);
    }

    [Fact]
    public async Task CaptureState_ThenRestore_KeepsHistoryViewOptionsAndSelection_WithoutListing()
    {
        var provider = TabTestSupport.Tree();
        var tab = new TabViewModel(TabTestSupport.Services(provider), P(@"C:\a"))
        {
            CustomTitle = "Inbox",
            Accent = "#FF8800",
            IsLocked = true,
            IsSplit = true,
        };
        tab.IsLocked = false;
        await tab.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        tab.SortBy("name");
        tab.Selection.Click(tab.Items.Rows[1]);
        var state = tab.CaptureState();
        var listCallsBefore = provider.ListCalls.Values.Sum();

        var restored = TabViewModel.Restore(state, TabTestSupport.Services(provider));

        Assert.Equal("Inbox", restored.Title);
        Assert.Equal("#FF8800", restored.Accent);
        Assert.True(restored.IsSplit);
        Assert.Equal(P(@"C:\v"), restored.History.Current);
        Assert.True(restored.History.CanGoBack);
        Assert.Equal(SortDirection.Descending, restored.Items.Sort[0].Direction);
        Assert.Equal(listCallsBefore, provider.ListCalls.Values.Sum());
        Assert.Empty(restored.Items.Rows);

        await restored.ReloadAsync(CancellationToken.None);
        Assert.Equal(["y.mkv"], restored.Selection.SelectedEntries.Select(e => e.Name));
    }

    [Fact]
    public async Task OfflineShare_SetsTheOfflineBadge_AndBusyWhileLoading()
    {
        var fault = new FaultInjectingLocationProvider(new FakeLocationProvider().AddFolder(@"\\nas\media"));
        fault.Inject(@"\\nas\media", new Fault.NetworkDrop());
        var tab = new TabViewModel(TabTestSupport.Services(fault), P(@"C:\"));
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await tab.NavigateAsync(P(@"\\nas\media"), CancellationToken.None);

        Assert.True(tab.IsOffline);
        Assert.False(tab.IsBusy);
        Assert.Contains(nameof(TabViewModel.IsBusy), changed);
        Assert.Contains(nameof(TabViewModel.IsOffline), changed);
    }
}
