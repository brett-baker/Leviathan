using Leviathan.Core.Paths;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Tests;

public sealed class StripReorderTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static TabStripViewModel Strip(params string[] folders)
    {
        var strip = new TabStripViewModel(TabTestSupport.Services());
        foreach (var folder in folders)
        {
            strip.NewTab(P(folder));
        }

        return strip;
    }

    /// <summary>The strip as the window shows it: each group's chip in front of its first tab.</summary>
    private static List<StripEntry> Shown(TabStripViewModel strip)
    {
        var entries = new List<StripEntry>();
        var seen = new HashSet<TabGroup>();
        foreach (var tab in strip.Tabs)
        {
            if (strip.GroupOf(tab) is { } group && seen.Add(group))
            {
                entries.Add(new ChipEntry(group));
            }

            entries.Add(new TabEntry(tab));
        }

        return entries;
    }

    /// <summary>Drags an item to another place in the list, as the strip does, and returns what was dragged.</summary>
    private static StripEntry Drag(List<StripEntry> shown, int from, int to)
    {
        var entry = shown[from];
        shown.RemoveAt(from);
        shown.Insert(to, entry);
        return entry;
    }

    [Fact]
    public void NothingMoved_IsNothing()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        strip.CreateGroup([strip.Tabs[0]]);

        Assert.Equal(StripChangeKind.None, StripReorder.Decide(strip, Shown(strip)).Kind);
    }

    [Fact]
    public void AMismatchInTheNumberOfItems_IsLeftToTheModel()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var shown = Shown(strip);
        shown.RemoveAt(1);

        Assert.Equal(StripChangeKind.None, StripReorder.Decide(strip, shown).Kind);
    }

    [Fact]
    public void ATabDragged_BetweenUngroupedTabs_IsAPlainMove()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var shown = Shown(strip);
        var dragged = Drag(shown, 0, 2);

        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(new StripChange(StripChangeKind.MoveTab, strip.Tabs[0], null, 2), change);
    }

    [Fact]
    public void ATabDroppedBetweenTwoTabsOfAGroup_JoinsItAtThatPlace()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]])!;
        var outsider = strip.Tabs[3];
        var shown = Shown(strip);

        // [a, chip, v, d1, d2] -> [a, chip, v, d2, d1]: d2 between v and d1.
        var dragged = Drag(shown, 4, 3);
        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(StripChangeKind.AddToGroup, change.Kind);
        Assert.Equal((outsider, group, 1), (change.Tab, change.Group, change.Index));
    }

    [Fact]
    public void ATabDroppedRightAfterTheChip_JoinsAtTheStartOfTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]])!;
        var outsider = strip.Tabs[0];
        var shown = Shown(strip);

        // [a, chip, v, d1] -> [chip, a, v, d1]
        var dragged = Drag(shown, 0, 1);
        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(StripChangeKind.AddToGroup, change.Kind);
        Assert.Equal((outsider, group, 0), (change.Tab, change.Group, change.Index));
    }

    [Fact]
    public void ATabDroppedJustAfterTheLastTabOfAGroup_StaysOutside()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        strip.CreateGroup([strip.Tabs[0], strip.Tabs[1]]);
        var shown = Shown(strip);

        // [chip, a, v, d1, d2] -> [chip, a, v, d2, d1]: d2 next to the group's last tab, not between two of its tabs.
        var dragged = Drag(shown, 4, 3);
        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(StripChangeKind.MoveTab, change.Kind);
        Assert.Equal(strip.Tabs[3], change.Tab);
    }

    [Fact]
    public void ATabDraggedOutOfItsGroup_LeavesIt()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        strip.CreateGroup([strip.Tabs[0], strip.Tabs[1]]);
        var member = strip.Tabs[0];
        var shown = Shown(strip);

        // [chip, a, v, d1, d2] -> [chip, v, d1, a, d2]
        var dragged = Drag(shown, 1, 3);
        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(new StripChange(StripChangeKind.LeaveGroupAndMove, member, null, 2), change);
    }

    [Fact]
    public void AGroupedTabMovedWithinItsGroup_StaysAMove()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        strip.CreateGroup(strip.Tabs.ToList());
        var first = strip.Tabs[0];
        var shown = Shown(strip);

        // [chip, a, v, d1] -> [chip, v, a, d1]
        var dragged = Drag(shown, 1, 2);
        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(new StripChange(StripChangeKind.MoveTab, first, null, 1), change);
    }

    [Fact]
    public void ATabDraggedAcrossTheChipOnly_IsAnAddToTheGroup_NotAMoveOfTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]]);
        var shown = Shown(strip);

        // The tab order is the same afterwards; only the chip and a swapped places. What was dragged decides.
        var dragged = Drag(shown, 0, 1);

        Assert.Equal(StripChangeKind.AddToGroup, StripReorder.Decide(strip, shown, dragged).Kind);
        var chipDragged = Shown(strip);
        var chip = Drag(chipDragged, 1, 0);
        Assert.Equal(StripChangeKind.MoveGroup, StripReorder.Decide(strip, chipDragged, chip).Kind);
    }

    [Fact]
    public void ATabDraggedFromOneGroupIntoAnother_JoinsTheOther()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2", @"C:\d3");
        var one = strip.CreateGroup([strip.Tabs[0], strip.Tabs[1]])!;
        var two = strip.CreateGroup([strip.Tabs[3], strip.Tabs[4]])!;
        var traveller = strip.Tabs[0];
        var shown = Shown(strip);

        // [chip1, a, v, d1, chip2, d2, d3] -> a between d2 and d3
        var dragged = Drag(shown, 1, 5);
        var change = StripReorder.Decide(strip, shown, dragged);

        Assert.Equal(StripChangeKind.AddToGroup, change.Kind);
        Assert.Equal((traveller, two, 1), (change.Tab, change.Group, change.Index));
        Assert.NotEqual(one, change.Group);
    }

    [Fact]
    public void ADraggedChip_MovesItsGroupToWhereTheChipNowIs()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var group = strip.CreateGroup([strip.Tabs[0], strip.Tabs[1]])!;
        var shown = Shown(strip);

        // [chip, a, v, d1, d2] -> only the chip moves, to between d1 and d2: [a, v, d1, chip, d2]
        var chip = Drag(shown, 0, 3);
        var change = StripReorder.Decide(strip, shown, chip);

        Assert.Equal(StripChangeKind.MoveGroup, change.Kind);
        Assert.Equal((group, 1), (change.Group, change.Index));
    }

    [Fact]
    public void AChipDraggedBackToWhereItWas_IsNothing()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        strip.CreateGroup([strip.Tabs[0]]);
        var shown = Shown(strip);
        var chip = shown[0];

        Assert.Equal(StripChangeKind.None, StripReorder.Decide(strip, shown, chip).Kind);
    }

    [Fact]
    public void ADecisionCanBeAppliedToTheModel_AndTheStripKeepsItsRules()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]])!;
        var outsider = strip.Tabs[3];
        var shown = Shown(strip);
        var dragged = Drag(shown, 4, 3);

        var change = StripReorder.Decide(strip, shown, dragged);
        strip.AddToGroup(change.Tab!, change.Group!, change.Index);

        Assert.Equal(group.Id, outsider.GroupId);
        Assert.Equal([P(@"C:\v"), P(@"C:\d2"), P(@"C:\d1")], strip.TabsOf(group).Select(t => t.History.Current));
    }
}
