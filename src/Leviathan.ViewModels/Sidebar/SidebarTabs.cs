namespace Leviathan.ViewModels.Sidebar;

/// <summary>The tabs under the sidebar tree (spec §7): each shows some of its sections.</summary>
public enum SidebarTab
{
    /// <summary>The folder tree: common folders, tags, drives, network and cloud.</summary>
    Files,

    /// <summary>Only the pinned folders and saved searches.</summary>
    Favorites,
}

/// <summary>
/// Which sections of the sidebar a tab shows. Favorites has a tab of its own, so it leaves the Files tree instead of being listed twice.
/// More tabs (Tags, Recent, Cloud) take their sections here when they are built. Pure, so the split is tested without a window.
/// </summary>
public static class SidebarTabs
{
    /// <summary>The title of the Favorites section.</summary>
    public const string FavoritesTitle = "Favorites";

    /// <summary>What the Favorites tab says while nothing is pinned.</summary>
    public const string NoFavoritesText = "No favorites yet. Press Ctrl+D, or click the star in the address bar.";

    /// <summary>The sections <paramref name="tab"/> shows, in their order.</summary>
    public static IReadOnlyList<SidebarSection> Filter(SidebarTab tab, IReadOnlyList<SidebarSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (tab != SidebarTab.Favorites)
        {
            return [.. sections.Where(s => s.Title != FavoritesTitle)];
        }

        // The sections leave out an empty Favorites; its own tab says how to fill it instead of showing nothing.
        var favorites = sections.FirstOrDefault(s => s.Title == FavoritesTitle);
        if (favorites is null)
        {
            return [new SidebarSection(FavoritesTitle, [], NoFavoritesText) { Glyph = "\uE734" }];
        }

        return [favorites.Places.Count == 0 ? favorites with { EmptyText = NoFavoritesText } : favorites];
    }
}
