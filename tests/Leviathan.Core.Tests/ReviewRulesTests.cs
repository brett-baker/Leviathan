using Leviathan.Core.Duplicates;

namespace Leviathan.Core.Tests;

/// <summary>Which copy of a group to keep (spec §15.2).</summary>
public sealed class ReviewRulesTests
{
    private static ReviewFile F(string path, long size = 100, int day = 1, int w = 0, int h = 0) => new(path, size, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc), w, h);

    [Fact]
    public void HighestResolution_KeepsTheMostPixels_ThenTheLargerFile()
    {
        var small = F(@"C:\a\1.jpg", 500, w: 800, h: 600);
        var big = F(@"C:\a\2.jpg", 100, w: 1920, h: 1080);
        var bigger = F(@"C:\a\3.jpg", 300, w: 1920, h: 1080);

        Assert.Same(bigger, ReviewRules.Keep([small, big, bigger], KeepRule.HighestResolution));
    }

    [Fact]
    public void HighestResolution_WithNoDimensions_FallsBackToTheLargestFile()
    {
        var a = F(@"C:\a\1.mp4", 100);
        var b = F(@"C:\a\2.mp4", 900);

        Assert.Same(b, ReviewRules.Keep([a, b], KeepRule.HighestResolution));
    }

    [Fact]
    public void Largest_Newest_Oldest_PickAsNamed()
    {
        var a = F(@"C:\a\1", size: 10, day: 5);
        var b = F(@"C:\a\2", size: 30, day: 1);
        var c = F(@"C:\a\3", size: 20, day: 9);
        IReadOnlyList<ReviewFile> group = [a, b, c];

        Assert.Same(b, ReviewRules.Keep(group, KeepRule.Largest));
        Assert.Same(c, ReviewRules.Keep(group, KeepRule.Newest));
        Assert.Same(b, ReviewRules.Keep(group, KeepRule.Oldest));
    }

    [Fact]
    public void PreferredFolder_KeepsTheCopyInsideIt_IncludingSubfolders_IgnoringCaseAndSlashEnds()
    {
        var outside = F(@"D:\Downloads\x.mkv", day: 1);
        var inside = F(@"D:\Library\Films\x.mkv", day: 9);

        Assert.Same(inside, ReviewRules.Keep([outside, inside], KeepRule.PreferredFolder, @"d:\library\"));
    }

    [Fact]
    public void PreferredFolder_DoesNotMatchASiblingWithTheSamePrefix()
    {
        var sibling = F(@"D:\Library2\x.mkv", day: 1);
        var inside = F(@"D:\Library\x.mkv", day: 9);

        Assert.Same(inside, ReviewRules.Keep([sibling, inside], KeepRule.PreferredFolder, @"D:\Library"));
    }

    [Fact]
    public void PreferredFolder_WithNoCopyInIt_OrNoFolder_KeepsTheOldest()
    {
        var older = F(@"E:\a\x", day: 1);
        var newer = F(@"E:\b\x", day: 2);

        Assert.Same(older, ReviewRules.Keep([newer, older], KeepRule.PreferredFolder, @"D:\Library"));
        Assert.Same(older, ReviewRules.Keep([newer, older], KeepRule.PreferredFolder, null));
    }

    [Fact]
    public void PreferredFolder_WithSeveralInsideIt_KeepsTheOldestOfThose()
    {
        var a = F(@"D:\L\a", day: 4);
        var b = F(@"D:\L\b", day: 2);
        var outside = F(@"E:\c", day: 1);

        Assert.Same(b, ReviewRules.Keep([a, outside, b], KeepRule.PreferredFolder, @"D:\L"));
    }

    [Fact]
    public void Ties_GoToTheFirstFound_SoTheAnswerIsStable()
    {
        var a = F(@"C:\a\1", 10, 3);
        var b = F(@"C:\a\2", 10, 3);

        Assert.Same(a, ReviewRules.Keep([a, b], KeepRule.Largest));
        Assert.Same(a, ReviewRules.Keep([a, b], KeepRule.Newest));
        Assert.Same(a, ReviewRules.Keep([a, b], KeepRule.Oldest));
    }

    [Fact]
    public void Extras_AreEveryoneButTheKeeper_InGroupOrder()
    {
        var a = F(@"C:\a\1", 10);
        var b = F(@"C:\a\2", 30);
        var c = F(@"C:\a\3", 20);

        Assert.Equal([a, c], ReviewRules.Extras([a, b, c], KeepRule.Largest));
    }

    [Fact]
    public void Keep_OfAnEmptyGroup_Throws() =>
        Assert.Throws<ArgumentException>(() => ReviewRules.Keep([], KeepRule.Largest));

    [Fact]
    public void ReviewFile_SplitsItsPath()
    {
        var f = F(@"C:\a\b\c.txt");

        Assert.Equal("c.txt", f.Name);
        Assert.Equal(@"C:\a\b", f.Folder);
    }
}
