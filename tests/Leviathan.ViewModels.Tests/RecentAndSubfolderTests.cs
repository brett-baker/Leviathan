using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Tests;

public sealed class RecentAndSubfolderTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void Add_PutsTheNewestFirst_AndRevisitingMovesItUp()
    {
        var recent = new RecentLocations();

        recent.Add(P(@"C:\a"));
        recent.Add(P(@"C:\b"));
        recent.Add(P(@"c:\A"));

        Assert.Equal([P(@"C:\a"), P(@"C:\b")], recent.Snapshot());
    }

    [Fact]
    public void Add_PastCapacity_DropsTheOldest()
    {
        var recent = new RecentLocations();
        for (var i = 0; i < RecentLocations.Capacity + 5; i++)
        {
            recent.Add(P(@"C:\f" + i));
        }

        var all = recent.Snapshot();
        Assert.Equal(RecentLocations.Capacity, all.Count);
        Assert.Equal(P(@"C:\f" + (RecentLocations.Capacity + 4)), all[0]);
        Assert.DoesNotContain(P(@"C:\f0"), all);
    }

    [Fact]
    public void RemoveAndLoad_EditTheList()
    {
        var recent = new RecentLocations();
        recent.Load([P(@"C:\x"), P(@"C:\y"), P(@"C:\x")]);

        Assert.Equal([P(@"C:\x"), P(@"C:\y")], recent.Snapshot());
        Assert.True(recent.Remove(P(@"C:\x")));
        Assert.False(recent.Remove(P(@"C:\x")));
        Assert.Equal([P(@"C:\y")], recent.Snapshot());
    }

    [Fact]
    public async Task EveryNavigationOfATab_IsRecorded_InTheSharedList()
    {
        var recent = new RecentLocations();
        var services = TabTestSupport.Services() with { Recent = recent };
        var tab = new TabViewModel(services, P(@"C:\a"));

        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);
        await tab.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        await tab.GoBackAsync(CancellationToken.None);

        Assert.Equal([P(@"C:\a\b"), P(@"C:\v")], recent.Snapshot());
    }

    private static SubfolderMenu Menu(FakeLocationProvider fake) => new(new ProviderFolderLister(new LocationProviderRegistry([fake])));

    private static FakeLocationProvider Tree() => new FakeLocationProvider()
        .AddFolder(@"C:\root\file10").AddFolder(@"C:\root\file2").AddFolder(@"C:\root\Alpha").AddFolder(@"C:\root\banana")
        .AddFile(@"C:\root\readme.txt", 1);

    [Fact]
    public async Task LoadAsync_ListsOnlyFolders_InNaturalOrder()
    {
        var folders = await Menu(Tree()).LoadAsync(P(@"C:\root"), CancellationToken.None);

        Assert.Equal(["Alpha", "banana", "file2", "file10"], folders.Select(f => f.Name));
    }

    [Fact]
    public async Task LoadAsync_AFolderThatCannotBeListed_GivesAnEmptyList()
    {
        var folders = await Menu(Tree()).LoadAsync(P(@"C:\missing"), CancellationToken.None);

        Assert.Empty(folders);
    }

    [Fact]
    public async Task Filter_MatchesAnywhereInTheName_PrefixMatchesFirst()
    {
        var folders = await Menu(Tree()).LoadAsync(P(@"C:\root"), CancellationToken.None);

        Assert.Equal(["file2", "file10"], SubfolderMenu.Filter(folders, "FILE").Select(f => f.Name));
        Assert.Equal(["Alpha", "banana"], SubfolderMenu.Filter(folders, "a").Select(f => f.Name));
        Assert.Equal(["banana"], SubfolderMenu.Filter(folders, "nan").Select(f => f.Name));
        Assert.Equal(4, SubfolderMenu.Filter(folders, "  ").Count);
        Assert.Empty(SubfolderMenu.Filter(folders, "zzz"));
    }
}
