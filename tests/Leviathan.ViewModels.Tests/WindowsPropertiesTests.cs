using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

/// <summary>How shell property values read in the details pane (spec §9.2).</summary>
public sealed class WindowsPropertiesTests
{
    [Theory]
    [InlineData("System.Photo.ExposureTime", 0.004, "1/250 s")]
    [InlineData("System.Photo.ExposureTime", 2.0, "2 s")]
    [InlineData("System.Photo.FNumber", 2.8, "f/2.8")]
    [InlineData("System.Photo.FocalLength", 50.0, "50 mm")]
    [InlineData("System.Photo.ISOSpeed", (ushort)400, "ISO 400")]
    [InlineData("System.Photo.Flash", (byte)1, "Fired")]
    [InlineData("System.Photo.Flash", (byte)16, "Did not fire")]
    [InlineData("System.Photo.Orientation", (ushort)6, "Rotated 90° clockwise")]
    [InlineData("System.Audio.EncodingBitrate", (uint)320_000, "320 kb/s")]
    [InlineData("System.Media.Duration", (ulong)1_800_000_000, "3m 00s")]
    [InlineData("System.Rating", (uint)99, "5 of 5")]
    [InlineData("System.Document.PageCount", 12, "12")]
    public void Format_ReadsEachKindOfValue(string key, object value, string expected) =>
        Assert.Equal(expected, WindowsProperties.Format(key, value));

    [Fact]
    public void Format_JoinsListsAndDropsEmptyThings()
    {
        Assert.Equal("A; B", WindowsProperties.Format("System.Music.Artist", new[] { "A", "", "B" }));
        Assert.Null(WindowsProperties.Format("System.Title", "  "));
        Assert.Null(WindowsProperties.Format("System.Title", null));
        Assert.Null(WindowsProperties.Format("System.Document.PageCount", 0));
        Assert.Null(WindowsProperties.Format("System.Photo.DateTaken", DateTimeOffset.MinValue));
    }

    [Fact]
    public void Sections_OfAPhoto_HaveTheImageGroup_AndAMapLinkWhenGpsIsPresent()
    {
        var values = new Dictionary<string, object?>
        {
            ["System.Photo.CameraModel"] = "X100V",
            ["System.Photo.FNumber"] = 2.0,
            ["System.GPS.LatitudeDecimal"] = 51.5007,
            ["System.GPS.LongitudeDecimal"] = -0.1246,
        };

        var sections = WindowsProperties.Sections(values, ".jpg");

        var image = Assert.Single(sections, s => s.Title == "Image");
        Assert.Contains(image.Rows, r => r is { Label: "Camera model", Value: "X100V" });
        Assert.Contains(image.Rows, r => r is { Label: "Aperture", Value: "f/2" });
        var all = Assert.Single(sections, s => s.Title == "All properties");
        var map = Assert.Single(all.Rows, r => r.Label == "Map");
        Assert.Contains("mlat=51.5007&mlon=-0.1246", map.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_OfASong_HaveTheAudioGroup_AndNothingForAnEmptyOne()
    {
        var song = WindowsProperties.Sections(new Dictionary<string, object?> { ["System.Music.Artist"] = new[] { "Band" }, ["System.Music.AlbumTitle"] = "Record" }, ".mp3");
        var empty = WindowsProperties.Sections(new Dictionary<string, object?>(), ".mp3");

        Assert.Contains(Assert.Single(song, s => s.Title == "Audio").Rows, r => r is { Label: "Artist", Value: "Band" });
        Assert.Empty(empty);
    }

    [Fact]
    public void Sections_ShowAPropertyOnlyOnce_EvenWhenTwoGroupsListIt()
    {
        var sections = WindowsProperties.Sections(new Dictionary<string, object?> { ["System.Title"] = "Report", ["System.Rating"] = (uint)75 }, ".docx");

        Assert.Equal(1, sections.SelectMany(s => s.Rows).Count(r => r.Value == "Report"));
    }

    [Fact]
    public void Keys_HaveNoDuplicates()
    {
        Assert.Equal(WindowsProperties.Keys.Count, WindowsProperties.Keys.Distinct().Count());
    }
}
