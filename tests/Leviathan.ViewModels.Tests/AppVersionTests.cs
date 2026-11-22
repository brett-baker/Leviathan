using Leviathan.Diagnostics.Crash;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.6.0", "0.6.0", null, ReleaseChannel.Stable)]
    [InlineData("0.6.0-beta.1", "0.6.0", "beta.1", ReleaseChannel.Beta)]
    [InlineData("0.6.0+3f2a9c1d5e", "0.6.0", null, ReleaseChannel.Stable)]
    [InlineData("0.6.0-beta.2+3f2a9c1", "0.6.0", "beta.2", ReleaseChannel.Beta)]
    [InlineData("0.6.0.0", "0.6.0", null, ReleaseChannel.Stable)]
    [InlineData("0.1.0", "0.1.0", null, ReleaseChannel.Dev)]
    [InlineData("0.1.0+abc", "0.1.0", null, ReleaseChannel.Dev)]
    [InlineData("unknown", "0.1.0", null, ReleaseChannel.Dev)]
    [InlineData("", "0.1.0", null, ReleaseChannel.Dev)]
    public void Parse_ReadsNumberLabelAndChannel(string informational, string number, string? label, ReleaseChannel channel)
    {
        var version = AppVersion.Parse(new BuildInfo(informational, null, null));
        Assert.Equal(number, version.Number);
        Assert.Equal(label, version.Prerelease);
        Assert.Equal(channel, version.Channel);
    }

    [Fact]
    public void Display_SaysDevelopmentBuild_ForAnUntaggedBuild()
    {
        Assert.Equal("0.1.0 (development build)", AppVersion.Parse(new BuildInfo("0.1.0+abc", "abc", null)).Display);
        Assert.Equal("0.6.0-beta.1", AppVersion.Parse(new BuildInfo("0.6.0-beta.1", null, null)).Display);
        Assert.Equal("0.6.0", AppVersion.Parse(new BuildInfo("0.6.0", null, null)).Display);
    }

    [Fact]
    public void ShortCommit_IsSevenCharacters_OrWhatThereIs()
    {
        Assert.Equal("3f2a9c1", AppVersion.Parse(new BuildInfo("0.6.0", "3f2a9c1d5e8b", null)).ShortCommit);
        Assert.Equal("abc", AppVersion.Parse(new BuildInfo("0.6.0", "abc", null)).ShortCommit);
        Assert.Equal(string.Empty, AppVersion.Parse(new BuildInfo("0.6.0", null, null)).ShortCommit);
    }

    [Theory]
    [InlineData("v0.6.0", true, "0.6.0")]
    [InlineData("v10.20.30", true, "10.20.30")]
    [InlineData("v0.6.0-beta.1", true, "0.6.0-beta.1")]
    [InlineData("0.6.0", false, "")]
    [InlineData("v0.6", false, "")]
    [InlineData("v0.6.0-rc.1", false, "")]
    [InlineData("v0.6.0-beta", false, "")]
    [InlineData("release-1", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void TryFromTag_AcceptsOnlyReleaseTags(string? tag, bool ok, string version)
    {
        Assert.Equal(ok, AppVersion.TryFromTag(tag, out var result));
        Assert.Equal(version, result);
    }

    [Theory]
    [InlineData("0.6.1", "0.6.0", 1)]
    [InlineData("0.6.0", "0.6.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("0.6.0", "0.6.0-beta.3", 1)]
    [InlineData("0.6.0-beta.1", "0.6.0", -1)]
    [InlineData("0.6.0-beta.10", "0.6.0-beta.2", 1)]
    [InlineData("0.6.0-beta.2", "0.6.0-beta.2", 0)]
    [InlineData("0.7.0-beta.1", "0.6.9", 1)]
    public void Compare_FollowsSemVerPrecedence(string left, string right, int expected)
    {
        Assert.Equal(expected, AppVersion.Compare(left, right));
        Assert.Equal(-expected, AppVersion.Compare(right, left));
    }
}
