using Leviathan.Core.Paths;
using Leviathan.Search;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

/// <summary>Reading and wording for the settings pages (spec §17.2).</summary>
public sealed class SettingsInputTests
{
    [Theory]
    [InlineData("22:00", 22, 0)]
    [InlineData(" 6:30 ", 6, 30)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void ATimeOfDay_IsRead(string text, int hours, int minutes)
    {
        Assert.True(SettingsInput.TryParseTimeOfDay(text, out var time));
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("24:00")]
    [InlineData("-1:00")]
    [InlineData("noon")]
    [InlineData("1.5")]
    public void NotATimeOfDay_IsRefused(string? text) => Assert.False(SettingsInput.TryParseTimeOfDay(text, out _));

    [Fact]
    public void ATimeOfDay_IsWrittenWithTwoDigits() => Assert.Equal("06:05", SettingsInput.FormatTimeOfDay(new TimeSpan(6, 5, 0)));

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IndexStatus_SaysWhatTheFolderIsDoing()
    {
        var root = LocationPath.Parse(@"D:\Photos");

        Assert.Equal("Listing now: 12 folders, 3,400 items", SettingsInput.IndexStatus(new IndexRootStatus(root, null, true, false, new CrawlProgress(12, 3400, 0), null, null), Now).Replace('\u00a0', ' '));
        Assert.Equal("Waiting: a copy is using it", SettingsInput.IndexStatus(new IndexRootStatus(root, null, false, true, null, null, "a copy is using it"), Now));
        Assert.Equal("Not listed yet · due now", SettingsInput.IndexStatus(new IndexRootStatus(root, null, false, false, null, Now, null), Now));
        Assert.Equal("Listed 2 h ago · next in 4 h", SettingsInput.IndexStatus(new IndexRootStatus(root, Now.AddHours(-2), false, false, null, Now.AddHours(4), null), Now));
        Assert.StartsWith("Listed just now", SettingsInput.IndexStatus(new IndexRootStatus(root, Now.AddSeconds(-5), false, false, null, null, null), Now));
    }
}
