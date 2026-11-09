using Leviathan.ViewModels.Tags;

namespace Leviathan.ViewModels.Tests;

/// <summary>The colour tag set (spec §11.1): defaults, renaming, recolouring, reordering, the 32 limit and keys 1 to 9.</summary>
public sealed class TagCatalogTests
{
    [Fact]
    public void Default_HasTheSevenTagsInOrder_GreenIsFour()
    {
        var names = TagCatalog.Default.Tags.Select(t => t.Name).ToArray();

        Assert.Equal(["Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gray"], names);
        Assert.Equal("Green", TagCatalog.Default.Find(Triage.TagIds.Green)?.Name);
    }

    [Fact]
    public void ForKey_FollowsTheOrder_AndIsNullBeyondTheTags()
    {
        var catalog = TagCatalog.Default.Move(5, 0);

        Assert.Equal(5, catalog.ForKey(1)?.Id);
        Assert.Equal(1, catalog.ForKey(2)?.Id);
        Assert.Null(catalog.ForKey(8));
        Assert.Null(catalog.ForKey(0));
        Assert.Null(catalog.ForKey(10));
        Assert.Equal(1, catalog.KeyOf(5));
    }

    [Fact]
    public void ForKey_OnlyReachesTheFirstNine_EvenWithMoreTags()
    {
        var catalog = TagCatalog.Default;
        for (var i = 0; i < 5; i++)
        {
            catalog = catalog.Add($"Extra {i}", "#112233");
        }

        Assert.Equal(12, catalog.Tags.Count);
        Assert.Equal(9, catalog.ForKey(9)?.Id);
        Assert.Equal(0, catalog.KeyOf(10));
    }

    [Fact]
    public void RenameAndRecolour_ChangeOneTag_AndIgnoreBadValues()
    {
        var catalog = TagCatalog.Default.Rename(4, "  Keep  ").Recolour(4, "#00ff00");

        Assert.Equal("Keep", catalog.Find(4)!.Name);
        Assert.Equal("#00FF00", catalog.Find(4)!.Colour);
        Assert.Same(catalog, catalog.Rename(4, "   "));
        Assert.Same(catalog, catalog.Recolour(4, "green"));
        Assert.Same(catalog, catalog.Rename(99, "Nope"));
    }

    [Fact]
    public void Move_ClampsTheIndex_AndKeepsEveryTag()
    {
        var last = TagCatalog.Default.Move(1, 99);

        Assert.Equal(1, last.Tags[^1].Id);
        Assert.Equal(7, last.Tags.Count);
        Assert.Equal(2, TagCatalog.Default.Move(1, -5).Move(2, 0).Tags[0].Id);
    }

    [Fact]
    public void Add_GivesTheNextFreeId_AndStopsAt32()
    {
        var catalog = TagCatalog.Default.Add("Mine", "#123456");
        Assert.Equal(8, catalog.Tags[^1].Id);

        while (catalog.Tags.Count < TagCatalog.MaxTags)
        {
            catalog = catalog.Add("More", "#123456");
        }

        Assert.Throws<InvalidOperationException>(() => catalog.Add("One too many", "#123456"));
    }

    [Fact]
    public void Constructor_CleansWhatAHandEditedFileCanHold()
    {
        var catalog = new TagCatalog(
        [
            new ColorTag(0, "No id", "#FFFFFF"),
            new ColorTag(3, "", "nonsense"),
            new ColorTag(3, "Repeat", "#FFFFFF"),
            new ColorTag(9, new string('x', 100), "#abcdef"),
        ]);

        Assert.Equal([3, 9], catalog.Tags.Select(t => t.Id).ToArray());
        Assert.Equal("Yellow", catalog.Find(3)!.Name);
        Assert.Equal("#8B8D98", catalog.Find(3)!.Colour);
        Assert.Equal(TagCatalog.MaxNameLength, catalog.Find(9)!.Name.Length);
        Assert.Equal("#ABCDEF", catalog.Find(9)!.Colour);
    }

    [Fact]
    public void Constructor_WithNothing_IsTheDefaults()
    {
        Assert.Equal(7, new TagCatalog(null).Tags.Count);
        Assert.Equal(7, new TagCatalog([]).Tags.Count);
    }

    [Fact]
    public void Constructor_CutsAtThirtyTwo()
    {
        var many = Enumerable.Range(1, 50).Select(i => new ColorTag(i, $"T{i}", "#000000"));

        Assert.Equal(TagCatalog.MaxTags, new TagCatalog(many).Tags.Count);
    }
}
