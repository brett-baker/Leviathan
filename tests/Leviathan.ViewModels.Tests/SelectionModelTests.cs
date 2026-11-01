using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class SelectionModelTests
{
    private static readonly LocationPath Root = LocationPath.Parse(@"C:\x");

    private static FileEntry File(string name, long size = 10) =>
        new(name, Root.Combine(name), FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, Root.Combine(name), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    private static (FileEntry[] Rows, SnapshotList<FileEntry> List, SelectionModel Model) Make(params string[] names)
    {
        var rows = names.Select(n => File(n)).ToArray();
        var list = new SnapshotList<FileEntry>();
        list.Replace(rows);
        return (rows, list, new SelectionModel(list));
    }

    private static string[] Selected(SelectionModel model) => [.. model.SelectedEntries.Select(e => e.Name)];

    [Fact]
    public void Click_SelectsOnlyThatRow_AndSetsFocus()
    {
        var (rows, _, model) = Make("a", "b", "c");

        model.Click(rows[0]);
        model.Click(rows[2]);

        Assert.Equal(["c"], Selected(model));
        Assert.Equal(rows[2].Location, model.Focus);
    }

    [Fact]
    public void CtrlClick_TogglesWithoutDroppingOthers()
    {
        var (rows, _, model) = Make("a", "b", "c");
        model.Click(rows[0]);

        model.Click(rows[2], ctrl: true);
        model.Click(rows[0], ctrl: true);

        Assert.Equal(["c"], Selected(model));
    }

    [Fact]
    public void ShiftClick_SelectsTheRangeFromTheAnchor_InEitherDirection()
    {
        var (rows, _, model) = Make("a", "b", "c", "d", "e");
        model.Click(rows[1]);

        model.Click(rows[3], shift: true);
        Assert.Equal(["b", "c", "d"], Selected(model));

        model.Click(rows[0], shift: true);
        Assert.Equal(["a", "b"], Selected(model));
    }

    [Fact]
    public void CtrlShiftClick_AddsTheRangeToTheExistingSelection()
    {
        var (rows, _, model) = Make("a", "b", "c", "d", "e");
        model.Click(rows[0]);
        model.Click(rows[2], ctrl: true);

        model.Click(rows[4], ctrl: true, shift: true);

        Assert.Equal(["a", "c", "d", "e"], Selected(model));
    }

    [Fact]
    public void ShiftClick_WithNoAnchor_SelectsJustThatRow()
    {
        var (rows, _, model) = Make("a", "b", "c");

        model.Click(rows[2], shift: true);

        Assert.Equal(["c"], Selected(model));
    }

    [Fact]
    public void SelectAll_Clear_Invert()
    {
        var (rows, _, model) = Make("a", "b", "c");

        model.SelectAll();
        Assert.Equal(3, model.Count);

        model.Click(rows[1], ctrl: true);
        model.Invert();
        Assert.Equal(["b"], Selected(model));

        model.Clear();
        Assert.Equal(0, model.Count);
    }

    [Fact]
    public void SelectByPattern_MatchesWildcards_ReplacesOrAdds()
    {
        var (rows, _, model) = Make("a.mkv", "b.txt", "c.MKV", "d.avi");
        model.Click(rows[1]);

        var matched = model.SelectByPattern("*.mkv");
        Assert.Equal(2, matched);
        Assert.Equal(["a.mkv", "c.MKV"], Selected(model));

        model.SelectByPattern("d.*", add: true);
        Assert.Equal(["a.mkv", "c.MKV", "d.avi"], Selected(model));
    }

    [Fact]
    public void Toggle_IsSpaceInKeyboardMode_AndSetsTheAnchor()
    {
        var (rows, _, model) = Make("a", "b", "c", "d");

        model.Toggle(rows[1]);
        model.Click(rows[3], shift: true);

        Assert.Equal(["b", "c", "d"], Selected(model));
    }

    [Fact]
    public void Move_Arrows_SelectNeighbourAndClampAtTheEnds()
    {
        var (_, _, model) = Make("a", "b", "c");

        model.Move(1);
        Assert.Equal(["a"], Selected(model));
        model.Move(1);
        model.Move(1);
        model.Move(1);
        Assert.Equal(["c"], Selected(model));
        model.Move(-1);
        Assert.Equal(["b"], Selected(model));
    }

    [Fact]
    public void Move_FirstPressWithNoFocus_StartsAtTheEdge()
    {
        var (_, _, down) = Make("a", "b", "c");
        var (_, _, up) = Make("a", "b", "c");

        down.Move(1);
        up.Move(-1);

        Assert.Equal(["a"], Selected(down));
        Assert.Equal(["c"], Selected(up));
    }

    [Fact]
    public void Move_HomeAndEnd_UseExtremeDeltas()
    {
        var (_, _, model) = Make("a", "b", "c", "d");
        model.Move(1);

        model.Move(int.MaxValue);
        Assert.Equal(["d"], Selected(model));
        model.Move(int.MinValue);
        Assert.Equal(["a"], Selected(model));
    }

    [Fact]
    public void Move_WithShift_ExtendsFromTheAnchor_AndShrinksBack()
    {
        var (rows, _, model) = Make("a", "b", "c", "d");
        model.Click(rows[1]);

        model.Move(2, shift: true);
        Assert.Equal(["b", "c", "d"], Selected(model));

        model.Move(-1, shift: true);
        Assert.Equal(["b", "c"], Selected(model));
    }

    [Fact]
    public void Move_WithCtrl_MovesFocusOnly_ThenSpaceToggles()
    {
        var (rows, _, model) = Make("a", "b", "c");
        model.Click(rows[0]);

        model.Move(2, ctrl: true);
        Assert.Equal(["a"], Selected(model));
        Assert.Equal(rows[2].Location, model.Focus);

        model.Toggle(rows[2]);
        Assert.Equal(["a", "c"], Selected(model));
    }

    [Fact]
    public void Move_EmptyView_DoesNothing()
    {
        var (_, _, model) = Make();

        model.Move(1);

        Assert.Null(model.Focus);
    }

    [Fact]
    public void Selection_SurvivesReSortAndNewRowsArriving()
    {
        var (rows, list, model) = Make("a", "b", "c");
        model.Click(rows[1]);

        list.Replace([rows[2], rows[1], rows[0], File("d")]);

        Assert.Equal(["b"], Selected(model));
        model.Click(list[3], shift: true);
        Assert.Equal(["b", "a", "d"], Selected(model));
    }

    [Fact]
    public void Selection_DropsItemsFilteredOutOfTheView()
    {
        var (rows, list, model) = Make("a", "b", "c");
        model.SelectAll();

        list.Replace([rows[0]]);

        Assert.Equal(["a"], Selected(model));
        Assert.Equal(1, model.Count);
    }

    [Fact]
    public void Focus_OnAnItemThatVanished_IsCleared()
    {
        var (rows, list, model) = Make("a", "b");
        model.Click(rows[1]);

        list.Replace([rows[0]]);
        _ = model.Count;

        Assert.Null(model.Focus);
    }

    [Fact]
    public void SelectedBytes_SumsFilesOnly()
    {
        var list = new SnapshotList<FileEntry>();
        FileEntry[] rows = [File("a", 100), File("b", 250), Folder("dir")];
        list.Replace(rows);
        var model = new SelectionModel(list);

        model.SelectAll();

        Assert.Equal(350, model.SelectedBytes);
        Assert.Equal(3, model.Count);
    }

    [Fact]
    public void Changed_IsRaisedForEveryChange()
    {
        var (rows, _, model) = Make("a", "b");
        var raised = 0;
        model.Changed += (_, _) => raised++;

        model.Click(rows[0]);
        model.Toggle(rows[1]);
        model.SelectAll();
        model.Invert();
        model.Clear();

        Assert.Equal(5, raised);
    }

    [Fact]
    public void TypeAhead_JumpsToTheFirstMatch_AndExtendsWhileTypingQuickly()
    {
        var time = new ManualTimeProvider();
        var list = new SnapshotList<FileEntry>();
        list.Replace([File("alpha"), File("beta"), File("bravo"), File("charlie")]);
        var model = new SelectionModel(list, time);

        Assert.Equal("beta", model.TypeAhead('b')?.Name);
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("bravo", model.TypeAhead('r')?.Name);
        Assert.Equal(["bravo"], Selected(model));
    }

    [Fact]
    public void TypeAhead_AfterAPause_StartsANewWord()
    {
        var time = new ManualTimeProvider();
        var list = new SnapshotList<FileEntry>();
        list.Replace([File("alpha"), File("beta"), File("charlie")]);
        var model = new SelectionModel(list, time);
        model.TypeAhead('b');

        time.Advance(SelectionModel.TypeAheadTimeout + TimeSpan.FromMilliseconds(1));

        Assert.Equal("charlie", model.TypeAhead('c')?.Name);
    }

    [Fact]
    public void TypeAhead_RepeatingTheSameLetter_CyclesThroughMatches()
    {
        var time = new ManualTimeProvider();
        var list = new SnapshotList<FileEntry>();
        list.Replace([File("apple"), File("avocado"), File("banana")]);
        var model = new SelectionModel(list, time);

        Assert.Equal("apple", model.TypeAhead('a')?.Name);
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal("avocado", model.TypeAhead('a')?.Name);
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal("apple", model.TypeAhead('a')?.Name);
    }

    [Fact]
    public void TypeAhead_NoMatch_ReturnsNull_AndLeavesSelection()
    {
        var (rows, _, model) = Make("alpha", "beta");
        model.Click(rows[0]);

        Assert.Null(model.TypeAhead('z'));
        Assert.Equal(["alpha"], Selected(model));
    }

    [Fact]
    public void RubberBand_SelectsIntersectingRows_AndAdditiveKeepsEarlierSelection()
    {
        var (rows, _, model) = Make("a", "b", "c", "d");
        SelectionRect[] rects = [new(0, 0, 100, 20), new(0, 20, 100, 20), new(0, 40, 100, 20), new(0, 60, 100, 20)];
        model.Click(rows[0]);
        var before = model.Snapshot();

        model.ApplyRubberBand(rects, SelectionRect.FromCorners(50, 70, 10, 30), before);

        Assert.Equal(["a", "b", "c", "d"], Selected(model));

        model.ApplyRubberBand(rects, SelectionRect.FromCorners(5, 25, 50, 35));
        Assert.Equal(["b"], Selected(model));
    }

    [Fact]
    public void SelectionRect_TouchingEdges_DoNotIntersect()
    {
        var a = new SelectionRect(0, 0, 10, 10);

        Assert.False(a.Intersects(new SelectionRect(10, 0, 10, 10)));
        Assert.True(a.Intersects(new SelectionRect(9.9, 9.9, 5, 5)));
    }

    [Fact]
    public void LargeView_RepeatedBatches_DoNotRebuildTheIndexUntilNeeded()
    {
        var list = new SnapshotList<FileEntry>();
        var model = new SelectionModel(list);
        var rows = Enumerable.Range(0, 50_000).Select(i => File("f" + i)).ToList();

        for (var i = 0; i < 200; i++)
        {
            list.Replace(rows);
        }

        model.Click(rows[49_999]);
        Assert.Equal(["f49999"], Selected(model));
    }
}
