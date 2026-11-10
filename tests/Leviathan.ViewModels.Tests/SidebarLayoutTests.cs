using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.Tags;

namespace Leviathan.ViewModels.Tests;

/// <summary>How the sidebar sections look and start (spec §7): icons, the order of the common folders, hidden Favorites, folded Tags.</summary>
public sealed class SidebarLayoutTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class NoFolders : ISubfolderSource
    {
        public Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LocationPath>>([]);
    }

    private static SidebarPlace Common(string name, SidebarIcon icon) => new(name, P($@"C:\Users\b\{name}"), icon);

    private static IReadOnlyList<SidebarSection> Build(IEnumerable<FavoriteEntry>? favorites = null, IEnumerable<ColorTag>? tags = null) =>
        SidebarSections.Build(
            favorites ?? [],
            [Common("Music", SidebarIcon.Music), Common("b", SidebarIcon.Home), Common("Videos", SidebarIcon.Videos), Common("Pictures", SidebarIcon.Pictures), Common("Documents", SidebarIcon.Documents), Common("Desktop", SidebarIcon.Desktop), Common("Downloads", SidebarIcon.Downloads)],
            [],
            [],
            tags: tags ?? TagCatalog.Default.Tags);

    private static SidebarTree Tree(IReadOnlyList<SidebarSection> sections)
    {
        var tree = new SidebarTree(new NoFolders(), new ManualTimeProvider());
        tree.SetSections(sections);
        return tree;
    }

    [Fact]
    public void CommonFolders_AreInTheOrderDesktopDocumentsDownloadsPicturesVideosMusicHome()
    {
        var common = Build().Single(s => s.Title == "Common folders");

        Assert.Equal(["Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "b"], common.Places.Select(p => p.Title));
    }

    [Fact]
    public void Favorites_AreLeftOutOfTheTree_WhenThereAreNone()
    {
        var tree = Tree(Build());

        Assert.DoesNotContain(tree.Rows, r => r.Title == "Favorites");
        Assert.DoesNotContain(tree.Rows, r => r.Title.Contains("Ctrl+D", StringComparison.Ordinal));
    }

    [Fact]
    public void Favorites_Show_WhenThereIsOne()
    {
        var tree = Tree(Build([new FavoriteEntry(P(@"D:\Exports"))]));

        Assert.Contains(tree.Rows, r => r.Title == "Favorites");
        Assert.Contains(tree.Rows, r => r.Title == "Exports");
    }

    [Fact]
    public void Tags_StartFolded_WithOnlyTheirHeadingShown()
    {
        var tree = Tree(Build());

        var heading = tree.Rows.Single(r => r.Title == "Tags");
        Assert.False(heading.IsExpanded);
        Assert.DoesNotContain(tree.Rows, r => r.Title == "Red");
        Assert.Contains(tree.Rows, r => r.Title == "Desktop"); // the other sections are open
    }

    [Fact]
    public async Task Tags_OpenWhenAsked_AndStayAsTheUserLeftThemWhenTheTreeIsBuiltAgain()
    {
        var sections = Build();
        var tree = Tree(sections);
        await tree.ToggleAsync(tree.Rows.Single(r => r.Title == "Tags"), CancellationToken.None);
        Assert.Contains(tree.Rows, r => r.Title == "Red");

        tree.SetSections(sections); // a tag was renamed, say: the sidebar is rebuilt

        Assert.True(tree.Rows.Single(r => r.Title == "Tags").IsExpanded);
        Assert.Contains(tree.Rows, r => r.Title == "Red");
    }

    [Fact]
    public void EverySectionHasAnIcon_ThatIsOnTheHeading()
    {
        var sections = Build([new FavoriteEntry(P(@"D:\Exports"))]);

        Assert.All(sections.Where(s => s.Title is "Favorites" or "Common folders" or "Tags" or "Local" or "Network" or "Internet"), s => Assert.False(string.IsNullOrEmpty(s.Glyph), s.Title));
        var tree = Tree(sections);
        Assert.All(tree.Rows.Where(r => r.Kind == SidebarNodeKind.Section), r => Assert.False(string.IsNullOrEmpty(r.Glyph), r.Title));
    }
}
