using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.ViewModels.Navigation;

namespace Leviathan.ViewModels.Tests;

/// <summary>Typing a path that goes into an archive (spec §5.2, §15.4).</summary>
public sealed class ArchiveAwareEntryProbeTests
{
    private sealed class Probe(FileEntryKind? kind) : IEntryProbe
    {
        public int Asked { get; private set; }

        public Task<FileEntryKind?> GetKindAsync(LocationPath location, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(kind);
        }
    }

    private sealed class Catalog(ArchiveProbe answer, Exception? failure = null) : IArchiveCatalog
    {
        public int Asked { get; private set; }

        public Task<ArchiveProbe> ProbeAsync(LocationPath location, CancellationToken cancellationToken)
        {
            Asked++;
            return failure is null ? Task.FromResult(answer) : Task.FromException<ArchiveProbe>(failure);
        }
    }

    private static readonly LocationPath Inside = LocationPath.Parse(@"D:\x\pack.zip\docs");

    [Fact]
    public async Task APathInsideAnArchive_IsAskedOfTheArchive()
    {
        var inner = new Probe(null);
        var catalog = new Catalog(new ArchiveProbe(true, FileEntryKind.Folder));

        var kind = await new ArchiveAwareEntryProbe(inner, catalog).GetKindAsync(Inside, CancellationToken.None);

        Assert.Equal(FileEntryKind.Folder, kind);
        Assert.Equal(0, inner.Asked);
    }

    [Fact]
    public async Task APathThatIsNotInTheArchive_IsNothing_NotAFileSystemGuess()
    {
        var inner = new Probe(FileEntryKind.Folder);

        var kind = await new ArchiveAwareEntryProbe(inner, new Catalog(new ArchiveProbe(true, null))).GetKindAsync(Inside, CancellationToken.None);

        Assert.Null(kind);
        Assert.Equal(0, inner.Asked);
    }

    [Fact]
    public async Task AFolderCalledLikeAnArchive_FallsBackToTheFileSystem()
    {
        var inner = new Probe(FileEntryKind.Folder);

        var kind = await new ArchiveAwareEntryProbe(inner, new Catalog(ArchiveProbe.NotInArchive)).GetKindAsync(Inside, CancellationToken.None);

        Assert.Equal(FileEntryKind.Folder, kind);
        Assert.Equal(1, inner.Asked);
    }

    [Fact]
    public async Task AnOrdinaryPath_NeverTouchesTheArchiveCatalog()
    {
        var inner = new Probe(FileEntryKind.File);
        var catalog = new Catalog(new ArchiveProbe(true, FileEntryKind.Folder));

        var kind = await new ArchiveAwareEntryProbe(inner, catalog).GetKindAsync(LocationPath.Parse(@"D:\x\notes.txt"), CancellationToken.None);

        Assert.Equal(FileEntryKind.File, kind);
        Assert.Equal(0, catalog.Asked);
    }

    [Theory]
    [InlineData(ArchiveError.PasswordRequired, LocationError.AuthenticationRequired)]
    [InlineData(ArchiveError.WrongPassword, LocationError.AuthenticationRequired)]
    [InlineData(ArchiveError.Unreadable, LocationError.Unknown)]
    public async Task AnArchiveThatCannotBeRead_FailsWithTheReason(ArchiveError error, LocationError expected)
    {
        var catalog = new Catalog(default, new ArchiveException(error, "pack.zip cannot be opened."));

        var ex = await Assert.ThrowsAsync<LocationException>(() => new ArchiveAwareEntryProbe(new Probe(null), catalog).GetKindAsync(Inside, CancellationToken.None));

        Assert.Equal(expected, ex.Error);
        Assert.Equal("pack.zip cannot be opened.", ex.Message);
    }
}
