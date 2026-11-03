using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

public sealed class TabGroupTests
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

    private static string Names(TabStripViewModel strip) => string.Join(" ", strip.Tabs.Select(t => t.Title));

    /// <summary>The rule every operation must keep: each group's tabs are side by side, and no pinned tab is in a group.</summary>
    private static void AssertInvariants(TabStripViewModel strip)
    {
        foreach (var group in strip.Groups)
        {
            var indexes = strip.Tabs.Select((t, i) => (t, i)).Where(x => x.t.GroupId == group.Id).Select(x => x.i).ToList();
            Assert.NotEmpty(indexes);
            Assert.Equal(indexes.Count, indexes[^1] - indexes[0] + 1);
        }

        Assert.All(strip.Tabs.Where(t => t.GroupId is not null), t => Assert.False(t.IsPinned));
        Assert.All(strip.Tabs.Where(t => t.GroupId is not null), t => Assert.NotNull(strip.GroupOf(t)));
    }

    [Fact]
    public void CreateGroup_GathersTheTabsWhereTheFirstIs_WithANameAndAColour()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var tabs = strip.Tabs.ToList();

        var group = strip.CreateGroup([tabs[3], tabs[1]], "Inbox");

        Assert.NotNull(group);
        Assert.Equal("Inbox", group.Name);
        Assert.True(TabGroupColors.IsColor(group.Color));
        Assert.Equal([tabs[0], tabs[1], tabs[3], tabs[2]], strip.Tabs);
        Assert.Equal([tabs[1], tabs[3]], strip.TabsOf(group));
        AssertInvariants(strip);
    }

    [Fact]
    public void NewGroups_GetDifferentColours_UntilThePaletteRunsOut()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");

        var first = strip.CreateGroup([strip.Tabs[0]])!;
        var second = strip.CreateGroup([strip.Tabs[1]])!;

        Assert.NotEqual(first.Color, second.Color);
        Assert.Equal(TabGroupColors.All.Count, TabGroupColors.All.Select(c => c.Hex).Distinct().Count());
    }

    [Fact]
    public void PinnedTabs_AndForeignTabs_AreNotGrouped()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        strip.SetPinned(strip.Tabs[1], true);
        var stranger = new TabViewModel(TabTestSupport.Services(), P(@"C:\d1"));

        var pinned = strip.Tabs.First(t => t.IsPinned);

        Assert.Null(strip.CreateGroup([pinned]));
        Assert.Null(strip.CreateGroup([stranger]));
        var group = strip.CreateGroup([strip.Tabs.First(t => !t.IsPinned), stranger, pinned]);
        Assert.Single(strip.TabsOf(group!));
        Assert.False(strip.AddToGroup(pinned, group!));
        Assert.False(strip.AddToGroup(stranger, group!));
        AssertInvariants(strip);
    }

    [Fact]
    public void AddToGroup_PutsTheTabAtTheEnd_OrWhereAsked()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[0], t[1]])!;

        Assert.True(strip.AddToGroup(t[3], group));
        Assert.Equal([t[0], t[1], t[3], t[2]], strip.Tabs);
        Assert.True(strip.AddToGroup(t[2], group, position: 0));
        Assert.Equal([t[2], t[0], t[1], t[3]], strip.Tabs);
        AssertInvariants(strip);
    }

    [Fact]
    public void AddingATabOfAnotherGroup_MovesItOver_AndAnEmptiedGroupDisappears()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var t = strip.Tabs.ToList();
        var one = strip.CreateGroup([t[0]])!;
        var two = strip.CreateGroup([t[1], t[2]])!;

        strip.AddToGroup(t[0], two);

        Assert.DoesNotContain(one, strip.Groups);
        Assert.Equal(3, strip.TabsOf(two).Count);
        AssertInvariants(strip);
    }

    [Fact]
    public void RemoveFromGroup_LandsTheTabRightAfterTheGroup_AndTheBlockStaysWhole()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[0], t[1], t[2]])!;

        Assert.True(strip.RemoveFromGroup(t[1]));

        Assert.Equal([t[0], t[2], t[1], t[3]], strip.Tabs);
        Assert.Null(t[1].GroupId);
        Assert.Equal([t[0], t[2]], strip.TabsOf(group));
        Assert.False(strip.RemoveFromGroup(t[3]));
        AssertInvariants(strip);
    }

    [Fact]
    public void RemovingTheLastTab_EndsTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var group = strip.CreateGroup([strip.Tabs[0]])!;

        strip.RemoveFromGroup(strip.Tabs[0]);

        Assert.DoesNotContain(group, strip.Groups);
    }

    [Fact]
    public void Ungroup_KeepsTheTabsInPlace()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var before = strip.Tabs.ToList();
        var group = strip.CreateGroup([before[1], before[2]])!;

        Assert.True(strip.Ungroup(group));

        Assert.Equal(before, strip.Tabs);
        Assert.Empty(strip.Groups);
        Assert.All(strip.Tabs, t => Assert.Null(t.GroupId));
    }

    [Fact]
    public void ANewTabNextToAGroupedTab_NeverLandsInsideTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[0], t[1], t[2]])!;
        strip.Select(t[0]);

        var added = strip.NewTab(P(@"C:\d2"));

        Assert.Equal(t[2], strip.Tabs[2]);
        Assert.Equal(added, strip.Tabs[3]);
        Assert.Null(added.GroupId);
        Assert.Equal(3, strip.TabsOf(group).Count);
        AssertInvariants(strip);
    }

    [Fact]
    public void NewTabInGroup_JoinsTheEndOfTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var group = strip.CreateGroup([strip.Tabs[0], strip.Tabs[1]])!;

        var added = strip.NewTabInGroup(group, P(@"C:\d2"))!;

        Assert.Equal(group.Id, added.GroupId);
        Assert.Equal(added, strip.Tabs[2]);
        Assert.Equal(added, strip.Selected);
        AssertInvariants(strip);
    }

    [Fact]
    public void Duplicate_OfAGroupedTab_StaysInTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var group = strip.CreateGroup([strip.Tabs[0]])!;

        var copy = strip.Duplicate(strip.Tabs[0]);

        Assert.Equal(group.Id, copy.GroupId);
        AssertInvariants(strip);
    }

    [Fact]
    public void Pinning_ATabInAGroup_TakesItOutOfTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var group = strip.CreateGroup([strip.Tabs[0], strip.Tabs[1]])!;
        var tab = strip.Tabs[1];

        strip.SetPinned(tab, true);

        Assert.Null(tab.GroupId);
        Assert.Equal(tab, strip.Tabs[0]);
        Assert.Single(strip.TabsOf(group));
        AssertInvariants(strip);
    }

    [Fact]
    public void Collapse_SkipsTheGroupsTabsForCtrlTabAndCtrlNumbers()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[1], t[2]])!;
        strip.Select(t[0]);

        Assert.True(strip.SetCollapsed(group, true));
        strip.Cycle(forward: true);
        var afterCycle = strip.Selected;
        strip.SelectByNumber(2);
        var afterNumber = strip.Selected;
        strip.SelectByNumber(9);

        Assert.Equal(t[3], afterCycle);
        Assert.Equal(t[3], afterNumber);
        Assert.Equal(t[3], strip.Selected);
        Assert.Equal([t[0], t[3]], strip.VisibleTabs);
    }

    [Fact]
    public void Collapsing_AGroupWithTheSelectedTab_MovesTheSelectionToANeighbour()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[1]])!;
        strip.Select(t[1]);

        strip.SetCollapsed(group, true);

        Assert.Equal(t[2], strip.Selected);
        Assert.True(group.IsCollapsed);
    }

    [Fact]
    public void AGroupHoldingEveryTab_CannotBeFoldedAway()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var group = strip.CreateGroup(strip.Tabs.ToList())!;

        Assert.False(strip.SetCollapsed(group, true));
        Assert.False(group.IsCollapsed);
    }

    [Fact]
    public void SelectingATabOfACollapsedGroup_OpensTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var group = strip.CreateGroup([strip.Tabs[1]])!;
        strip.SetCollapsed(group, true);

        strip.Select(strip.Tabs[1]);

        Assert.False(group.IsCollapsed);
    }

    [Fact]
    public void ClosingTheSelectedTab_NeverSelectsOneHiddenInACollapsedGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[1], t[2]])!;
        strip.Select(t[0]);
        strip.SetCollapsed(group, true);

        strip.Close(t[0]);

        // Only hidden tabs remain, so one is shown and its group opens.
        Assert.NotNull(strip.Selected);
        Assert.False(group.IsCollapsed);
    }

    [Fact]
    public void CloseGroup_ClosesItsTabs_AndReopenBringsTheGroupBackAsAGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var t = strip.Tabs.ToList();
        var group = strip.CreateGroup([t[1], t[2]], "Work", "#E74856")!;

        Assert.Equal(2, strip.CloseGroup(group));
        Assert.Equal(2, strip.Tabs.Count);
        Assert.Empty(strip.Groups);

        var reopened = strip.Reopen();

        Assert.NotNull(reopened);
        Assert.Equal(4, strip.Tabs.Count);
        var back = Assert.Single(strip.Groups);
        Assert.Equal(("Work", "#E74856", group.Id), (back.Name, back.Color, back.Id));
        Assert.Equal([P(@"C:\v"), P(@"C:\d1")], strip.TabsOf(back).Select(x => x.History.Current));
        Assert.False(strip.CanReopen);
        AssertInvariants(strip);
    }

    [Fact]
    public void ReopeningOneClosedTab_PutsItBackInItsGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]])!;
        strip.Close(strip.Tabs[2]);

        var back = strip.Reopen()!;

        Assert.Equal(group.Id, back.GroupId);
        Assert.Equal(2, strip.TabsOf(group).Count);
        AssertInvariants(strip);
    }

    [Fact]
    public void ReopeningALoneTabWhoseGroupEnded_RecreatesTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var group = strip.CreateGroup([strip.Tabs[1]], "Solo", "#16C60C")!;
        strip.Close(strip.Tabs[1]);
        Assert.Empty(strip.Groups);

        var back = strip.Reopen()!;

        Assert.Equal(group.Id, back.GroupId);
        Assert.Equal("Solo", strip.GroupOf(back)!.Name);
        AssertInvariants(strip);
    }

    [Fact]
    public void ClosingTheLastTabOfAGroup_RemovesTheGroup()
    {
        var strip = Strip(@"C:\a", @"C:\v");
        var group = strip.CreateGroup([strip.Tabs[1]])!;

        strip.Close(strip.Tabs[1]);

        Assert.DoesNotContain(group, strip.Groups);
    }

    [Fact]
    public void MoveGroup_MovesTheWholeBlock_LeftAndRight_AndHopsOverOtherGroups()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2", @"C:\d3");
        var t = strip.Tabs.ToList();
        var first = strip.CreateGroup([t[0], t[1]])!;
        var second = strip.CreateGroup([t[3], t[4]])!;

        Assert.True(strip.MoveGroup(first, 3));
        Assert.Equal([t[2], t[3], t[4], t[0], t[1]], strip.Tabs);
        Assert.True(strip.MoveGroup(first, 0));
        Assert.Equal([t[0], t[1], t[2], t[3], t[4]], strip.Tabs);

        // Landing between the two tabs of another group means the nearer edge of it.
        Assert.True(strip.MoveGroup(first, 3));
        Assert.Equal(second.Id, strip.Tabs[1].GroupId);
        AssertInvariants(strip);
    }

    [Fact]
    public void MoveGroup_NeverGoesAmongPinnedTabs()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1");
        strip.SetPinned(strip.Tabs[2], true);
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]])!;

        strip.MoveGroup(group, 0);

        Assert.True(strip.Tabs[0].IsPinned);
        AssertInvariants(strip);
    }

    [Fact]
    public void MovingAnUngroupedTab_HopsOverAGroupInsteadOfLandingInIt()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var t = strip.Tabs.ToList();
        strip.CreateGroup([t[1], t[2]]);

        strip.Move(t[0], 1);

        Assert.Equal([t[1], t[2], t[0], t[3]], strip.Tabs);
        AssertInvariants(strip);
    }

    [Fact]
    public void AGroupedTab_MovesWithinItsGroupOnly()
    {
        var strip = Strip(@"C:\a", @"C:\v", @"C:\d1", @"C:\d2");
        var t = strip.Tabs.ToList();
        strip.CreateGroup([t[1], t[2]]);

        strip.Move(t[1], 0);
        Assert.Equal(t[0], strip.Tabs[0]);
        strip.Move(t[1], 99);

        Assert.Equal([t[0], t[2], t[1], t[3]], strip.Tabs);
        AssertInvariants(strip);
    }

    [Fact]
    public void Rename_AndRecolour_ChangeTheGroup()
    {
        var strip = Strip(@"C:\a");
        var group = strip.CreateGroup([strip.Tabs[0]])!;

        group.Name = "NAS inbox";
        group.Color = "#f7630c";

        Assert.Equal("NAS inbox", group.ToInfo().Name);
        Assert.Equal(new TabGroupInfo(group.Id, "NAS inbox", "#f7630c", false), group.ToInfo());
        Assert.Equal("#F7630C", TabGroup.FromInfo(group.ToInfo()).Color);
    }

    [Fact]
    public void AGroupMadeWithABadColour_GetsTheDefault()
    {
        var group = new TabGroup(Guid.NewGuid(), "x", "red");

        Assert.Equal(TabGroupColors.Default.Hex, group.Color);
    }
}

public sealed class TabGroupWindowTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void TearOffGroup_MovesTheWholeGroupToANewWindow_KeepingItsLook()
    {
        var manager = new WindowManager(TabTestSupport.Services());
        var window = manager.OpenWindow(P(@"C:\a"));
        window.Tabs.NewTab(P(@"C:\v"));
        window.Tabs.NewTab(P(@"C:\d1"));
        var group = window.Tabs.CreateGroup([window.Tabs.Tabs[1], window.Tabs.Tabs[2]], "Moved", "#886CE4")!;

        var torn = manager.TearOffGroup(window, group, 300, 200);

        Assert.NotNull(torn);
        Assert.Single(window.Tabs.Tabs);
        Assert.Empty(window.Tabs.Groups);
        var moved = Assert.Single(torn.Tabs.Groups);
        Assert.Equal(("Moved", "#886CE4", group.Id), (moved.Name, moved.Color, moved.Id));
        Assert.Equal(2, torn.Tabs.TabsOf(moved).Count);
        Assert.True(torn.PlaceExplicitly);
    }

    [Fact]
    public void TearOffGroup_OfTheOnlyGroupInTheWindow_DoesNothing()
    {
        var manager = new WindowManager(TabTestSupport.Services());
        var window = manager.OpenWindow(P(@"C:\a"));
        var group = window.Tabs.CreateGroup(window.Tabs.Tabs.ToList())!;

        Assert.Null(manager.TearOffGroup(window, group, 0, 0));
        Assert.Single(manager.Windows);
    }

    [Fact]
    public void MergeGroup_MovesAGroupBetweenWindows_AndClosesTheEmptiedOne()
    {
        var manager = new WindowManager(TabTestSupport.Services());
        var from = manager.OpenWindow(P(@"C:\a"));
        var to = manager.OpenWindow(P(@"C:\v"));
        var group = from.Tabs.CreateGroup(from.Tabs.Tabs.ToList(), "Visitors")!;

        Assert.True(manager.MergeGroup(from, group, to));

        Assert.DoesNotContain(from, manager.Windows);
        Assert.Equal(2, to.Tabs.Tabs.Count);
        Assert.Equal("Visitors", Assert.Single(to.Tabs.Groups).Name);
        Assert.Equal(1, to.Tabs.Tabs.Count(t => t.GroupId is null));
    }

    [Fact]
    public void MergeGroup_IntoItself_IsRefused()
    {
        var manager = new WindowManager(TabTestSupport.Services());
        var window = manager.OpenWindow(P(@"C:\a"));
        var group = window.Tabs.CreateGroup([window.Tabs.Tabs[0]])!;

        Assert.False(manager.MergeGroup(window, group, window));
    }

    [Fact]
    public void DetachingOneTab_LeavesItsGroupBehind_AndItArrivesUngrouped()
    {
        var manager = new WindowManager(TabTestSupport.Services());
        var from = manager.OpenWindow(P(@"C:\a"));
        from.Tabs.NewTab(P(@"C:\v"));
        var group = from.Tabs.CreateGroup(from.Tabs.Tabs.ToList())!;
        var to = manager.OpenWindow(P(@"C:\d1"));
        var tab = from.Tabs.Tabs[1];

        manager.Merge(from, tab, to);

        Assert.Null(tab.GroupId);
        Assert.Single(from.Tabs.TabsOf(group));
        Assert.Empty(to.Tabs.Groups);
    }
}

public sealed class TabGroupSessionTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task GroupsSurviveASaveAndARestore_WithOrderColourNameAndCollapse()
    {
        var source = new WindowManager(TabTestSupport.Services());
        var window = source.OpenWindow(P(@"C:\a"));
        window.Tabs.NewTab(P(@"C:\v"));
        window.Tabs.NewTab(P(@"C:\d1"));
        window.Tabs.NewTab(P(@"C:\d2"));
        var group = window.Tabs.CreateGroup([window.Tabs.Tabs[1], window.Tabs.Tabs[2]], "Inbox", "#E3008C")!;
        window.Tabs.SetCollapsed(group, true);
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        var strip = target.Windows[0].Tabs;
        var restored = Assert.Single(strip.Groups);
        Assert.Equal(("Inbox", "#E3008C", true, group.Id), (restored.Name, restored.Color, restored.IsCollapsed, restored.Id));
        Assert.Equal([P(@"C:\v"), P(@"C:\d1")], strip.TabsOf(restored).Select(t => t.History.Current));
        Assert.Equal(2, strip.Tabs.Count(t => t.GroupId is null));
    }

    [Fact]
    public async Task ASessionSavedBeforeGroupsExisted_OpensWithoutGroups()
    {
        var source = new WindowManager(TabTestSupport.Services());
        source.OpenWindow(P(@"C:\a"));
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);
        var text = fs.File.ReadAllText(@"C:\data\session.json");
        fs.File.WriteAllText(@"C:\data\session.json", System.Text.RegularExpressions.Regex.Replace(text, "\\s*\"(Groups|GroupId)\":[^\\r\\n]*", string.Empty));

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        Assert.Empty(target.Windows[0].Tabs.Groups);
        Assert.Single(target.Windows[0].Tabs.Tabs);
    }

    private static SessionState Crafted(IReadOnlyList<TabState> tabs, IReadOnlyList<TabGroupInfo>? groups) =>
        new([new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 0, tabs, Groups: groups)]);

    private static TabState Saved(string folder, Guid? group = null, bool pinned = false) =>
        new(new Navigation.NavigationSnapshot([], P(folder), []), Views.FolderViewSettings.Default, null, null, pinned, false, false, [], group);

    [Fact]
    public void ASavedFileThatBreaksTheRules_IsRepairedOnLoad()
    {
        var group = Guid.NewGuid();
        var other = Guid.NewGuid();
        var info = new TabGroupInfo(group, "G", "#0078D4");

        // Members apart, one pinned, one naming a group the file never described, and a group nobody is in.
        var state = Crafted(
            [Saved(@"C:\a", group), Saved(@"C:\v"), Saved(@"C:\d1", group), Saved(@"C:\d2", group, pinned: true), Saved(@"C:\d3", other)],
            [info, new TabGroupInfo(Guid.NewGuid(), "Empty", "#E74856")]);

        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, state);
        var strip = target.Windows[0].Tabs;

        Assert.Equal(2, strip.Groups.Count);
        Assert.DoesNotContain(strip.Groups, g => g.Name == "Empty");
        var members = strip.TabsOf(strip.Groups.First(g => g.Id == group));
        Assert.Equal([P(@"C:\a"), P(@"C:\d1")], members.Select(t => t.History.Current));
        Assert.Equal(1, strip.Tabs.IndexOf(members[1]) - strip.Tabs.IndexOf(members[0]));
        Assert.All(strip.Tabs.Where(t => t.IsPinned), t => Assert.Null(t.GroupId));
        Assert.NotNull(strip.GroupOf(strip.Tabs.First(t => t.History.Current == P(@"C:\d3"))));
    }

    [Fact]
    public async Task AChangeToAGroup_IsSavedByTheRecorder()
    {
        var fs = new MockFileSystem();
        var time = new ManualTimeProvider();
        var windows = new WindowManager(TabTestSupport.Services());
        var window = windows.OpenWindow(P(@"C:\a"));
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        using var recorder = new SessionRecorder(windows, store, new Leviathan.ViewModels.Tests.InlineUiContext(), time);
        recorder.Start();

        var group = window.Tabs.CreateGroup([window.Tabs.Tabs[0]], "Before")!;
        time.Advance(SessionRecorder.Delay);
        await recorder.Saved;
        group.Name = "After";
        time.Advance(SessionRecorder.Delay);
        await recorder.Saved;

        using var reader = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        var saved = await reader.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("After", Assert.Single(saved!.State.Windows[0].Groups!).Name);
    }
}

public sealed class TabGroupCommandTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static async Task<WindowCommandsTests.Harness> MakeAsync()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        h.Window.Tabs.NewTab(P(@"C:\v"));
        h.Window.Tabs.NewTab(P(@"C:\d1"));
        return h;
    }

    [Fact]
    public async Task NewGroup_GroupsTheCurrentTab_WithTheGivenName()
    {
        var h = await MakeAsync();

        var result = await h.Run(CommandIds.TabGroupNew, "Work");

        Assert.Equal(CommandResult.Executed, result);
        var group = Assert.Single(h.Window.Tabs.Groups);
        Assert.Equal("Work", group.Name);
        Assert.Equal(group.Id, h.Tab.GroupId);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "tab.group.new");
    }

    [Fact]
    public async Task APinnedTab_CannotStartAGroup()
    {
        var h = await MakeAsync();
        h.Window.Tabs.SetPinned(h.Tab, true);

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabGroupNew));
    }

    [Fact]
    public async Task TheGroupCommands_RenameRecolourCollapseUngroupAndClose()
    {
        var h = await MakeAsync();
        var strip = h.Window.Tabs;
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]], "Old")!;

        await h.Run(CommandIds.TabGroupRename, "  New  ", group);
        await h.Run(CommandIds.TabGroupColor, "#16c60c", group);
        var badColour = await h.Run(CommandIds.TabGroupColor, "green", group);
        await h.Run(CommandIds.TabGroupCollapse, null, group);
        var collapsed = group.IsCollapsed;
        await h.Run(CommandIds.TabGroupCollapse, "false", group);

        Assert.Equal(("New", "#16C60C"), (group.Name, group.Color));
        Assert.Equal(CommandResult.Disabled, badColour);
        Assert.True(collapsed);
        Assert.False(group.IsCollapsed);

        await h.Run(CommandIds.TabGroupUngroup, null, group);
        Assert.Empty(strip.Groups);

        var again = strip.CreateGroup([strip.Tabs[1]])!;
        await h.Run(CommandIds.TabGroupClose, null, again);
        Assert.Equal(2, strip.Tabs.Count);
    }

    [Fact]
    public async Task AddAndRemove_MoveATabInAndOutOfAGroup()
    {
        var h = await MakeAsync();
        var strip = h.Window.Tabs;
        var group = strip.CreateGroup([strip.Tabs[0]])!;
        var tab = strip.Tabs[2];

        await h.Run(CommandIds.TabGroupAdd, parameter: new TabGroupAdd(tab, group));
        var added = tab.GroupId == group.Id;
        await h.Run(CommandIds.TabGroupRemove, parameter: tab);

        Assert.True(added);
        Assert.Null(tab.GroupId);
    }

    [Fact]
    public async Task Collapsing_TheGroupThatHoldsEveryTab_SaysWhyAndChangesNothing()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var group = h.Window.Tabs.CreateGroup(h.Window.Tabs.Tabs.ToList())!;

        await h.Run(CommandIds.TabGroupCollapse, null, group);

        Assert.False(group.IsCollapsed);
        Assert.Contains("every tab", h.Shell.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToWindow_MovesTheGroupOut_AndDragCommandsMoveAndMergeIt()
    {
        var h = await MakeAsync();
        var strip = h.Window.Tabs;
        var group = strip.CreateGroup([strip.Tabs[1], strip.Tabs[2]])!;

        await h.Run(CommandIds.TabGroupMove, parameter: new TabGroupMove(group, 0));
        var moved = strip.Tabs[0].GroupId == group.Id;
        await h.Run(CommandIds.TabGroupToWindow, null, group);

        Assert.True(moved);
        Assert.Equal(2, h.Windows.Windows.Count);
        Assert.Equal(group.Id, Assert.Single(h.Windows.Windows[1].Tabs.Groups).Id);
    }

    [Fact]
    public async Task NewTabInGroup_OpensAtTheEnd_AndListsTheFolder()
    {
        var h = await MakeAsync();
        var strip = h.Window.Tabs;
        var group = strip.CreateGroup([strip.Tabs[0]])!;

        await h.Run(CommandIds.TabGroupNewTab, @"C:\a\b", group);

        Assert.Equal(2, strip.TabsOf(group).Count);
        Assert.Equal(P(@"C:\a\b"), strip.Selected!.History.Current);
        Assert.Equal(Folders.FolderLoadState.Loaded, strip.Selected.Folder.State);
    }

    [Fact]
    public async Task ACommandWithAGroupFromAnotherWindow_IsRefused()
    {
        var h = await MakeAsync();
        var foreign = new TabGroup(Guid.NewGuid(), "x", "#0078D4");

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.TabGroupRename, "y", foreign));
    }
}
