using Leviathan.Core.Paths;
using Leviathan.Search;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Searches;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

/// <summary>A search pinned to Favorites behaves like a folder that updates itself (spec §13.2).</summary>
public sealed class SavedSearchTests
{
    private sealed class MemoryFavorites : IFavoritesStorage
    {
        public List<FavoriteEntry> Saved { get; private set; } = [];

        public IReadOnlyList<FavoriteEntry> Load() => Saved;

        public void Save(IReadOnlyList<FavoriteEntry> favorites) => Saved = [.. favorites];
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static LocationPath Search(string query = "type:video -tag:green", string root = @"\\nas\inbox") =>
        SearchStarter.Plan(query, P(root), false).Results!;

    private static SidebarPlace FavoritePlace(FavoriteEntry entry) =>
        SidebarSections.Build([entry], [], [], []).Single(s => s.Title == "Favorites").Places.Single();

    [Fact]
    public void Sidebar_ShowsASavedSearchWithAMagnifier_ItsQueryAsTitle_AndNoExpander()
    {
        var place = FavoritePlace(new FavoriteEntry(Search()));

        Assert.Equal("Search: type:video -tag:green", place.Title);
        Assert.Equal("\uE721", place.Glyph);
        Assert.True(place.IsLeaf);
        Assert.Equal("Search for \"type:video -tag:green\" in \\\\nas\\inbox", place.Detail);
    }

    [Fact]
    public void Sidebar_KeepsTheNameAndIconTheUserChose()
    {
        var place = FavoritePlace(new FavoriteEntry(Search(), Name: "Untriaged", Icon: FavoriteStyles.Icons[1].Key, Color: "#E74856"));

        Assert.Equal("Untriaged", place.Title);
        Assert.Equal(FavoriteStyles.Icons[1].Glyph, place.Glyph);
        Assert.Equal("#E74856", place.Accent);
        Assert.True(place.IsLeaf);
    }

    [Fact]
    public void Sidebar_OrdinaryFavoritesAreUnchanged()
    {
        var place = FavoritePlace(new FavoriteEntry(P(@"D:\Exports")));

        Assert.Equal("Exports", place.Title);
        Assert.False(place.IsLeaf);
        Assert.Equal(@"D:\Exports", place.Detail);
    }

    [Fact]
    public void Location_SurvivesBeingSavedAsText()
    {
        // Favorites are stored as the location's text and parsed back on the next run.
        var location = Search("Ünïcödé ✓ \"two words\" size:>1GB");

        var back = LocationPath.Parse(location.ToString());

        Assert.Equal(location, back);
        Assert.Equal("Ünïcödé ✓ \"two words\" size:>1GB", SearchLocations.TryRead(back)!.QueryText);
    }

    [Fact]
    public async Task SaveCommand_PinsTheSearch_OnlyOnce_AndOnlyOnAResultsTab()
    {
        var storage = new MemoryFavorites();
        var favorites = new FavoritesList(storage);
        var h = new WindowCommandsTests.Harness(favorites: favorites);
        await h.Ready;

        Assert.False(h.Commands.CanExecute(CommandIds.SearchSave)); // an ordinary folder

        await h.Run(CommandIds.SearchRun, "holiday");
        Assert.True(h.Commands.CanExecute(CommandIds.SearchSave));
        await h.Run(CommandIds.SearchSave);
        await h.Run(CommandIds.SearchSave);

        var saved = Assert.Single(storage.Saved);
        Assert.Equal("holiday", SearchLocations.TryRead(saved.Location)!.QueryText);
        Assert.Contains("already in Favorites", h.Shell.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlD_OnAResultsTab_AlsoSavesTheSearch()
    {
        var favorites = new FavoritesList(new MemoryFavorites());
        var h = new WindowCommandsTests.Harness(favorites: favorites);
        await h.Ready;
        await h.Run(CommandIds.SearchRun, "holiday");

        await h.Run(CommandIds.FavoriteToggle);

        Assert.True(favorites.Contains(h.Tab.History.Current));
    }
}
