using Leviathan.Core.Archives;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Tests;

/// <summary>Where a location splits into archive and inside, and how an archive's flat list becomes folders (spec §15.4).</summary>
public sealed class ArchiveFolderTests
{
    private static readonly DateTimeOffset Fallback = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly LocationPath Archive = LocationPath.Parse(@"D:\Stuff\photos.zip");

    private static ArchiveEntryInfo File(string path, long size = 10, DateTimeOffset? modified = null) => new(path, false, size, modified, null);

    private static ArchiveEntryInfo Folder(string path) => new(path, true, 0, null, null);

    private static string[] Names(IReadOnlyList<FileEntry> entries) => [.. entries.Select(e => e.Name).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData(@"D:\Stuff\photos.zip", @"D:\Stuff\photos.zip", "")]
    [InlineData(@"D:\Stuff\photos.zip\2024\a.jpg", @"D:\Stuff\photos.zip", "2024/a.jpg")]
    [InlineData(@"D:\Stuff\PHOTOS.ZIP\x", @"D:\Stuff\PHOTOS.ZIP", "x")]
    [InlineData(@"\\nas\media\old\box.7z\disc1", @"\\nas\media\old\box.7z", "disc1")]
    [InlineData(@"D:\a.tar.gz\b", @"D:\a.tar.gz", "b")]
    public void Split_FindsTheArchiveAndTheWayInside(string location, string archive, string inner)
    {
        var split = ArchiveKinds.Split(LocationPath.Parse(location));

        Assert.NotNull(split);
        Assert.Equal(archive, split.Archive.Value);
        Assert.Equal(inner, split.InnerPath);
    }

    [Theory]
    [InlineData(@"D:\Stuff\photos")]
    [InlineData(@"D:\Stuff\readme.txt")]
    [InlineData(@"D:\.zip")]
    [InlineData(@"\\nas")]
    [InlineData("sftp://host/x/a.zip/b")]
    public void Split_NoArchiveInThePath_IsNull(string location)
    {
        Assert.Null(ArchiveKinds.Split(LocationPath.Parse(location)));
    }

    [Fact]
    public void Split_AnArchiveInsideAnArchive_IsJustAFile()
    {
        var split = ArchiveKinds.Split(LocationPath.Parse(@"D:\outer.zip\inner.zip\x.txt"));

        Assert.Equal(@"D:\outer.zip", split!.Archive.Value);
        Assert.Equal("inner.zip/x.txt", split.InnerPath);
    }

    [Fact]
    public void IsInside_TheArchiveItselfIsNot()
    {
        Assert.False(ArchiveKinds.IsInside(Archive));
        Assert.True(ArchiveKinds.IsInside(Archive.Combine("a")));
    }

    [Fact]
    public void Children_TopLevel_MakesUpFoldersThatTheArchiveOnlyImplies()
    {
        ArchiveEntryInfo[] entries = [File("readme.txt"), File("2024/jan/a.jpg"), File("2024/b.jpg"), File("2025/c.jpg")];

        var top = ArchiveFolder.Children(Archive, [], entries, Fallback)!;

        Assert.Equal(["2024", "2025", "readme.txt"], Names(top));
        Assert.All(top.Where(e => e.Name != "readme.txt"), e => Assert.Equal(FileEntryKind.Folder, e.Kind));
    }

    [Fact]
    public void Children_Inside_ListsOnlyThatLevel()
    {
        ArchiveEntryInfo[] entries = [File("readme.txt"), File("2024/jan/a.jpg"), File("2024/b.jpg"), File("2025/c.jpg")];

        var inside = ArchiveFolder.Children(Archive, ["2024"], entries, Fallback)!;

        Assert.Equal(["b.jpg", "jan"], Names(inside));
        Assert.Equal(@"D:\Stuff\photos.zip\2024\b.jpg", inside.Single(e => e.Name == "b.jpg").Location.Value);
    }

    [Fact]
    public void Children_NoSuchFolder_IsNull_NotEmpty()
    {
        ArchiveEntryInfo[] entries = [File("a/b.txt")];

        Assert.Null(ArchiveFolder.Children(Archive, ["nope"], entries, Fallback));
        Assert.Null(ArchiveFolder.Children(Archive, ["a", "x"], entries, Fallback));
    }

    [Fact]
    public void Children_AnEmptyFolderEntry_ExistsAndIsEmpty()
    {
        ArchiveEntryInfo[] entries = [Folder("empty"), File("a.txt")];

        var inside = ArchiveFolder.Children(Archive, ["empty"], entries, Fallback);

        Assert.NotNull(inside);
        Assert.Empty(inside);
    }

    [Fact]
    public void Children_PrefixIsAWholeSegment_NotJustTheStartOfAName()
    {
        ArchiveEntryInfo[] entries = [File("photos2/a.jpg")];

        Assert.Null(ArchiveFolder.Children(Archive, ["photos"], entries, Fallback));
    }

    [Fact]
    public void Children_UsesTheEntryTime_ElseTheArchivesOwn()
    {
        var stored = new DateTimeOffset(2020, 5, 5, 0, 0, 0, TimeSpan.Zero);
        ArchiveEntryInfo[] entries = [File("a.txt", modified: stored), File("b.txt")];

        var top = ArchiveFolder.Children(Archive, [], entries, Fallback)!;

        Assert.Equal(stored, top.Single(e => e.Name == "a.txt").Modified);
        Assert.Equal(Fallback, top.Single(e => e.Name == "b.txt").Modified);
    }

    [Fact]
    public void Children_FolderEntryAndImpliedFolder_AreOneItem()
    {
        ArchiveEntryInfo[] entries = [Folder("docs"), File("docs/a.txt")];

        var top = ArchiveFolder.Children(Archive, [], entries, Fallback)!;

        Assert.Single(top);
        Assert.Equal(FileEntryKind.Folder, top[0].Kind);
    }

    [Fact]
    public void Children_NamesCompareWithoutCase_LikeTheRestOfWindows()
    {
        ArchiveEntryInfo[] entries = [File("Docs/a.txt")];

        var inside = ArchiveFolder.Children(Archive, ["docs"], entries, Fallback);

        Assert.NotNull(inside);
        Assert.Equal(["a.txt"], Names(inside));
    }

    [Fact]
    public void Children_NamesWindowsCannotSpell_AreLeftOut_NotAFailure()
    {
        ArchiveEntryInfo[] entries = [File("good.txt"), File("what?.txt"), File("a:b.txt"), File("sub/inside.txt")];

        var top = ArchiveFolder.Children(Archive, [], entries, Fallback)!;

        Assert.Equal(["good.txt", "sub"], Names(top));
    }

    [Fact]
    public void Find_ReturnsFilesOnly()
    {
        ArchiveEntryInfo[] entries = [Folder("a"), File("a/b.txt", 5)];

        Assert.Equal(5, ArchiveFolder.Find(entries, "A/B.TXT")!.Size);
        Assert.Null(ArchiveFolder.Find(entries, "a"));
    }

    [Fact]
    public void NormalizePath_TurnsBackslashesAndTrimsSlashes()
    {
        Assert.Equal("a/b/c.txt", ArchiveEntryInfo.NormalizePath(@"\a\b/c.txt/"));
    }
}
