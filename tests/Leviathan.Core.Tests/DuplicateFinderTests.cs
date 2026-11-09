using Leviathan.Core.Duplicates;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Core.Tests;

/// <summary>Name-based duplicates, ported from vlib (spec §12.7).</summary>
public sealed class DuplicateFinderTests
{
    private static FileEntry F(string name, long size = 1000)
    {
        var location = LocationPath.Parse(@"C:\v\" + name);
        return new FileEntry(name, location, FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
    }

    private const string Scott = "OnlyFans.26.10.05.Mia.Amor.And.CC.Wilde.Lesbian.Latinas.Make.Step.Brother.Feel.Included.Scottstark.XXX.720p.MP4-P2P.mp4";
    private const string Plain = "OnlyFans.26.10.05.Mia.Amor.And.CC.Wilde.Lesbian.Latinas.Make.Step.Brother.Feel.Included.XXX.1080p.MP4-P2P.mp4";

    [Fact]
    public void Normalize_DropsResolutionContainerAndNoise()
    {
        var words = NameSimilarity.Normalize("Show.S01.Title [1080p] x265 WEBDL 1920x1080");

        Assert.Equal(["s01", "show", "title"], words.Order());
    }

    [Fact]
    public void Jaccard_OfTheExampleNames_IsHigh()
    {
        var a = NameSimilarity.Normalize(Path.GetFileNameWithoutExtension(Scott));
        var b = NameSimilarity.Normalize(Path.GetFileNameWithoutExtension(Plain));

        Assert.True(NameSimilarity.Jaccard(a, b) > 0.9);
    }

    [Fact]
    public void Find_GroupsTheExampleNames_AndKeepsThe1080p()
    {
        var groups = DuplicateFinder.Find([F(Scott, 500), F(Plain, 900), F("Something.Else.Entirely.mp4")]);

        var group = Assert.Single(groups);
        Assert.Equal(Plain, group.Keeper.Name);
        Assert.Equal([Scott], group.Others.Select(f => f.Name));
        Assert.Equal(500, group.ReclaimableBytes);
    }

    [Fact]
    public void Find_LeavesUnrelatedNamesAlone()
    {
        Assert.Empty(DuplicateFinder.Find([F("Holiday.In.Spain.mp4"), F("Birthday.Party.Cake.mp4")]));
    }

    [Fact]
    public void Find_TreatsASameSizeConflictCopyAsExact_AndKeepsTheOriginal()
    {
        var groups = DuplicateFinder.Find([F("clip.1.mp4", 777), F("clip.mp4", 777)]);

        var group = Assert.Single(groups);
        Assert.Equal("clip.mp4", group.Keeper.Name);
        Assert.Equal(1, group.Similarity[0]);
    }

    [Fact]
    public void Find_AConflictCopyOfADifferentSize_IsNotExact_ButIsStillSimilarByName()
    {
        var groups = DuplicateFinder.Find([F("long.name.of.a.clip.mp4", 100), F("long.name.of.a.clip.1.mp4", 200)]);

        Assert.Single(groups);
    }

    [Theory]
    [InlineData(0.99, 0)]
    [InlineData(0.5, 1)]
    public void Find_TheThresholdDecidesHowLooseTheMatchIs(double threshold, int expectedGroups)
    {
        // Three of five words in common: Jaccard 3/7.
        var groups = DuplicateFinder.Find([F("alpha.beta.gamma.delta.mp4"), F("alpha.beta.gamma.omega.mp4")], threshold);

        Assert.Equal(expectedGroups, groups.Count);
    }

    [Fact]
    public void Keeper_PrefersHevc1080_ThenHevc720_ThenLargest1080_ThenSmallest2160()
    {
        Assert.Equal("a.x265.1080p.mp4", DuplicateFinder.Keeper([F("a.1080p.mp4", 9000), F("a.x265.1080p.mp4", 100), F("a.x265.720p.mp4")]).Name);
        Assert.Equal("a.x265.720p.mp4", DuplicateFinder.Keeper([F("a.1080p.mp4", 9000), F("a.x265.720p.mp4", 100)]).Name);
        Assert.Equal("b.1080p.mp4", DuplicateFinder.Keeper([F("a.1080p.mp4", 100), F("b.1080p.mp4", 900), F("c.720p.mp4", 5000)]).Name);
        Assert.Equal("small.2160p.mp4", DuplicateFinder.Keeper([F("big.2160p.mp4", 900), F("small.2160p.mp4", 100)]).Name);
    }

    [Fact]
    public void Keeper_OnATie_PrefersMkv_ThenTheLongerName()
    {
        Assert.Equal("a.1080p.mkv", DuplicateFinder.Keeper([F("a.1080p.mp4", 100), F("a.1080p.mkv", 100)]).Name);
        Assert.Equal("a.1080p.group.mp4", DuplicateFinder.Keeper([F("a.1080p.mp4", 100), F("a.1080p.group.mp4", 100)]).Name);
    }

    [Fact]
    public void FindAgainst_AnIdenticalFileInTheDestination_IsTheExtraOne()
    {
        var matches = DuplicateFinder.FindAgainst([F("clip.mp4", 500), F("new.thing.mp4")], [F("clip.mp4", 500), F("other.mp4")]);

        var match = Assert.Single(matches);
        Assert.Equal("clip.mp4", match.Source.Name);
        Assert.True(match.Identical);
        Assert.True(match.DestinationIsBetter);
    }

    [Fact]
    public void FindAgainst_ABetterSourceCopy_IsNotMarkedAsTheExtraOne()
    {
        var matches = DuplicateFinder.FindAgainst([F(Plain, 900)], [F(Scott, 500)]);

        var match = Assert.Single(matches);
        Assert.False(match.DestinationIsBetter);
        Assert.False(match.Identical);
        Assert.True(match.Similarity > 0.9);
    }

    [Fact]
    public void FindAgainst_ABetterDestinationCopy_MakesTheSourceTheExtraOne()
    {
        var matches = DuplicateFinder.FindAgainst([F(Scott, 500)], [F(Plain, 900)]);

        Assert.True(Assert.Single(matches).DestinationIsBetter);
    }

    [Fact]
    public void FindAgainst_UnrelatedNamesAndFolders_NeverMatch()
    {
        Assert.Empty(DuplicateFinder.FindAgainst([F("Holiday.In.Spain.mp4")], [F("Birthday.Party.Cake.mp4")]));
        Assert.Empty(DuplicateFinder.FindAgainst([], [F("a.mp4")]));
    }

    [Fact]
    public void Find_IgnoresFolders_AndAFolderOfThousandsIsFast()
    {
        var files = Enumerable.Range(0, 5000).Select(i => F($"unique{i}.word{i}.thing{i}.mp4")).ToList();
        files.Add(new FileEntry("sub", LocationPath.Parse(@"C:\v\sub"), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var groups = DuplicateFinder.Find(files);

        Assert.Empty(groups);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }
}
