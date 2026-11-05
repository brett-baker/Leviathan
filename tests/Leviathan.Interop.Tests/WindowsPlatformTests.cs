using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Interop.Tests;

/// <summary>Runs against the real machine: only reads folders and drive information, never writes.</summary>
public sealed class WindowsPlatformTests
{
    [Fact]
    public void KnownFolders_Documents_MatchesTheFrameworkAnswer()
    {
        var folders = new WindowsKnownFolders();

        var documents = folders.Get(KnownFolder.Documents);

        Assert.NotNull(documents);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), documents.Value, ignoreCase: true);
    }

    [Theory]
    [InlineData(KnownFolder.Home)]
    [InlineData(KnownFolder.Desktop)]
    [InlineData(KnownFolder.Downloads)]
    [InlineData(KnownFolder.Pictures)]
    [InlineData(KnownFolder.Videos)]
    [InlineData(KnownFolder.Music)]
    public void KnownFolders_Resolve_ToAbsoluteLocations(KnownFolder folder)
    {
        var location = new WindowsKnownFolders().Get(folder);

        Assert.NotNull(location);
        Assert.True(Path.IsPathRooted(location.Value));
    }

    [Fact]
    public void KnownFolders_RepeatedCalls_ReturnTheCachedValue()
    {
        var folders = new WindowsKnownFolders();

        Assert.Same(folders.Get(KnownFolder.Home), folders.Get(KnownFolder.Home));
    }

    [Fact]
    public async Task Volumes_ListTheSystemDrive_WithSizesAndFileSystem()
    {
        var drives = await new WindowsVolumeInfo().GetDrivesAsync(CancellationToken.None);

        var systemLetter = Path.GetPathRoot(Environment.SystemDirectory)![..2];
        var system = Assert.Single(drives, d => d.Root.Value.StartsWith(systemLetter, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DriveKind.Fixed, system.Kind);
        Assert.True(system.HasSizes);
        Assert.InRange(system.FreeBytes, 0, system.TotalBytes);
        Assert.Equal("NTFS", system.FileSystem, ignoreCase: true);
        Assert.True(system.IsResponsive);
    }

    [Fact]
    public async Task GetDriveAsync_AnswersForTheOneDrive_AndNothingForAShare()
    {
        var volumes = new WindowsVolumeInfo();
        var system = LocationPath.Parse(Path.Combine(Environment.SystemDirectory, "drivers"));

        var drive = await volumes.GetDriveAsync(system, CancellationToken.None);

        Assert.Equal(DriveKind.Fixed, drive!.Kind);
        Assert.Equal(DeleteBehavior.RecycleBin, DeletePolicy.Decide(system, drive));
        Assert.Null(await volumes.GetDriveAsync(LocationPath.Parse(@"\\nas\media\a.mkv"), CancellationToken.None));
    }

    [Fact]
    public async Task Volumes_ASlowDrive_DoesNotFailTheListing()
    {
        // A near-zero timeout makes every query time out: the listing still completes, with unresponsive drives.
        var drives = await new WindowsVolumeInfo(TimeSpan.FromTicks(1)).GetDrivesAsync(CancellationToken.None);

        Assert.NotEmpty(drives);
    }

    [Fact]
    public async Task Volumes_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsVolumeInfo().GetDrivesAsync(cts.Token));
    }

    [Fact]
    public void DriveMappings_ALocalDrive_HasNoSharePath()
    {
        Assert.Null(WindowsDriveMappings.QueryUnc(Path.GetPathRoot(Environment.SystemDirectory)!));
    }
}
