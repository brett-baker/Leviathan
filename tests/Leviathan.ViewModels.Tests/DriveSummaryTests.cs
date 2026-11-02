using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Drives;

namespace Leviathan.ViewModels.Tests;

public sealed class DriveSummaryTests
{
    private static DriveDetails Drive(DriveKind kind = DriveKind.Fixed, StorageMedium medium = StorageMedium.Ssd, long total = 1000L * 1024, long free = 500L * 1024, bool responsive = true) =>
        new(LocationPath.Parse(@"D:\"), "Development", kind, medium, "NTFS", total, free, IsResponsive: responsive);

    [Fact]
    public void From_DescribesALocalSsd()
    {
        var s = DriveSummary.From(Drive());

        Assert.Equal("Development (D:)", s.Title);
        Assert.Equal("Local disk · NTFS · SSD", s.TypeText);
        Assert.Equal("500 KB used · 500 KB free · 1000 KB total", s.UsageText);
        Assert.Equal(0.5, s.UsedFraction, 3);
        Assert.False(s.IsLowOnSpace);
    }

    [Fact]
    public void From_WarnsBelowFifteenPercentFree()
    {
        Assert.True(DriveSummary.From(Drive(free: 100L * 1024)).IsLowOnSpace);
    }

    [Fact]
    public void From_NetworkDriveWithoutMedium_OmitsIt()
    {
        Assert.Equal("Network share · NTFS", DriveSummary.From(Drive(DriveKind.Network, StorageMedium.Unknown)).TypeText);
    }

    [Fact]
    public void From_DeadDriveAndUnknownSizes_SayWhy()
    {
        Assert.Equal("Not responding", DriveSummary.From(Drive(responsive: false)).UsageText);
        Assert.Equal("Size not available", DriveSummary.From(Drive(total: -1, free: -1)).UsageText);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\nas\media", true)]
    [InlineData(@"\\nas", false)]
    [InlineData(@"\\nas\media\x", false)]
    public void IsDriveRoot_OnlyForTheTopOfADriveOrShare(string path, bool expected)
    {
        Assert.Equal(expected, DriveSummary.IsDriveRoot(LocationPath.Parse(path)));
    }
}

public sealed class DriveCardPreferencesTests
{
    private sealed class MemoryStorage : IDriveCardStorage
    {
        public List<string> Saved { get; set; } = [];

        public int Saves { get; private set; }

        public IReadOnlyList<string> Load() => Saved;

        public void Save(IReadOnlyList<string> roots)
        {
            Saved = [.. roots];
            Saves++;
        }
    }

    [Fact]
    public void SetHidden_RemembersPerDrive_IgnoringCase_AndSaves()
    {
        var storage = new MemoryStorage();
        var prefs = new DriveCardPreferences(storage);
        var changes = 0;
        prefs.Changed += (_, _) => changes++;

        prefs.SetHidden(LocationPath.Parse(@"D:\"), true);
        prefs.SetHidden(LocationPath.Parse(@"d:\"), true);

        Assert.True(prefs.IsHidden(LocationPath.Parse(@"D:\")));
        Assert.False(prefs.IsHidden(LocationPath.Parse(@"C:\")));
        Assert.Equal(1, changes);
        Assert.Equal(1, storage.Saves);
        Assert.Equal([@"D:\"], storage.Saved);
        Assert.True(new DriveCardPreferences(storage).IsHidden(LocationPath.Parse(@"D:\")));
    }

    [Fact]
    public void SetHidden_False_ShowsTheCardAgain()
    {
        var prefs = new DriveCardPreferences(new MemoryStorage { Saved = [@"D:\"] });

        prefs.SetHidden(LocationPath.Parse(@"D:\"), false);

        Assert.False(prefs.IsHidden(LocationPath.Parse(@"D:\")));
    }

    [Fact]
    public async Task DriveCardCommand_Toggles_OnlyForARoot()
    {
        var prefs = new DriveCardPreferences(new MemoryStorage());
        var h = new WindowCommandsTests.Harness(driveCards: prefs);
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.DriveCardToggle, @"C:\a"));
        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.DriveCardToggle, @"C:\"));
        Assert.True(prefs.IsHidden(LocationPath.Parse(@"C:\")));
        await h.Run(CommandIds.DriveCardToggle, @"C:\");
        Assert.False(prefs.IsHidden(LocationPath.Parse(@"C:\")));
    }
}
