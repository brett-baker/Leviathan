using Leviathan.Core.Platform;

namespace Leviathan.Core.Tests;

/// <summary>The data folder follows LEVIATHAN_DATA_DIR, so a test run never touches the user's own files (spec §17, §19).</summary>
public sealed class DataLocationTests
{
    [Fact]
    public void Root_WithAFolderGiven_IsThatFolder()
    {
        Assert.Equal(@"D:\scratch\data", DataLocation.Root(@"D:\scratch\data"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Root_WithoutOne_IsTheLeviathanFolderInLocalAppData(string? custom)
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan");

        Assert.Equal(expected, DataLocation.Root(custom));
    }
}
