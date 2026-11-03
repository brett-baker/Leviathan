using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

public sealed class FavoriteStyleTests
{
    private sealed class MemoryFavorites : IFavoritesStorage
    {
        public List<FavoriteEntry> Saved { get; private set; } = [];

        public IReadOnlyList<FavoriteEntry> Load() => Saved;

        public void Save(IReadOnlyList<FavoriteEntry> favorites) => Saved = [.. favorites];
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Theory]
    [InlineData("#E74856", true)]
    [InlineData("#e74856", true)]
    [InlineData("#00ff7F", true)]
    [InlineData("E74856", false)]
    [InlineData("#E7485", false)]
    [InlineData("#E74856F", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsColor_AcceptsOnlySixHexDigitsAfterAHash(string? text, bool expected) => Assert.Equal(expected, FavoriteStyles.IsColor(text));

    [Fact]
    public void EveryIcon_HasAGlyph_AndAUniqueKey()
    {
        Assert.All(FavoriteStyles.Icons, i => Assert.False(string.IsNullOrEmpty(i.Glyph)));
        Assert.Equal(FavoriteStyles.Icons.Count, FavoriteStyles.Icons.Select(i => i.Key).Distinct().Count());
        Assert.All(FavoriteStyles.Colors, c => Assert.True(FavoriteStyles.IsColor(c.Hex)));
    }

    [Fact]
    public void SetStyle_StoresTheIconAndColour_AndSavesThem()
    {
        var storage = new MemoryFavorites();
        var list = new FavoritesList(storage);
        list.Toggle(P(@"\\nas\media"));

        list.SetStyle(P(@"\\nas\media"), "heart", "#e74856");

        var entry = Assert.Single(list.Items);
        Assert.Equal(("heart", "#E74856"), (entry.Icon, entry.Color));
        Assert.Equal("heart", storage.Saved[0].Icon);
    }

    [Fact]
    public void SetStyle_IgnoresAnUnknownIconAndABadColour()
    {
        var list = new FavoritesList(new MemoryFavorites());
        list.Toggle(P(@"C:\a"));
        list.SetStyle(P(@"C:\a"), "heart", "#0078D4");

        list.SetStyle(P(@"C:\a"), "no-such-icon", "blue");

        var entry = Assert.Single(list.Items);
        Assert.Null(entry.Icon);
        Assert.Null(entry.Color);
    }

    [Fact]
    public void SetStyle_OfAFolderThatIsNotAFavorite_ChangesNothing()
    {
        var list = new FavoritesList(new MemoryFavorites());
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.SetStyle(P(@"C:\a"), "heart", "#0078D4");

        Assert.Empty(list.Items);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Style_SurvivesRenameAndAReload()
    {
        var storage = new MemoryFavorites();
        var list = new FavoritesList(storage);
        list.Toggle(P(@"C:\a"));
        list.SetStyle(P(@"C:\a"), "work", "#16C60C");
        list.Rename(P(@"C:\a"), "Projects");

        var reloaded = new FavoritesList(storage);

        var entry = Assert.Single(reloaded.Items);
        Assert.Equal(("Projects", "work", "#16C60C"), (entry.Name, entry.Icon, entry.Color));
    }

    [Fact]
    public void TheSidebar_ShowsAFavoritesOwnIconAndColour()
    {
        var favorites = new[] { new FavoriteEntry(P(@"C:\a"), null, "heart", "#E3008C"), new FavoriteEntry(P(@"C:\b")) };

        var section = SidebarSections.Build(favorites, [], [], [])[0];

        Assert.Equal(FavoriteStyles.GlyphOf("heart"), section.Places[0].Glyph);
        Assert.Equal("#E3008C", section.Places[0].Accent);
        Assert.Null(section.Places[1].Glyph);
        Assert.Null(section.Places[1].Accent);
    }

    [Fact]
    public void TheTree_CarriesTheStyleToTheRow()
    {
        var tree = new SidebarTree(new NoSubfolders());
        tree.SetSections([new SidebarSection("Favorites", [new SidebarPlace("a", P(@"C:\a"), SidebarIcon.Favorite) { Glyph = "X", Accent = "#112233" }])]);

        var row = tree.Rows.First(r => r.Kind == SidebarNodeKind.Place);

        Assert.Equal(("X", "#112233"), (row.Glyph, row.Accent));
    }

    private sealed class NoSubfolders : ISubfolderSource
    {
        public Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocationPath>>([]);
    }

    [Fact]
    public async Task TheCommands_RenameAndRestyleAFavorite_AndRefuseOtherFolders()
    {
        var list = new FavoritesList(new MemoryFavorites());
        list.Toggle(P(@"C:\a"));
        var h = new WindowCommandsTests.Harness(favorites: list);
        await h.Ready;

        await h.Run(CommandIds.FavoriteRename, @"C:\a", "Work");
        await h.Run(CommandIds.FavoriteStyle, @"C:\a", new FavoriteStyleChange("flag", "#F7630C"));
        var elsewhere = await h.Run(CommandIds.FavoriteStyle, @"C:\v", new FavoriteStyleChange("flag", null));

        var entry = Assert.Single(list.Items);
        Assert.Equal(("Work", "flag", "#F7630C"), (entry.Name, entry.Icon, entry.Color));
        Assert.Equal(CommandResult.Disabled, elsewhere);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "favorite.style");
    }
}
