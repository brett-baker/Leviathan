using Leviathan.Core.Compare;

namespace Leviathan.Core.Tests;

/// <summary>What a sync would do in each mode (spec §15.5).</summary>
public sealed class SyncPlannerTests
{
    private static readonly DateTimeOffset Old = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = Old.AddDays(1);

    private static CompareEntry File(string path, CompareStatus status, long left = 10, long right = 10) => status switch
    {
        CompareStatus.LeftOnly => new(path, false, new CompareSide(left, Old), null, status),
        CompareStatus.RightOnly => new(path, false, null, new CompareSide(right, Old), status),
        CompareStatus.NewerLeft => new(path, false, new CompareSide(left, New), new CompareSide(right, Old), status),
        CompareStatus.NewerRight => new(path, false, new CompareSide(left, Old), new CompareSide(right, New), status),
        CompareStatus.Different => new(path, false, new CompareSide(left, Old), new CompareSide(right, Old), status),
        _ => new(path, false, new CompareSide(left, Old), new CompareSide(right, Old), CompareStatus.Identical),
    };

    private static CompareEntry Folder(string path, CompareStatus status) => status switch
    {
        CompareStatus.LeftOnly => new(path, true, new CompareSide(0, Old), null, status),
        CompareStatus.RightOnly => new(path, true, null, new CompareSide(0, Old), status),
        _ => new(path, true, new CompareSide(0, Old), new CompareSide(0, Old), CompareStatus.Identical),
    };

    private static SyncPlan Plan(SyncMode mode, IReadOnlyDictionary<string, SyncChoice>? choices = null, params CompareEntry[] entries) =>
        SyncPlanner.Plan(new CompareResult(entries, []), mode, choices);

    private static string[] Steps(SyncPlan plan) => [.. plan.Actions.Select(a => $"{a.Kind} {a.Path}")];

    private static readonly CompareEntry[] All =
    [
        File("same.txt", CompareStatus.Identical),
        File("l.txt", CompareStatus.LeftOnly),
        File("r.txt", CompareStatus.RightOnly),
        File("nl.txt", CompareStatus.NewerLeft),
        File("nr.txt", CompareStatus.NewerRight),
        File("d.txt", CompareStatus.Different),
    ];

    [Fact]
    public void MirrorLeftToRight_CopiesNewAndChanged_AndDeletesWhatOnlyTheRightHas()
    {
        var plan = Plan(SyncMode.MirrorLeftToRight, null, All);

        Assert.Equal(["DeleteRight r.txt", "CopyToRight d.txt", "CopyToRight l.txt", "CopyToRight nl.txt", "CopyToRight nr.txt"], Steps(plan));
        Assert.Equal(1, plan.Deletions);
        Assert.Equal(1, plan.NewerOverwritten);
        Assert.True(plan.Actions.Single(a => a.Path == "nr.txt").OverwritesNewer);
        Assert.Contains("makes it match", plan.Actions.Single(a => a.Path == "nr.txt").Reason);
    }

    [Fact]
    public void MirrorRightToLeft_IsTheMirrorImage()
    {
        var plan = Plan(SyncMode.MirrorRightToLeft, null, All);

        Assert.Equal(["DeleteLeft l.txt", "CopyToLeft d.txt", "CopyToLeft nl.txt", "CopyToLeft nr.txt", "CopyToLeft r.txt"], Steps(plan));
    }

    [Fact]
    public void TwoWay_CopiesEachWayTheNewerSide_FlagsWhatItCannotSettle_AndNeverDeletes()
    {
        var plan = Plan(SyncMode.TwoWay, null, All);

        Assert.Equal(["CopyToRight l.txt", "CopyToRight nl.txt", "CopyToLeft nr.txt", "CopyToLeft r.txt"], Steps(plan));
        Assert.Equal(["d.txt"], plan.Conflicts.Select(c => c.Path));
        Assert.Equal(0, plan.Deletions);
        Assert.Equal(0, plan.NewerOverwritten);
        Assert.Contains("never deletes", plan.Notes[0]);
    }

    [Fact]
    public void Update_CopiesOnlyNewAndNewer_NeverDeletes_NeverOverwritesNewer()
    {
        var right = Plan(SyncMode.UpdateLeftToRight, null, All);
        var left = Plan(SyncMode.UpdateRightToLeft, null, All);

        Assert.Equal(["CopyToRight l.txt", "CopyToRight nl.txt"], Steps(right));
        Assert.Equal(["CopyToLeft nr.txt", "CopyToLeft r.txt"], Steps(left));
        Assert.Equal(0, right.Deletions + left.Deletions);
        Assert.Equal(0, right.NewerOverwritten + left.NewerOverwritten);
    }

    [Fact]
    public void Identical_Items_NeverProduceAStep()
    {
        foreach (var mode in Enum.GetValues<SyncMode>())
        {
            Assert.True(Plan(mode, null, File("a", CompareStatus.Identical), Folder("f", CompareStatus.Identical)).IsEmpty);
        }
    }

    [Fact]
    public void ACopiedFolder_TakesItsContents_SoTheyAreNotListedAgain_AndItsSizeIsTheirSum()
    {
        var plan = Plan(SyncMode.UpdateLeftToRight, null,
            Folder("new", CompareStatus.LeftOnly),
            File(@"new\a.txt", CompareStatus.LeftOnly, left: 100),
            File(@"new\deep\b.txt", CompareStatus.LeftOnly, left: 50),
            Folder(@"new\deep", CompareStatus.LeftOnly));

        var step = Assert.Single(plan.Actions);
        Assert.Equal("new", step.Path);
        Assert.True(step.IsDirectory);
        Assert.Equal(150, step.Size);
        Assert.Equal(150, plan.BytesToCopy);
    }

    [Fact]
    public void ADeletedFolder_IsOneStep_AndDeepestDeletionsComeFirst()
    {
        var plan = Plan(SyncMode.MirrorLeftToRight, null,
            Folder("gone", CompareStatus.RightOnly),
            File(@"gone\x.txt", CompareStatus.RightOnly),
            File("lone.txt", CompareStatus.RightOnly));

        Assert.Equal(["DeleteRight gone", "DeleteRight lone.txt"], Steps(plan));
    }

    [Fact]
    public void DeletionsRunBeforeCopies()
    {
        var plan = Plan(SyncMode.MirrorLeftToRight, null, File("a.txt", CompareStatus.LeftOnly), File("b.txt", CompareStatus.RightOnly));

        Assert.Equal(["DeleteRight b.txt", "CopyToRight a.txt"], Steps(plan));
    }

    [Fact]
    public void AFileAgainstAFolder_IsDeletedThenReplacedInAMirror()
    {
        // Left has a file "x", right has a folder "x" holding y.txt.
        var mismatch = new CompareEntry("x", false, new CompareSide(5, Old), new CompareSide(0, Old), CompareStatus.TypeMismatch);
        var inside = File(@"x\y.txt", CompareStatus.RightOnly);

        var toRight = Plan(SyncMode.MirrorLeftToRight, null, mismatch, inside);
        var toLeft = Plan(SyncMode.MirrorRightToLeft, null, mismatch, inside);

        Assert.Equal(["DeleteRight x", "CopyToRight x"], Steps(toRight));
        Assert.True(toRight.Actions[0].IsDirectory);
        Assert.False(toRight.Actions[1].IsDirectory);
        Assert.Equal(["DeleteLeft x", "CopyToLeft x"], Steps(toLeft).Where(s => !s.EndsWith("y.txt", StringComparison.Ordinal)).ToArray());
        Assert.False(toLeft.Actions[0].IsDirectory);
        Assert.True(toLeft.Actions.Single(a => a.Kind == SyncActionKind.CopyToLeft).IsDirectory);
    }

    [Fact]
    public void TwoWay_AFileAgainstAFolder_IsAConflict()
    {
        var mismatch = new CompareEntry("x", false, new CompareSide(5, Old), new CompareSide(0, Old), CompareStatus.TypeMismatch);

        var plan = Plan(SyncMode.TwoWay, null, mismatch);

        Assert.Equal("x", Assert.Single(plan.Conflicts).Path);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ASkipChoice_LeavesTheItemAlone_InEveryMode()
    {
        var choices = new Dictionary<string, SyncChoice> { ["L.TXT"] = SyncChoice.Skip, ["r.txt"] = SyncChoice.Skip };

        var plan = Plan(SyncMode.MirrorLeftToRight, choices, File("l.txt", CompareStatus.LeftOnly), File("r.txt", CompareStatus.RightOnly), File("o.txt", CompareStatus.LeftOnly));

        Assert.Equal(["CopyToRight o.txt"], Steps(plan));
    }

    [Fact]
    public void AChoiceToPickASide_SettlesAConflict_AndOverridesTheMode()
    {
        var conflict = File("d.txt", CompareStatus.Different);
        var newer = File("nl.txt", CompareStatus.NewerLeft);
        var choices = new Dictionary<string, SyncChoice> { ["d.txt"] = SyncChoice.RightToLeft, ["nl.txt"] = SyncChoice.RightToLeft };

        var plan = Plan(SyncMode.TwoWay, choices, conflict, newer);

        Assert.Equal(["CopyToLeft d.txt", "CopyToLeft nl.txt"], Steps(plan));
        Assert.Empty(plan.Conflicts);
        Assert.True(plan.Actions.Single(a => a.Path == "nl.txt").OverwritesNewer);
    }

    [Fact]
    public void AChoiceForAnItemOnlyTheOtherSideHas_MeansDeleteIt()
    {
        var plan = Plan(SyncMode.UpdateLeftToRight, new Dictionary<string, SyncChoice> { ["l.txt"] = SyncChoice.RightToLeft }, File("l.txt", CompareStatus.LeftOnly));

        Assert.Equal(["DeleteLeft l.txt"], Steps(plan));
    }

    [Fact]
    public void Bytes_AreTheSumOfTheCopies_NotTheDeletions()
    {
        var plan = Plan(SyncMode.MirrorLeftToRight, null, File("a", CompareStatus.LeftOnly, left: 7), File("b", CompareStatus.NewerLeft, left: 3), File("c", CompareStatus.RightOnly, right: 99));

        Assert.Equal(10, plan.BytesToCopy);
        Assert.Single(plan.Of(SyncActionKind.DeleteRight));
        Assert.Equal(2, plan.Of(SyncActionKind.CopyToRight).Count());
    }
}
