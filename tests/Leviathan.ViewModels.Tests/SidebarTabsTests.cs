using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

public sealed class SidebarTabsTests
{
    private static IReadOnlyList<SidebarSection> Sections() => SidebarSections.Build([], [], [], []);

    [Fact]
    public void Files_ShowsEverythingButFavorites()
    {
        var titles = SidebarTabs.Filter(SidebarTab.Files, Sections()).Select(s => s.Title).ToList();
        Assert.DoesNotContain("Favorites", titles);
        Assert.Contains("Local", titles);
        Assert.Contains("Network", titles);
    }

    [Fact]
    public void Favorites_ShowsOnlyFavorites()
    {
        Assert.Equal(["Favorites"], SidebarTabs.Filter(SidebarTab.Favorites, Sections()).Select(s => s.Title));
    }

    [Fact]
    public void Favorites_WithNonePinned_ShowsAMessageSayingHowToAddOne()
    {
        var favorites = SidebarTabs.Filter(SidebarTab.Favorites, Sections()).Single();
        Assert.Empty(favorites.Places);
        Assert.Equal(SidebarTabs.NoFavoritesText, favorites.EmptyText);
    }

    [Fact]
    public void Favorites_WhenPinned_ShowsTheRealSection()
    {
        var real = new SidebarSection("Favorites", [new SidebarPlace("Docs", Leviathan.Core.Paths.LocationPath.Parse(@"C:\Docs"), SidebarIcon.Folder)]);
        Assert.Same(real, SidebarTabs.Filter(SidebarTab.Favorites, [real, new SidebarSection("Local", [])]).Single());
    }
}
