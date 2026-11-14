using Leviathan.Contracts;
using Leviathan.Core.Hashing;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Checksums;
using Leviathan.ViewModels.Commands;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>What the checksums window shows and writes, and how it is opened (spec §15.4).</summary>
public sealed class ChecksumViewTests
{
    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Md5 = "900150983cd24fb0d6963f7d28e17f72";

    private static ItemOutcome Done(string path, params (ChecksumKind Kind, string Hash)[] sums) =>
        new(path, ChecksumResults.Encode(sums.ToDictionary(s => s.Kind, s => s.Hash)), ItemStatus.Done);

    private static ChecksumRow Row(string path, params (ChecksumKind Kind, string Hash)[] sums) => ChecksumView.RowsFrom([Done(path, sums)])[0];

    [Fact]
    public void RowsFrom_TellsEachOutcomeApart()
    {
        var rows = ChecksumView.RowsFrom(
        [
            Done(@"D:\a.txt", (ChecksumKind.Sha256, Sha)),
            new(@"D:\b.txt", "Sha256=00", ItemStatus.Failed, $"{ChecksumResults.Mismatch}: expected 11, got 00."),
            new(@"D:\c.txt", null, ItemStatus.Failed, $"{ChecksumResults.Missing}: the file is not there."),
            new(@"D:\d.txt", null, ItemStatus.Failed, "Access to the path is denied."),
            new("sftp://h/e", null, ItemStatus.Skipped, "Only files on this PC can be checked."),
        ]);

        Assert.Equal([ChecksumState.Ok, ChecksumState.Mismatch, ChecksumState.Missing, ChecksumState.Failed, ChecksumState.Skipped], rows.Select(r => r.State));
        Assert.Equal(Sha, rows[0].Sums[ChecksumKind.Sha256]);
        Assert.Null(rows[0].Message);
        Assert.Contains("expected 11", rows[1].Message);
    }

    [Fact]
    public void Summary_SaysWhatIsWrongOrThatAllIsGood()
    {
        Assert.Equal("1 file · all good", ChecksumView.Summary([Row(@"D:\a", (ChecksumKind.Md5, Md5))]));
        Assert.Equal("0 files", ChecksumView.Summary([]));
        var rows = ChecksumView.RowsFrom(
        [
            Done(@"D:\a"),
            new(@"D:\b", null, ItemStatus.Failed, ChecksumResults.Mismatch + ": x"),
            new(@"D:\c", null, ItemStatus.Failed, ChecksumResults.Mismatch + ": x"),
            new(@"D:\d", null, ItemStatus.Failed, ChecksumResults.Missing + ": x"),
            new(@"D:\e", null, ItemStatus.Failed, "denied"),
        ]);

        Assert.Equal("5 files · 2 do not match · 1 missing · 1 could not be read", ChecksumView.Summary(rows));
    }

    [Fact]
    public void ChecksumFile_NamesFilesRelativeToTheFolder_WithForwardSlashes_AndSkipsFilesWithoutThatSum()
    {
        var rows = ChecksumView.RowsFrom([Done(@"D:\Data\a.txt", (ChecksumKind.Sha256, Sha)), Done(@"D:\Data\sub\b.txt", (ChecksumKind.Sha256, Sha)), Done(@"D:\Data\c.txt", (ChecksumKind.Md5, Md5)), Done(@"E:\Other\d.txt", (ChecksumKind.Sha256, Sha))]);

        var text = ChecksumView.ChecksumFile(rows, ChecksumKind.Sha256, @"D:\Data");

        Assert.Equal($"{Sha} *a.txt\n{Sha} *sub/b.txt\n{Sha} *d.txt\n", text);
    }

    [Fact]
    public void ChecksumFile_ThatIsReadBack_VerifiesTheSameFiles()
    {
        var rows = ChecksumView.RowsFrom([Done(@"D:\Data\sub\b.txt", (ChecksumKind.Sha256, Sha))]);

        var read = ChecksumManifest.Parse(ChecksumView.ChecksumFile(rows, ChecksumKind.Sha256, @"D:\Data"), "x.sha256");

        Assert.Equal(("sub/b.txt", Sha), (read.Entries[0].Name, read.Entries[0].Hash));
    }

    [Theory]
    [InlineData(Sha)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("  ba7816bf 8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\n")]
    [InlineData("0xba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void Matching_FindsTheFileWhoseSumWasPasted_WhateverTheCaseOrSpaces(string pasted)
    {
        var rows = ChecksumView.RowsFrom([Done(@"D:\a", (ChecksumKind.Sha256, Sha)), Done(@"D:\b", (ChecksumKind.Sha256, new string('0', 64)))]);

        var found = ChecksumView.Matching(rows, pasted);

        Assert.Equal(@"D:\a", Assert.Single(found).Path);
        Assert.Equal(ChecksumKind.Sha256, ChecksumView.KindOfPasted(pasted));
    }

    [Fact]
    public void Matching_NothingPasted_OrNothingEqual_FindsNothing()
    {
        var rows = ChecksumView.RowsFrom([Done(@"D:\a", (ChecksumKind.Md5, Md5))]);

        Assert.Empty(ChecksumView.Matching(rows, "  "));
        Assert.Empty(ChecksumView.Matching(rows, Sha));
        Assert.Null(ChecksumView.KindOfPasted("12345"));
    }

    [Fact]
    public void DefaultFolder_IsTheCheckedFolder_OrWhatTheFilesShare()
    {
        var inFolder = ChecksumView.RowsFrom([Done(@"D:\Data\a.txt"), Done(@"D:\Data\sub\b.txt")]);
        var scattered = ChecksumView.RowsFrom([Done(@"D:\Data\x\a.txt"), Done(@"D:\Data\y\b.txt")]);

        Assert.Equal(@"D:\Data", ChecksumView.DefaultFolder([LocationPath.Parse(@"D:\Data")], inFolder));
        Assert.Equal(@"D:\Data", ChecksumView.DefaultFolder([LocationPath.Parse(@"D:\Data\x\a.txt"), LocationPath.Parse(@"D:\Data\y\b.txt")], scattered));
        Assert.Equal(@"D:\Data\x", ChecksumView.DefaultFolder([LocationPath.Parse(@"D:\Data\x\a.txt")], ChecksumView.RowsFrom([Done(@"D:\Data\x\a.txt")])));
        Assert.Equal(string.Empty, ChecksumView.DefaultFolder([], []));
    }

    [Theory]
    [InlineData(@"D:\Data", ChecksumKind.Sha256, "Data.sha256")]
    [InlineData(@"D:\movie.mkv", ChecksumKind.Md5, "movie.mkv.md5")]
    [InlineData(@"D:\Data\x\a.txt", ChecksumKind.Crc32, "a.txt.crc32")]
    public void DefaultFileName_IsTheCheckedItemsNameWithTheKindsExtension(string item, ChecksumKind kind, string expected)
    {
        Assert.Equal(expected, ChecksumView.DefaultFileName([LocationPath.Parse(item)], kind));
    }

    private static FileEntry FileAt(string path) =>
        new(Path.GetFileName(path), TabTestSupport.P(path), FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Fact]
    public async Task TheCommand_OpensTheWindowForTheItem_AndVerifiesALoneChecksumFile()
    {
        var h = new Harness();
        await h.Ready;
        var photo = FileAt(@"C:\a\photo.jpg");
        var sums = FileAt(@"C:\a\photos.sha256");

        await h.Run(CommandIds.ShowChecksum, null, photo);
        await h.Run(CommandIds.ShowChecksum, null, sums);

        Assert.Equal([([photo.Location], false), ([sums.Location], true)], h.Shell.ChecksumWindows.Select(w => (w.Items, w.Verify)));
    }

    [Fact]
    public async Task TheCommand_OfSomethingOnAServer_SaysSoInsteadOfOpeningTheWindow()
    {
        var h = new Harness();
        await h.Ready;
        var remote = new FileEntry("a.bin", LocationPath.Parse("sftp://host/a.bin"), FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

        await h.Run(CommandIds.ShowChecksum, null, remote);

        Assert.Empty(h.Shell.ChecksumWindows);
        Assert.Single(h.Shell.Messages);
    }
}
