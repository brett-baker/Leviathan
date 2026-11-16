using Leviathan.Core.Paths;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Tests;

/// <summary>The tab drag's one decision, in geometry alone (spec §4.1). Each tab is 100 wide, each chip 40; the strip is 1000 wide and 40 tall.</summary>
public sealed class TabDragPlannerTests
{
    private const double StripWidth = 1000;
    private const double StripHeight = 40;

    private static TabStripViewModel Strip(params string[] folders)
    {
        var strip = new TabStripViewModel(TabTestSupport.Services());
        foreach (var folder in folders)
        {
            strip.NewTab(LocationPath.Parse(folder));
        }

        return strip;
    }

    private static List<DragSlot> Slots(TabStripViewModel strip, params TabViewModel[] hidden)
    {
        var slots = new List<DragSlot>();
        var seen = new HashSet<TabGroup>();
        var left = 0.0;
        foreach (var tab in strip.Tabs)
        {
            if (strip.GroupOf(tab) is { } group && seen.Add(group))
            {
                slots.Add(new DragSlot(new ChipEntry(group), left, 40));
                left += 40;
            }

            var isHidden = hidden.Contains(tab);
            slots.Add(new DragSlot(new TabEntry(tab), left, isHidden ? 0 : 100, !isHidden));
            left += isHidden ? 0 : 100;
        }

        return slots;
    }

    private static DragPlan PlanAt(TabStripViewModel strip, List<DragSlot> slots, StripEntry dragged, double centre, double y = 20) =>
        TabDragPlanner.Plan(strip, slots, dragged, centre, centre, y, StripWidth, StripHeight);

    private static string Names(IEnumerable<StripEntry> order) => string.Join(",", order.Select(e => e switch
    {
        TabEntry t => t.Tab.Title,
        ChipEntry c => "[" + c.Group.Name + "]",
        _ => "?",
    }));

    [Fact]
    public void ATabLetGoPastTwoOthers_TakesThePlaceAfterThem_AndTheTwoSlideAside()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c", @"C:\d");
        var slots = Slots(strip);
        var first = new TabEntry(strip.Tabs[0]);

        var plan = PlanAt(strip, slots, first, centre: 260);

        Assert.Equal(DragOutcome.Reorder, plan.Outcome);
        Assert.Equal("b,c,a,d", Names(plan.Order));
        Assert.Equal(-100, plan.Shifts[new TabEntry(strip.Tabs[1])]);
        Assert.Equal(-100, plan.Shifts[new TabEntry(strip.Tabs[2])]);
        Assert.Equal(2, plan.Shifts.Count);
        var change = StripReorder.Decide(strip, plan.Order, first);
        Assert.Equal(StripChangeKind.MoveTab, change.Kind);
        Assert.Equal(2, change.Index);
    }

    [Fact]
    public void ATabLetGoFarRight_GoesToTheEnd()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        var plan = PlanAt(strip, Slots(strip), new TabEntry(strip.Tabs[0]), centre: 900);

        Assert.Equal("b,c,a", Names(plan.Order));
    }

    [Fact]
    public void ATabLetGoFarLeft_GoesToTheStart_AndTheOthersSlideRight()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        var plan = PlanAt(strip, Slots(strip), new TabEntry(strip.Tabs[2]), centre: 5);

        Assert.Equal("c,a,b", Names(plan.Order));
        Assert.Equal(100, plan.Shifts[new TabEntry(strip.Tabs[0])]);
        Assert.Equal(100, plan.Shifts[new TabEntry(strip.Tabs[1])]);
    }

    [Fact]
    public void ATabLetGoWhereItStarted_ChangesNothing()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        var plan = PlanAt(strip, Slots(strip), new TabEntry(strip.Tabs[1]), centre: 150);

        Assert.Equal("a,b,c", Names(plan.Order));
        Assert.Empty(plan.Shifts);
        Assert.Equal(StripChangeKind.None, StripReorder.Decide(strip, plan.Order, new TabEntry(strip.Tabs[1])).Kind);
    }

    [Fact]
    public void ADrift_BelowTheRowButWithinTheSlack_IsStillAReorder()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");

        var plan = PlanAt(strip, Slots(strip), new TabEntry(strip.Tabs[0]), centre: 250, y: StripHeight + 60);

        Assert.Equal(DragOutcome.Reorder, plan.Outcome);
        Assert.Equal("b,c,a", Names(plan.Order));
    }

    [Theory]
    [InlineData(500, 40 + 91)]
    [InlineData(500, -46)]
    [InlineData(-1, 20)]
    [InlineData(1001, 20)]
    public void ADragClearlyAwayFromTheStrip_TearsOff_AndMovesNothing(double x, double y)
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");

        var plan = TabDragPlanner.Plan(strip, Slots(strip), new TabEntry(strip.Tabs[0]), x, x, y, StripWidth, StripHeight);

        Assert.Equal(DragOutcome.TearOff, plan.Outcome);
        Assert.Equal("a,b,c", Names(plan.Order));
        Assert.Empty(plan.Shifts);
    }

    [Fact]
    public void APinnedTab_CannotBePickedUp_AndNothingMoves()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        strip.SetPinned(strip.Tabs[0], true);
        var pinned = new TabEntry(strip.Tabs[0]);

        Assert.False(TabDragPlanner.CanDrag(pinned));
        var plan = PlanAt(strip, Slots(strip), pinned, centre: 260);

        Assert.Equal("a,b,c", Names(plan.Order));
        Assert.Empty(plan.Shifts);
    }

    [Fact]
    public void AnUnpinnedTab_ReachingOverPinnedOnes_StaysAfterThem_WhenTheMoveIsCarriedOut()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c", @"C:\d");
        strip.SetPinned(strip.Tabs[0], true);
        strip.SetPinned(strip.Tabs[1], true);
        var last = strip.Tabs[3];

        var plan = PlanAt(strip, Slots(strip), new TabEntry(last), centre: 5);
        var change = StripReorder.Decide(strip, plan.Order, new TabEntry(last));
        strip.Move(change.Tab!, change.Index);

        Assert.Equal(["a", "b", "d", "c"], strip.Tabs.Select(t => t.Title));
    }

    [Fact]
    public void ATabInAFoldedGroup_IsNotATarget_AndKeepsItsPlaceInTheOrder()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c", @"C:\d");
        var slots = Slots(strip, hidden: strip.Tabs[1]);

        var plan = PlanAt(strip, slots, new TabEntry(strip.Tabs[0]), centre: 120);

        // b is hidden: a passes c's middle (150 is not reached at 120), so a goes before c, which leaves it next to b.
        Assert.Equal("b,a,c,d", Names(plan.Order));
        Assert.Empty(plan.Shifts);
    }

    [Fact]
    public void ATabLetGoBetweenTwoTabsOfAGroup_JoinsTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c", @"C:\d");
        var group = strip.CreateGroup([strip.Tabs[1]])!;
        strip.AddToGroup(strip.Tabs[2], group);
        var plan = PlanAt(strip, Slots(strip), new TabEntry(strip.Tabs[3]), centre: 250);

        // Slots: a 0-100, chip 100-140, b 140-240, c 240-340, d 340-440. Centre 250 is between the middles of b (190) and c (290).
        Assert.Equal("a,[],b,d,c", Names(plan.Order).Replace("[" + group.Name + "]", "[]"));
        var change = StripReorder.Decide(strip, plan.Order, new TabEntry(strip.Tabs[3]));
        Assert.Equal(StripChangeKind.AddToGroup, change.Kind);
    }

    [Fact]
    public void ATabOfAGroup_LetGoOutsideIt_LeavesTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c");
        var group = strip.CreateGroup([strip.Tabs[1]]);
        var dragged = new TabEntry(strip.Tabs[1]);

        var plan = PlanAt(strip, Slots(strip), dragged, centre: 900);

        var change = StripReorder.Decide(strip, plan.Order, dragged);
        Assert.Equal(StripChangeKind.LeaveGroupAndMove, change.Kind);
        Assert.NotNull(group);
    }

    [Fact]
    public void AChip_LetGoAfterTheTabsOfTheNextTab_MovesTheWholeGroup_AndItsTabsAreNotShifted()
    {
        var strip = Strip(@"C:\a", @"C:\b", @"C:\c", @"C:\d");
        var group = strip.CreateGroup([strip.Tabs[0]])!;
        strip.AddToGroup(strip.Tabs[1], group);
        var chip = new ChipEntry(group);

        // Slots: chip 0-40, a 40-140, b 140-240, c 240-340, d 340-440. The chip's middle goes past c's middle (290).
        var plan = PlanAt(strip, Slots(strip), chip, centre: 300);

        var change = StripReorder.Decide(strip, plan.Order, chip);
        Assert.Equal(StripChangeKind.MoveGroup, change.Kind);
        Assert.Equal(1, change.Index);
        Assert.DoesNotContain(new TabEntry(strip.Tabs[0]), plan.Shifts.Keys);
        Assert.DoesNotContain(new TabEntry(strip.Tabs[1]), plan.Shifts.Keys);
        Assert.Equal(-(40 + 100 + 100), plan.Shifts[new TabEntry(strip.Tabs[2])]);
    }
}
