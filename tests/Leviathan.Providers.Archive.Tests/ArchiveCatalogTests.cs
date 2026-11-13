using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;

namespace Leviathan.Providers.Archive.Tests;

/// <summary>What the address bar learns about a path inside an archive (spec §5.2, §15.4).</summary>
public sealed class ArchiveCatalogTests
{
    private const string Zip = @"D:\Stuff\photos.zip";

    private static ArchiveCatalog Make(bool zipIsFile = true)
    {
        var reader = new FakeArchiveReader().Add(Zip, new Dictionary<string, string> { ["readme.txt"] = "hi", ["2024/jan/a.jpg"] = "a", ["empty/"] = "" });
        var disk = new MockFileSystem();
        if (zipIsFile)
        {
            disk.AddFile(Zip, new MockFileData("zip"));
        }
        else
        {
            disk.AddDirectory(Zip);
        }

        return new ArchiveCatalog(new ArchiveIndex(reader, disk));
    }

    private static Task<ArchiveProbe> Probe(ArchiveCatalog catalog, string location) => catalog.ProbeAsync(LocationPath.Parse(location), CancellationToken.None);

    [Fact]
    public async Task AFileInsideTheArchive_IsAFile()
    {
        var probe = await Probe(Make(), Zip + @"\2024\jan\a.jpg");

        Assert.True(probe.InArchive);
        Assert.Equal(FileEntryKind.File, probe.Kind);
    }

    [Theory]
    [InlineData(@"\2024")]
    [InlineData(@"\2024\jan")]
    [InlineData(@"\empty")]
    public async Task AFolderInsideTheArchive_IsAFolder_EvenWhenOnlyImplied(string inner)
    {
        var probe = await Probe(Make(), Zip + inner);

        Assert.True(probe.InArchive);
        Assert.Equal(FileEntryKind.Folder, probe.Kind);
    }

    [Fact]
    public async Task ThingsThatAreNotInTheArchive_AreNothing()
    {
        var probe = await Probe(Make(), Zip + @"\2025");

        Assert.True(probe.InArchive);
        Assert.Null(probe.Kind);
    }

    [Fact]
    public async Task OrdinaryPathsAndTheArchiveItself_AreNotTheCatalogsBusiness()
    {
        var catalog = Make();

        Assert.False((await Probe(catalog, @"D:\Stuff\notes.txt")).InArchive);
        Assert.False((await Probe(catalog, Zip)).InArchive);
    }

    [Fact]
    public async Task AFolderCalledLikeAnArchive_IsNotTheCatalogsBusiness()
    {
        var probe = await Probe(Make(zipIsFile: false), Zip + @"\2024");

        Assert.False(probe.InArchive);
    }
}
