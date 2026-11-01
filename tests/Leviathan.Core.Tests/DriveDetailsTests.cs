using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Core.Tests;

public sealed class DriveDetailsTests
{
    private static DriveDetails Drive(string root, string label, DriveKind kind, long total, long free) =>
        new(LocationPath.Parse(root), label, kind, StorageMedium.Ssd, "NTFS", total, free);

    [Fact]
    public void UsageNumbers_AreDerivedFromTotalAndFree()
    {
        var drive = Drive(@"D:\", "Data", DriveKind.Fixed, 2_000, 240);

        Assert.Equal(1_760, drive.UsedBytes);
        Assert.Equal(0.12, drive.FreeFraction, 3);
        Assert.Equal(0.88, drive.UsedFraction, 3);
        Assert.True(drive.IsLowOnSpace);
    }

    [Fact]
    public void IsLowOnSpace_FalseAtOrAboveFifteenPercent()
    {
        Assert.False(Drive(@"C:\", "", DriveKind.Fixed, 1_000, 150).IsLowOnSpace);
        Assert.True(Drive(@"C:\", "", DriveKind.Fixed, 1_000, 149).IsLowOnSpace);
    }

    [Fact]
    public void UnknownSizes_GiveNoUsageAndNoWarning()
    {
        var drive = Drive(@"Z:\", "", DriveKind.Network, -1, -1);

        Assert.False(drive.HasSizes);
        Assert.Equal(-1, drive.UsedBytes);
        Assert.Equal(0, drive.UsedFraction);
        Assert.False(drive.IsLowOnSpace);
    }

    [Theory]
    [InlineData(@"D:\", "Data", DriveKind.Fixed, "Data (D:)")]
    [InlineData(@"C:\", "", DriveKind.Fixed, "Local Disk (C:)")]
    [InlineData(@"E:\", "", DriveKind.Removable, "Removable Disk (E:)")]
    [InlineData(@"Z:\", "", DriveKind.Network, "Network Drive (Z:)")]
    [InlineData(@"F:\", "", DriveKind.Optical, "DVD Drive (F:)")]
    public void DisplayName_UsesTheLabelOrAKindName(string root, string label, DriveKind kind, string expected)
    {
        Assert.Equal(expected, Drive(root, label, kind, 1, 1).DisplayName);
    }
}
