using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

/// <summary>The headings of the sidebar have icons of their own and a colour, so they do not look like the rows below them.</summary>
public sealed class SidebarSectionIconsTests
{
    private static IReadOnlyList<SidebarSection> Sections() => SidebarSections.Build([], [], [], []);

    [Theory]
    [InlineData("Tags")]
    [InlineData("Local")]
    [InlineData("Network")]
    public void Heading_HasAGlyphAndAColour(string title)
    {
        var section = Sections().Single(s => s.Title == title);
        Assert.False(string.IsNullOrEmpty(section.Glyph));
        Assert.True(FavoriteStyles.IsColor(section.Accent));
    }

    [Fact]
    public void NetworkHeading_DoesNotUseTheIconOfTheDevicesBelowIt()
    {
        var network = Sections().Single(s => s.Title == "Network");
        var device = SidebarSections.NearbyGroup([new NearbyDevice("nas", "nas")], [], false).Places.Single();
        Assert.NotEqual(device.Glyph ?? string.Empty, network.Glyph);
    }

    [Fact]
    public void LocalHeading_IsNotTheHardDriveGlyph() => Assert.NotEqual("", Sections().Single(s => s.Title == "Local").Glyph);

    [Fact]
    public void NearbyGroup_HasAnIcon() => Assert.False(string.IsNullOrEmpty(SidebarSections.NearbyGroup([], [], true).Glyph));
}
