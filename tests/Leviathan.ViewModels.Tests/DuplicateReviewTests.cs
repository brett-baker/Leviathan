using Leviathan.Contracts;
using Leviathan.Core.Duplicates;
using Leviathan.ViewModels.Duplicates;

namespace Leviathan.ViewModels.Tests;

/// <summary>The state of the duplicate results window (spec §15.2).</summary>
public sealed class DuplicateReviewTests
{
    private static ReviewFile F(string path, long size = 100, int day = 1) => new(path, size, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));

    private static ReviewGroup G(params ReviewFile[] files) => new("k" + files[0].Path, files, true, files[0]);

    [Fact]
    public void ANewReview_KeepsTheOldestCopyOfEachGroup()
    {
        var review = new DuplicateReview([G(F(@"C:\a", day: 5), F(@"C:\b", day: 2))]);

        Assert.Equal(@"C:\b", review.Groups[0].Keep.Path);
        Assert.Equal(1, review.ExtraCount);
        Assert.Equal(100, review.ExtraBytes);
    }

    [Fact]
    public void Apply_ChangesTheKeptCopyInEveryGroup()
    {
        var review = new DuplicateReview([G(F(@"C:\a", day: 1), F(@"C:\b", day: 9)), G(F(@"D:\x", day: 3), F(@"D:\y", day: 4))]);

        review.Apply(KeepRule.Newest, null);

        Assert.Equal([@"C:\b", @"D:\y"], review.Groups.Select(g => g.Keep.Path));
        Assert.Equal(KeepRule.Newest, review.Rule);
    }

    [Fact]
    public void ToCleanup_CarriesTheKeeper_AndWhetherTheGroupIsExact()
    {
        var review = new DuplicateReview([G(F(@"C:\a"), F(@"C:\b"))]);

        var clean = review.ToCleanup()[0];

        Assert.Same(review.Groups[0].Keep, clean.Keep);
        Assert.True(clean.Exact);
    }

    [Fact]
    public void Resolve_DropsActedOnFiles_AndAGroupLeftWithOneCopy()
    {
        var review = new DuplicateReview([G(F(@"C:\a", day: 1), F(@"C:\b", day: 2), F(@"C:\c", day: 3)), G(F(@"D:\x"), F(@"D:\y"))]);

        review.Resolve([@"c:\B", @"D:\y"]);

        Assert.Single(review.Groups);
        Assert.Equal([@"C:\a", @"C:\c"], review.Groups[0].Files.Select(f => f.Path));
    }

    [Fact]
    public void GroupsFrom_PutsFilesWithTheSameKeyTogether_BiggestWasteFirst_AndReadsTheSizeFromTheKey()
    {
        var items = new[]
        {
            new ItemOutcome(@"C:\small1", "10:AA", ItemStatus.Done),
            new ItemOutcome(@"C:\big1", "5000:BB", ItemStatus.Done),
            new ItemOutcome(@"C:\small2", "10:AA", ItemStatus.Done),
            new ItemOutcome(@"C:\big2", "5000:BB", ItemStatus.Done),
            new ItemOutcome(@"C:\lonely", "7:CC", ItemStatus.Done),
            new ItemOutcome(@"C:\skipped", null, ItemStatus.Skipped, "not searched"),
        };

        var groups = DuplicateReview.GroupsFrom(items, _ => DateTime.UnixEpoch);

        Assert.Equal(["5000:BB", "10:AA"], groups.Select(g => g.Key));
        Assert.All(groups[0].Files, f => Assert.Equal(5000, f.Size));
        Assert.All(groups, g => Assert.True(g.Exact));
    }
}
