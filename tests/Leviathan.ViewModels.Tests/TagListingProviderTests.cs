using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.Tags;

namespace Leviathan.ViewModels.Tests;

/// <summary>The tag folders behind the Tags section of the sidebar (spec §7, §11.2).</summary>
public sealed class TagListingProviderTests
{
    private sealed class Index(params string[] paths) : ITagIndex
    {
        public int AskedFor { get; private set; }

        public IReadOnlyList<string> PathsWithTag(int tagId)
        {
            AskedFor = tagId;
            return paths;
        }

        public TagIndexEntry? FindByPath(string path) => null;

        public TagIndexEntry? FindByIdentity(FileIdentity identity) => null;

        public void Put(TagIndexEntry entry)
        {
        }

        public void Remove(string path)
        {
        }
    }

    private static FileEntry? Stat(string path) =>
        path.Contains("gone", StringComparison.Ordinal)
            ? null
            : new FileEntry(System.IO.Path.GetFileName(path), LocationPath.Parse(path), path.EndsWith("dir", StringComparison.Ordinal) ? FileEntryKind.Folder : FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static async Task<List<FileEntry>> ListAsync(TagListingProvider provider, LocationPath location)
    {
        var all = new List<FileEntry>();
        await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None))
        {
            all.AddRange(batch.Items);
        }

        return all;
    }

    [Fact]
    public void TagLocation_RoundTripsTheTagId()
    {
        var location = TagLocations.For(4);

        Assert.Equal(4, TagLocations.TagIdOf(location));
        Assert.Null(TagLocations.TagIdOf(LocationPath.Parse(@"C:\a")));
        Assert.Null(TagLocations.TagIdOf(LocationPath.Parse(@"shell:Downloads")));
    }

    [Fact]
    public async Task List_GivesTheTaggedFiles_LeavingOutVanishedFilesAndFolders()
    {
        var index = new Index(@"C:\a\one.jpg", @"C:\a\gone.jpg", @"C:\b\two.jpg", @"C:\b\dir");
        var provider = new TagListingProvider(new Lazy<ITagIndex>(index), Stat);

        var entries = await ListAsync(provider, TagLocations.For(3));

        Assert.Equal(3, index.AskedFor);
        Assert.Equal(["one.jpg", "two.jpg"], entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public async Task List_OfTheTagRootItself_IsEmpty()
    {
        var provider = new TagListingProvider(new Lazy<ITagIndex>(new Index(@"C:\a\one.jpg")), Stat);

        Assert.Empty(await ListAsync(provider, LocationPath.Parse("shell:tag")));
    }

    [Fact]
    public void CanHandle_OnlyTagLocations()
    {
        var provider = new TagListingProvider(new Lazy<ITagIndex>(new Index()), Stat);

        Assert.True(provider.CanHandle(TagLocations.For(1)));
        Assert.False(provider.CanHandle(LocationPath.Parse(@"C:\a")));
        Assert.False(provider.CanHandle(LocationPath.Parse("shell:Downloads")));
    }

    [Fact]
    public void TagsSection_HasOneLeafPlacePerTag_InTheUsersOrder()
    {
        var catalog = TagCatalog.Default.Move(5, 0);

        var section = SidebarSections.Build([], [], [], [], tags: catalog.Tags).Single(s => s.Title == "Tags");

        Assert.Equal(catalog.Tags.Select(t => t.Name), section.Places.Select(p => p.Title));
        Assert.All(section.Places, p => Assert.True(p.IsLeaf));
        Assert.Equal(TagLocations.For(catalog.Tags[0].Id), section.Places[0].Location);
        Assert.Equal(catalog.Tags[0].Colour, section.Places[0].Accent);
    }
}
