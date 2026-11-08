using System.Formats.Tar;
using System.IO.Compression;
using Leviathan.Core.Model;
using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

public sealed class TreemapLayoutTests
{
    [Fact]
    public void Layout_GivesEachWeightAnAreaInProportion()
    {
        double[] weights = [6, 3, 2, 1];

        var rects = TreemapLayout.Layout(weights, 120, 100);

        for (var i = 0; i < weights.Length; i++)
        {
            Assert.Equal(120 * 100 * weights[i] / 12, rects[i].Area, 6);
        }
    }

    [Fact]
    public void Layout_FillsTheWholeAreaWithoutOverlap()
    {
        double[] weights = [50, 20, 15, 10, 4, 1];

        var rects = TreemapLayout.Layout(weights, 300, 200);

        Assert.Equal(300 * 200, rects.Sum(r => r.Area), 4);
        Assert.All(rects, r =>
        {
            Assert.InRange(r.X, -1e-9, 300 + 1e-9);
            Assert.InRange(r.X + r.Width, 0, 300 + 1e-6);
            Assert.InRange(r.Y + r.Height, 0, 200 + 1e-6);
        });
        for (var i = 0; i < rects.Length; i++)
        {
            for (var j = i + 1; j < rects.Length; j++)
            {
                var overlapX = Math.Min(rects[i].X + rects[i].Width, rects[j].X + rects[j].Width) - Math.Max(rects[i].X, rects[j].X);
                var overlapY = Math.Min(rects[i].Y + rects[i].Height, rects[j].Y + rects[j].Height) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(overlapX > 1e-6 && overlapY > 1e-6, $"{i} overlaps {j}");
            }
        }
    }

    [Fact]
    public void Layout_KeepsTheInputOrder_WhateverTheWeights()
    {
        var rects = TreemapLayout.Layout([1, 10], 100, 100);

        Assert.True(rects[1].Area > rects[0].Area);
    }

    [Fact]
    public void Layout_GivesZeroAndNegativeWeightsNothing()
    {
        var rects = TreemapLayout.Layout([0, 5, -3], 100, 100);

        Assert.Equal(0, rects[0].Area);
        Assert.Equal(0, rects[2].Area);
        Assert.Equal(10000, rects[1].Area, 6);
    }

    [Fact]
    public void Layout_OfNothingOrNoSpace_IsEmpty()
    {
        Assert.Empty(TreemapLayout.Layout([], 100, 100));
        Assert.All(TreemapLayout.Layout([1, 2], 0, 100), r => Assert.Equal(0, r.Area));
        Assert.All(TreemapLayout.Layout([0, 0], 100, 100), r => Assert.Equal(0, r.Area));
    }

    [Fact]
    public void Layout_KeepsRectanglesNearlySquare()
    {
        var rects = TreemapLayout.Layout(Enumerable.Repeat(1.0, 16).ToArray(), 400, 400);

        Assert.All(rects, r => Assert.InRange(r.Width / r.Height, 0.99, 1.01));
    }
}

public sealed class SelectionSummaryTests
{
    private static FileEntry File(string name, long size) =>
        new(name, TabTestSupport.P($@"C:\a\{name}"), FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, TabTestSupport.P($@"C:\a\{name}"), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    [Fact]
    public void From_CountsFilesFoldersAndBytes()
    {
        var s = SelectionSummary.From([File("a.mkv", 1000), File("b.mkv", 500), File("c.txt", 5), Folder("d")]);

        Assert.Equal(3, s.Files);
        Assert.Equal(1, s.Folders);
        Assert.Equal(1505, s.FileBytes);
    }

    [Fact]
    public void From_GroupsByExtensionIgnoringCase_BiggestFirst()
    {
        var s = SelectionSummary.From([File("a.MKV", 1000), File("b.mkv", 500), File("c.txt", 5), File("noext", 7)]);

        Assert.Equal([".mkv", "no extension", ".txt"], s.Types.Select(t => t.Label));
        Assert.Equal(2, s.Types[0].Count);
        Assert.Equal(1500, s.Types[0].Bytes);
    }

    [Fact]
    public void From_FoldsTheSmallestKindsIntoOther()
    {
        var files = Enumerable.Range(1, 8).Select(i => File($"f.ext{i}", i * 10)).ToList();

        var s = SelectionSummary.From(files);

        Assert.Equal(SelectionSummary.MaxTypes, s.Types.Count);
        Assert.Equal("other", s.Types[^1].Label);
        Assert.Equal(4, s.Types[^1].Count);
        Assert.Equal(files.Sum(f => f.Size), s.Types.Sum(t => t.Bytes));
    }

    [Fact]
    public void Headline_ReadsInPlainWords()
    {
        var headline = SelectionSummary.From([File("a", 1000), File("b", 500), File("c", 36), Folder("d")]).Headline;

        Assert.StartsWith("4 items · 3 files, 1 folders · ", headline);
    }

    [Fact]
    public void From_OfNothing_IsEmpty()
    {
        var s = SelectionSummary.From([]);

        Assert.Equal(0, s.Files + s.Folders);
        Assert.Empty(s.Types);
    }
}

public sealed class ArchiveListingTests
{
    private static MemoryStream Zip(params (string Name, int Size)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, size) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(new byte[size]);
            }
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void ReadZip_ListsItemsWithSizesAndFolders()
    {
        using var ms = Zip(("docs/", 0), ("docs/a.txt", 10), ("b.bin", 100));

        var contents = ArchiveListing.ReadZip(ms);

        Assert.Equal(["docs", "docs/a.txt", "b.bin"], contents.Items.Select(i => i.Path));
        Assert.True(contents.Items[0].IsFolder);
        Assert.Equal(110, contents.TotalBytes);
        Assert.False(contents.Truncated);
    }

    [Fact]
    public void ReadZip_OfABigArchive_ListsTheFirstOnesAndSaysSo()
    {
        using var ms = Zip(Enumerable.Range(0, ArchiveListing.MaxItems + 5).Select(i => ($"f{i}.txt", 1)).ToArray());

        var contents = ArchiveListing.ReadZip(ms);

        Assert.Equal(ArchiveListing.MaxItems, contents.Items.Count);
        Assert.True(contents.Truncated);
        Assert.Equal(ArchiveListing.MaxItems + 5, contents.TotalBytes);
    }

    [Fact]
    public void ReadZip_OfGarbage_Throws()
    {
        using var ms = new MemoryStream("not a zip at all, just text"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => ArchiveListing.ReadZip(ms));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadTar_ListsItemsPlainOrGzipped(bool gzip)
    {
        var raw = new MemoryStream();
        using (var writer = new TarWriter(raw, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "src/a.txt") { DataStream = new MemoryStream(new byte[42]) };
            writer.WriteEntry(entry);
        }

        raw.Position = 0;
        Stream source = raw;
        if (gzip)
        {
            var zipped = new MemoryStream();
            using (var gz = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
            {
                raw.CopyTo(gz);
            }

            zipped.Position = 0;
            source = zipped;
        }

        var contents = ArchiveListing.ReadTar(source, gzip);

        Assert.Single(contents.Items);
        Assert.Equal("src/a.txt", contents.Items[0].Path);
        Assert.Equal(42, contents.Items[0].Size);
    }

    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".NUPKG", true)]
    [InlineData(".tgz", true)]
    [InlineData(".7z", false)]
    [InlineData(".rar", false)]
    public void CanList_KnowsWhichKindsItCanRead(string extension, bool expected) => Assert.Equal(expected, ArchiveListing.CanList(extension));
}
