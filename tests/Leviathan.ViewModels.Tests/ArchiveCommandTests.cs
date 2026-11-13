using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Archives;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Transfers;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>Extract and compress from the menus (spec §15.4).</summary>
public sealed class ArchiveCommandTests
{
    private sealed class FakeArchives : IArchiveService
    {
        public List<(IReadOnlyList<LocationPath> Archives, LocationPath Destination, bool Subfolder)> Extracts { get; } = [];

        public List<(IReadOnlyList<LocationPath> Items, LocationPath Archive, CompressChoice Choice)> Compresses { get; } = [];

        public TransferOutcome Outcome { get; set; } = new(true, "queued");

        public Task<TransferOutcome> ExtractAsync(IReadOnlyList<LocationPath> archives, LocationPath destination, bool intoSubfolder, CancellationToken cancellationToken)
        {
            Extracts.Add((archives, destination, intoSubfolder));
            return Task.FromResult(Outcome);
        }

        public Task<TransferOutcome> CompressAsync(IReadOnlyList<LocationPath> items, LocationPath archive, CompressChoice choice, CancellationToken cancellationToken)
        {
            Compresses.Add((items, archive, choice));
            return Task.FromResult(Outcome);
        }
    }

    private static FileEntry FileAt(string path) =>
        new(Path.GetFileName(path), TabTestSupport.P(path), FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Fact]
    public async Task ExtractHere_QueuesAJobIntoTheArchivesFolder()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;
        var zip = FileAt(@"C:\a\pack.zip");

        await h.Run(CommandIds.ExtractHere, zip.Location.Value, zip);

        var (items, destination, subfolder) = Assert.Single(archives.Extracts);
        Assert.Equal([zip.Location], items);
        Assert.Equal(TabTestSupport.P(@"C:\a"), destination);
        Assert.False(subfolder);
    }

    [Fact]
    public async Task ExtractToFolder_AsksForASubfolderNamedAfterTheArchive()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;

        await h.Run(CommandIds.ExtractToFolder, null, FileAt(@"C:\a\pack.7z"));

        Assert.True(Assert.Single(archives.Extracts).Subfolder);
    }

    [Fact]
    public async Task ExtractTo_UsesTheFolderThePersonPicked_AndDoesNothingWhenTheyCancel()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;
        var zip = FileAt(@"C:\a\pack.zip");

        await h.Run(CommandIds.ExtractTo, null, zip);
        Assert.Empty(archives.Extracts);

        h.Shell.PickedFolder = TabTestSupport.P(@"D:\Elsewhere");
        await h.Run(CommandIds.ExtractTo, null, zip);

        Assert.Equal(TabTestSupport.P(@"D:\Elsewhere"), Assert.Single(archives.Extracts).Destination);
    }

    [Fact]
    public async Task Extract_OfSomethingThatIsNotAnArchive_SaysSo()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;

        await h.Run(CommandIds.ExtractHere, null, FileAt(@"C:\a\notes.txt"));

        Assert.Empty(archives.Extracts);
        Assert.Single(h.Shell.Messages);
    }

    [Fact]
    public async Task Compress_OfOneFile_MakesAZipBesideItNamedAfterIt()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;
        var file = FileAt(@"C:\a\report.final.docx");

        await h.Run(CommandIds.Compress, null, file);

        var (items, archive, choice) = Assert.Single(archives.Compresses);
        Assert.Equal([file.Location], items);
        Assert.Equal(TabTestSupport.P(@"C:\a\report.final.zip"), archive);
        Assert.Equal(ArchiveFormat.Zip, choice.Format);
        Assert.Equal(ArchiveLevel.Normal, choice.Level);
        Assert.Null(choice.Password);
    }

    [Fact]
    public async Task CompressWith_UsesWhatTheDialogReturned_AndFixesTheExtension()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;
        h.Shell.CompressAnswer = new CompressChoice("backup.zip", ArchiveFormat.SevenZip, ArchiveLevel.Ultra, "pw");

        await h.Run(CommandIds.CompressWith, null, FileAt(@"C:\a\1.txt"));

        var (_, archive, choice) = Assert.Single(archives.Compresses);
        Assert.Equal("1.zip", h.Shell.CompressAskedWith);
        Assert.Equal(TabTestSupport.P(@"C:\a\backup.7z"), archive);
        Assert.Equal(ArchiveLevel.Ultra, choice.Level);
        Assert.Equal("pw", choice.Password);
    }

    [Fact]
    public async Task CompressWith_Cancelled_QueuesNothing()
    {
        var archives = new FakeArchives();
        var h = new Harness(archives: archives);
        await h.Ready;

        await h.Run(CommandIds.CompressWith, null, FileAt(@"C:\a\1.txt"));

        Assert.Empty(archives.Compresses);
    }

    [Fact]
    public async Task ARefusedJob_IsShownToThePerson()
    {
        var archives = new FakeArchives { Outcome = new TransferOutcome(false, "The worker is not running.") };
        var h = new Harness(archives: archives);
        await h.Ready;

        await h.Run(CommandIds.Compress, null, FileAt(@"C:\a\1.txt"));

        Assert.Equal(["The worker is not running."], h.Shell.Messages);
    }

    [Fact]
    public async Task WithoutAnArchiveService_TheCommandsAreDisabled()
    {
        var h = new Harness();
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.Compress, null, FileAt(@"C:\a\1.txt")));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.ExtractHere, null, FileAt(@"C:\a\pack.zip")));
    }

    [Theory]
    [InlineData(@"C:\a\photo.jpg", ".zip", "photo.zip")]
    [InlineData(@"C:\a\My Folder", ".7z", "My Folder.7z")]
    [InlineData(@"C:\a\.hidden", ".zip", ".hidden.zip")]
    public void Suggest_OneItem_UsesItsNameWithoutTheFileExtension(string item, string extension, string expected)
    {
        Assert.Equal(expected, ArchiveNaming.Suggest([LocationPath.Parse(item)], extension));
    }

    [Fact]
    public void Suggest_SeveralItems_UseTheirFolderName_AndADriveRootFallsBack()
    {
        Assert.Equal("Holiday.zip", ArchiveNaming.Suggest([LocationPath.Parse(@"C:\Holiday\a.jpg"), LocationPath.Parse(@"C:\Holiday\b.jpg")], ".zip"));
        Assert.Equal("Archive.zip", ArchiveNaming.Suggest([], ".zip"));
    }

    [Theory]
    [InlineData("data", ArchiveFormat.Zip, "data.zip")]
    [InlineData("data.zip", ArchiveFormat.SevenZip, "data.7z")]
    [InlineData("data.7z", ArchiveFormat.Zip, "data.zip")]
    [InlineData("a:b?", ArchiveFormat.Zip, "a_b_.zip")]
    [InlineData("   ", ArchiveFormat.Zip, "Archive.zip")]
    public void WithExtension_GivesTheNameTheRightEnding(string name, ArchiveFormat format, string expected)
    {
        Assert.Equal(expected, ArchiveNaming.WithExtension(name, format));
        Assert.Equal(format, ArchiveNaming.FormatOf(expected));
    }

    [Fact]
    public void Rules_RefuseWhatCannotBePacked_OrUnpacked()
    {
        Assert.NotNull(ArchiveNaming.WhyNotCompress([]));
        Assert.NotNull(ArchiveNaming.WhyNotCompress([LocationPath.Parse("sftp://host/a.txt")]));
        Assert.NotNull(ArchiveNaming.WhyNotCompress([LocationPath.Parse(@"C:\a\x.zip\inner.txt")]));
        Assert.Null(ArchiveNaming.WhyNotCompress([LocationPath.Parse(@"C:\a\x.zip")]));
        Assert.NotNull(ArchiveNaming.WhyNotExtract([LocationPath.Parse(@"C:\a\x.zip\inner.zip")]));
        Assert.NotNull(ArchiveNaming.WhyNotExtract([LocationPath.Parse(@"C:\a\x.txt")]));
        Assert.Null(ArchiveNaming.WhyNotExtract([LocationPath.Parse(@"C:\a\x.zip"), LocationPath.Parse(@"C:\a\y.7z")]));
    }
}
