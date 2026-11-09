using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Tags;

namespace Leviathan.ViewModels.Tests;

/// <summary>What the Tags page edits (spec §11.1, §17.2).</summary>
public sealed class TagEditorTests
{
    [Fact]
    public void ResetStandard_RestoresNamesColoursAndOrder_ButKeepsTagsTheUserAdded()
    {
        var edited = TagCatalog.Default.Rename(1, "Urgent").Recolour(2, "#112233").Move(7, 0).Add("Later", "#445566");

        var reset = edited.ResetStandard();

        Assert.Equal(TagCatalog.Default.Tags.Select(t => (t.Id, t.Name, t.Colour)), reset.Tags.Take(7).Select(t => (t.Id, t.Name, t.Colour)));
        Assert.Equal(new ColorTag(8, "Later", "#445566"), reset.Tags[7]);
    }

    [Fact]
    public void WithCatalog_StoresTheCatalogAndKeepsTheTintSetting()
    {
        var settings = new TagSettings { TintRows = true };

        var changed = settings.WithCatalog(TagCatalog.Default.Rename(4, "Keep"));

        Assert.True(changed.TintRows);
        Assert.Equal("Keep", changed.Catalog().Find(4)!.Name);
    }

    [Fact]
    public void TheAddedTagGetsANewId_SoANeverDeletedTagKeepsItsMeaning()
    {
        var catalog = TagCatalog.Default.Add("A", "#000000").Add("B", "#000000");

        Assert.Equal([8, 9], catalog.Tags.Skip(7).Select(t => t.Id));
    }

    [Fact]
    public void ThePalette_HasOnlyValidColours_WithoutRepeats()
    {
        Assert.All(TagPalette.Colours, c => Assert.True(TagPalette.IsColour(c.Hex)));
        Assert.Equal(TagPalette.Colours.Count, TagPalette.Colours.Select(c => c.Hex).Distinct().Count());
        Assert.All(TagCatalog.Default.Tags, t => Assert.Contains(TagPalette.Colours, c => c.Hex == t.Colour));
    }

    [Fact]
    public void TheTagsPage_IsAvailable_AndItsSettingsCanBeFound()
    {
        Assert.True(SettingsCatalog.Page("tags").IsAvailable);
        Assert.Contains(SettingsCatalog.Search("recolour"), e => e.PageId == "tags");
        Assert.Contains(SettingsCatalog.Search("tint"), e => e.PageId == "tags");
    }
}
