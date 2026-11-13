using Leviathan.Core.Model;
using Leviathan.ViewModels.Commands;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>Enter on an archive (spec §15.4).</summary>
public sealed class ArchiveOpenCommandTests
{
    private static FileEntry FileAt(string path) =>
        new(Path.GetFileName(path), TabTestSupport.P(path), FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Theory]
    [InlineData(@"C:\a\pack.zip")]
    [InlineData(@"C:\a\old.7z")]
    [InlineData(@"C:\a\disc.ISO")]
    [InlineData(@"C:\a\logs.tar.gz")]
    public async Task ItemOpen_AnArchive_OpensAsAFolder_NotInItsProgram(string path)
    {
        var h = new Harness();
        await h.Ready;
        var archive = FileAt(path);

        await h.Run(CommandIds.ItemOpen, archive.Location.Value, archive);

        Assert.Empty(h.Launcher.Opened);
        Assert.Equal(archive.Location, h.Tab.History.Current);
    }

    [Fact]
    public async Task ItemOpen_AnArchiveInsideAnArchive_IsHandedToTheLauncher()
    {
        var h = new Harness();
        await h.Ready;
        var inner = FileAt(@"C:\a\outer.zip\inner.zip");

        await h.Run(CommandIds.ItemOpen, inner.Location.Value, inner);

        Assert.Equal([inner.Location], h.Launcher.Opened);
    }

    [Fact]
    public async Task ItemOpen_APlainFile_StillLaunches()
    {
        var h = new Harness();
        await h.Ready;
        var file = FileAt(@"C:\a\notes.txt");

        await h.Run(CommandIds.ItemOpen, file.Location.Value, file);

        Assert.Equal([file.Location], h.Launcher.Opened);
    }
}
