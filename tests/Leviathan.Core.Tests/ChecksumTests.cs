using System.Text;
using Leviathan.Core.Hashing;

namespace Leviathan.Core.Tests;

/// <summary>The checksum kinds, and reading and writing checksum files (spec §9.2, §15.4).</summary>
public sealed class ChecksumTests
{
    private static MemoryStream Bytes(string text) => new(Encoding.ASCII.GetBytes(text));

    [Fact]
    public async Task Compute_AllSixKinds_MatchTheirPublishedTestVectors()
    {
        var sums = await FileChecksums.ComputeAsync(Bytes("123456789"), ChecksumKinds.All.ToList(), null, CancellationToken.None);

        Assert.Equal("25f9e794323b453885f5181f1b624d0b", sums[ChecksumKind.Md5]);
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", sums[ChecksumKind.Sha1]);
        Assert.Equal("15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225", sums[ChecksumKind.Sha256]);
        Assert.Equal("d9e6762dd1c8eaf6d61b3c6192fc408d4d6d5f1176d0c29169bc24e71c3f274ad27fcd5811b313d681f7e55ec02d73d499c95455b6b5bb503acf574fba8ffe85", sums[ChecksumKind.Sha512]);
        Assert.Equal("cbf43926", sums[ChecksumKind.Crc32]);
        Assert.Equal(16, sums[ChecksumKind.XxHash3].Length);
    }

    [Fact]
    public void Compute_OfNothing_GivesTheKnownEmptySums()
    {
        Assert.Equal("2d06800538d394c2", FileChecksums.Compute([], ChecksumKind.XxHash3));
        Assert.Equal("00000000", FileChecksums.Compute([], ChecksumKind.Crc32));
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", FileChecksums.Compute([], ChecksumKind.Sha1));
    }

    [Fact]
    public async Task Compute_AcrossBlocks_EqualsOneShot()
    {
        var data = new byte[(3 * 1024 * 1024) + 17];
        new Random(5).NextBytes(data);

        var streamed = await FileChecksums.ComputeAsync(new MemoryStream(data), [ChecksumKind.XxHash3, ChecksumKind.Crc32, ChecksumKind.Sha512], null, CancellationToken.None);

        Assert.Equal(FileChecksums.Compute(data, ChecksumKind.XxHash3), streamed[ChecksumKind.XxHash3]);
        Assert.Equal(FileChecksums.Compute(data, ChecksumKind.Crc32), streamed[ChecksumKind.Crc32]);
        Assert.Equal(FileChecksums.Compute(data, ChecksumKind.Sha512), streamed[ChecksumKind.Sha512]);
    }

    [Theory]
    [InlineData(ChecksumKind.Md5, ".md5", 32)]
    [InlineData(ChecksumKind.Sha1, ".sha1", 40)]
    [InlineData(ChecksumKind.Sha256, ".sha256", 64)]
    [InlineData(ChecksumKind.Sha512, ".sha512", 128)]
    [InlineData(ChecksumKind.XxHash3, ".xxh3", 16)]
    [InlineData(ChecksumKind.Crc32, ".crc32", 8)]
    public void Kinds_KnowTheirExtensionAndLength_AndAreToldApartByLength(ChecksumKind kind, string extension, int length)
    {
        Assert.Equal(extension, ChecksumKinds.Extension(kind));
        Assert.Equal(length, ChecksumKinds.HexLength(kind));
        Assert.Equal(kind, ChecksumKinds.FromExtension(extension.ToUpperInvariant()));
        Assert.Equal(kind, ChecksumKinds.FromHexLength(length));
    }

    [Fact]
    public void FromExtension_KnowsTheOtherSpellings_AndNothingElse()
    {
        Assert.Equal(ChecksumKind.Crc32, ChecksumKinds.FromExtension(".sfv"));
        Assert.Equal(ChecksumKind.Sha256, ChecksumKinds.FromExtension(".sha256sum"));
        Assert.Null(ChecksumKinds.FromExtension(".txt"));
        Assert.Null(ChecksumKinds.FromHexLength(20));
    }

    private const string Md5 = "d41d8cd98f00b204e9800998ecf8427e";
    private const string Sha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Parse_GnuLines_BothModes_AndPathsWithSpaces()
    {
        var text = $"{Sha256}  photos/a b.jpg\n{Sha256} *c.txt\r\n\n# a comment\n";

        var result = ChecksumManifest.Parse(text, "all.sha256");

        Assert.Equal(["photos/a b.jpg", "c.txt"], result.Entries.Select(e => e.Name));
        Assert.All(result.Entries, e => Assert.Equal(ChecksumKind.Sha256, e.Kind));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Parse_BsdLines()
    {
        var result = ChecksumManifest.Parse($"MD5 (some file.iso) = {Md5.ToUpperInvariant()}\nSHA256 (x.bin) = {Sha256}", "any.txt");

        Assert.Equal([("some file.iso", ChecksumKind.Md5, Md5), ("x.bin", ChecksumKind.Sha256, Sha256)], result.Entries.Select(e => (e.Name, e.Kind, e.Hash)));
    }

    [Fact]
    public void Parse_Sfv_TakesTheCrcAtTheEndOfTheLine()
    {
        var result = ChecksumManifest.Parse("; made by a tool\r\nmovie part 1.rar CBF43926\r\n", "release.sfv");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("movie part 1.rar", entry.Name);
        Assert.Equal(ChecksumKind.Crc32, entry.Kind);
        Assert.Equal("cbf43926", entry.Hash);
    }

    [Fact]
    public void Parse_ALoneSum_BelongsToTheFileWithTheChecksumFilesName()
    {
        var result = ChecksumManifest.Parse(Sha256 + "\n", "ubuntu.iso.sha256");

        Assert.Equal("ubuntu.iso", Assert.Single(result.Entries).Name);
    }

    [Fact]
    public void Parse_MixesKinds_StripsDotSlash_AndReportsWhatItCannotRead()
    {
        var text = $"{Md5}  ./a.txt\n{Sha256}  b.txt\nnot a checksum line\n1234567890123456789012345  c.txt\n";

        var result = ChecksumManifest.Parse(text, "mixed.txt");

        Assert.Equal([("a.txt", ChecksumKind.Md5), ("b.txt", ChecksumKind.Sha256)], result.Entries.Select(e => (e.Name, e.Kind)));
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public void Parse_AByteOrderMark_AndWindowsLineEndings_AreHarmless()
    {
        var result = ChecksumManifest.Parse("﻿" + Md5 + "  a.txt\r\n", "x.md5");

        Assert.Equal("a.txt", Assert.Single(result.Entries).Name);
    }

    [Fact]
    public void Format_Gnu_UsesBinaryMarkers_ForwardSlashes_AndLowercase()
    {
        var text = ChecksumManifest.Format([(@"sub\a.txt", Sha256.ToUpperInvariant()), ("b.txt", Sha256)], ChecksumKind.Sha256);

        Assert.Equal($"{Sha256} *sub/a.txt\n{Sha256} *b.txt\n", text);
    }

    [Fact]
    public void Format_Crc32_IsSfv()
    {
        Assert.Equal("a.bin cbf43926\r\n", ChecksumManifest.Format([("a.bin", "CBF43926")], ChecksumKind.Crc32));
    }

    [Fact]
    public void Format_ThenParse_RoundTrips_ForEveryKind()
    {
        foreach (var kind in ChecksumKinds.All)
        {
            var hash = FileChecksums.Compute(Encoding.ASCII.GetBytes("abc"), kind);

            var back = ChecksumManifest.Parse(ChecksumManifest.Format([("dir/one two.txt", hash)], kind), "x" + ChecksumKinds.Extension(kind));

            var entry = Assert.Single(back.Entries);
            Assert.Equal(("dir/one two.txt", hash, kind), (entry.Name, entry.Hash, entry.Kind));
        }
    }
}
