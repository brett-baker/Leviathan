using Leviathan.Core.Paths;
using Leviathan.Diagnostics;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Views;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

/// <summary>A tab split into two panes (spec §15.1): each pane its own path, history, view and selection; the bars follow the focused one.</summary>
public sealed class SplitPaneTests
{
    private static LocationPath P(string path) => TabTestSupport.P(path);

    private static async Task<TabViewModel> OpenAsync(TabServices? services = null)
    {
        var tab = new TabViewModel(services ?? TabTestSupport.Services(), P(@"C:\a"));
        await tab.ReloadAsync(CancellationToken.None);
        return tab;
    }

    [Fact]
    public async Task ANewTab_HasOnePane_AndItIsTheFocusedOne()
    {
        var tab = await OpenAsync();

        Assert.False(tab.IsSplit);
        Assert.Equal(PaneLayout.Single, tab.Panes);
        Assert.Null(tab.Secondary);
        Assert.Same(tab, tab.Focused);
        Assert.Null(tab.Other);
    }

    [Fact]
    public async Task Split_OpensTheFocusedFoldersInTheNewPane_AndListsIt()
    {
        var tab = await OpenAsync();
        await tab.NavigateAsync(P(@"C:\a\b"), CancellationToken.None);

        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);

        Assert.True(tab.IsSplit);
        Assert.Equal(PaneLayout.SideBySide, tab.Panes);
        Assert.Equal(P(@"C:\a\b"), tab.Secondary!.History.Current);
        Assert.Contains(tab.Secondary.Items.Rows, r => r.Name == "2.txt");
        Assert.Same(tab, tab.Focused);
        Assert.Same(tab.Secondary, tab.Other);
    }

    [Fact]
    public async Task EachPane_HasItsOwnPathHistoryAndSelection()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);

        await tab.Secondary!.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        tab.Secondary.Selection.Click(tab.Secondary.Items.Rows[0]);

        Assert.Equal(P(@"C:\a"), tab.History.Current);
        Assert.Equal(P(@"C:\v"), tab.Secondary.History.Current);
        Assert.True(tab.Secondary.History.CanGoBack);
        Assert.False(tab.History.CanGoBack);
        Assert.Equal(0, tab.Selection.Count);
        Assert.Equal(1, tab.Secondary.Selection.Count);
    }

    [Fact]
    public async Task EachPane_KeepsItsOwnLayout()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.TopBottom, CancellationToken.None);

        tab.Secondary!.SetLayout(ViewLayout.Thumbnails);

        Assert.Equal(ViewLayout.Details, tab.Layout);
        Assert.Equal(ViewLayout.Thumbnails, tab.Secondary.Layout);
    }

    [Fact]
    public async Task FocusPane_MovesWhatTheBarsFollow_AndRaisesTheChange()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.FocusPane(1);

        Assert.Same(tab.Secondary, tab.Focused);
        Assert.Same(tab, tab.Other);
        Assert.Contains(nameof(TabViewModel.FocusedPane), raised);
        tab.FocusPane(1);
        Assert.Equal(1, raised.Count(n => n == nameof(TabViewModel.FocusedPane)));
    }

    [Fact]
    public async Task FocusPane_WithoutASplit_StaysOnTheFirstPane()
    {
        var tab = await OpenAsync();

        tab.FocusPane(1);

        Assert.Equal(0, tab.FocusedPane);
        Assert.Same(tab, tab.Focused);
    }

    [Fact]
    public async Task ChangingTheLayoutOfASplit_KeepsTheSecondPane()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);
        var second = tab.Secondary;

        await tab.SetPanesAsync(PaneLayout.TopBottom, CancellationToken.None);

        Assert.Same(second, tab.Secondary);
        Assert.Equal(PaneLayout.TopBottom, tab.Panes);
    }

    [Fact]
    public async Task CloseSplit_WithTheFirstPaneFocused_KeepsTheFirstPane()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);
        await tab.Secondary!.NavigateAsync(P(@"C:\v"), CancellationToken.None);

        await tab.SetPanesAsync(PaneLayout.Single, CancellationToken.None);

        Assert.False(tab.IsSplit);
        Assert.Equal(P(@"C:\a"), tab.History.Current);
    }

    [Fact]
    public async Task CloseSplit_WithTheSecondPaneFocused_KeepsTheSecondPane_ItsFolderAndView()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);
        await tab.Secondary!.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        tab.Secondary.SetLayout(ViewLayout.Thumbnails);
        tab.FocusPane(1);

        await tab.CloseSplitAsync(CancellationToken.None);

        Assert.False(tab.IsSplit);
        Assert.Equal(0, tab.FocusedPane);
        Assert.Equal(P(@"C:\v"), tab.History.Current);
        Assert.Equal(ViewLayout.Thumbnails, tab.Layout);
        Assert.Contains(tab.Items.Rows, r => r.Name == "x.mkv");
    }

    [Fact]
    public async Task CloseSplit_OfALockedTab_ShowsTheKeptFolderHere_NotInANewTab()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);
        await tab.Secondary!.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        tab.FocusPane(1);
        tab.IsLocked = true;
        var openedElsewhere = false;
        tab.OpenInNewTabRequested += (_, _) => openedElsewhere = true;

        await tab.CloseSplitAsync(CancellationToken.None);

        Assert.False(openedElsewhere);
        Assert.Equal(P(@"C:\v"), tab.History.Current);
    }

    [Fact]
    public async Task CaptureState_ThenRestore_KeepsTheSplit_ThePanesOwnHistoryAndTheFocus()
    {
        var provider = TabTestSupport.Tree();
        var tab = await OpenAsync(TabTestSupport.Services(provider));
        await tab.SetPanesAsync(PaneLayout.TopBottom, CancellationToken.None);
        await tab.Secondary!.NavigateAsync(P(@"C:\v"), CancellationToken.None);
        tab.FocusPane(1);
        var listCallsBefore = provider.ListCalls.Values.Sum();

        var restored = TabViewModel.Restore(tab.CaptureState(), TabTestSupport.Services(provider));

        Assert.True(restored.IsSplit);
        Assert.Equal(PaneLayout.TopBottom, restored.Panes);
        Assert.Equal(1, restored.FocusedPane);
        Assert.Equal(P(@"C:\a"), restored.History.Current);
        Assert.Equal(P(@"C:\v"), restored.Secondary!.History.Current);
        Assert.True(restored.Secondary.History.CanGoBack);
        Assert.Equal(listCallsBefore, provider.ListCalls.Values.Sum());
        Assert.Empty(restored.Secondary.Items.Rows);

        await restored.Secondary.ReloadAsync(CancellationToken.None);
        Assert.Contains(restored.Secondary.Items.Rows, r => r.Name == "x.mkv");
    }

    [Fact]
    public async Task ASingleTab_RestoresAsASingleTab()
    {
        var tab = await OpenAsync();

        var restored = TabViewModel.Restore(tab.CaptureState(), TabTestSupport.Services());

        Assert.False(restored.IsSplit);
        Assert.Null(restored.CaptureState().Split);
    }

    [Fact]
    public async Task AFileWrittenBeforeSplittingExisted_WithAnIsSplitFlagButNoPane_RestoresAsOnePane()
    {
        var old = (await OpenAsync()).CaptureState() with { IsSplit = true, Split = null };

        var restored = TabViewModel.Restore(old, TabTestSupport.Services());

        Assert.False(restored.IsSplit);
    }

    [Fact]
    public async Task ASecondPaneThatNavigates_RaisesAChangeOnTheTab_SoTheSessionIsSaved()
    {
        var tab = await OpenAsync();
        await tab.SetPanesAsync(PaneLayout.SideBySide, CancellationToken.None);
        var raised = 0;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.Secondary))
            {
                raised++;
            }
        };

        await tab.Secondary!.NavigateAsync(P(@"C:\v"), CancellationToken.None);

        Assert.True(raised > 0);
    }
}

/// <summary>The commands behind the pane buttons and Ctrl+Backslash work on the focused pane (spec §15.1).</summary>
public sealed class SplitPaneCommandTests
{
    private static LocationPath P(string path) => TabTestSupport.P(path);

    private static (WindowViewModel Window, CommandRegistry Commands) Open()
    {
        var windows = new WindowManager(TabTestSupport.Services());
        var window = windows.OpenWindow(P(@"C:\a"));
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        fs.AddDirectory(@"C:\a");
        fs.AddDirectory(@"C:\v");
        var commands = WindowCommands.Create(new ActionTrail(), window, windows, new AddressResolver(new FileSystemEntryProbe(fs)), new WindowCommandsTests.FakeLauncher(), new WindowCommandsTests.FakeShell(), () => P(@"C:\Home"));
        return (window, commands);
    }

    [Fact]
    public async Task CtrlBackslash_SplitsSideBySide_AndAgainClosesIt()
    {
        var (window, commands) = Open();
        var tab = window.Tabs.Selected!;
        await tab.ReloadAsync(CancellationToken.None);

        await commands.ExecuteAsync(CommandIds.PaneSideBySide, new CommandContext(null, null), CancellationToken.None);
        Assert.Equal(PaneLayout.SideBySide, tab.Panes);

        await commands.ExecuteAsync(CommandIds.PaneSideBySide, new CommandContext(null, null), CancellationToken.None);
        Assert.False(tab.IsSplit);
    }

    [Fact]
    public async Task CtrlShiftBackslash_SplitsTopAndBottom_AndTheOnePaneCommandClosesIt()
    {
        var (window, commands) = Open();
        var tab = window.Tabs.Selected!;
        await tab.ReloadAsync(CancellationToken.None);

        await commands.ExecuteAsync(CommandIds.PaneTopBottom, new CommandContext(null, null), CancellationToken.None);
        Assert.Equal(PaneLayout.TopBottom, tab.Panes);

        await commands.ExecuteAsync(CommandIds.PaneSingle, new CommandContext(null, null), CancellationToken.None);
        Assert.False(tab.IsSplit);
    }

    [Fact]
    public async Task TheDefaultKeys_AreBound_AndCountWhileSplit()
    {
        var map = WindowCommands.DefaultKeyMap();

        Assert.Equal(CommandIds.PaneSideBySide, map.Resolve(KeyGesture.Parse(@"Ctrl+\"), isSplit: false));
        Assert.Equal(CommandIds.PaneTopBottom, map.Resolve(KeyGesture.Parse(@"Ctrl+Shift+\"), isSplit: true));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Navigation_ActsOnTheFocusedPane_NotOnTheTab()
    {
        var (window, commands) = Open();
        var tab = window.Tabs.Selected!;
        await tab.ReloadAsync(CancellationToken.None);
        await commands.ExecuteAsync(CommandIds.PaneSideBySide, new CommandContext(null, null), CancellationToken.None);
        tab.FocusPane(1);

        await commands.ExecuteAsync(CommandIds.NavGo, new CommandContext(@"C:\v", null), CancellationToken.None);

        Assert.Equal(P(@"C:\a"), tab.History.Current);
        Assert.Equal(P(@"C:\v"), tab.Secondary!.History.Current);
    }

    [Fact]
    public async Task OpenInSplitPane_OpensTheFolderInTheOtherPane_SplittingFirstIfNeeded()
    {
        var (window, commands) = Open();
        var tab = window.Tabs.Selected!;
        await tab.ReloadAsync(CancellationToken.None);

        await commands.ExecuteAsync(CommandIds.OpenInSplitPane, new CommandContext(@"C:\v", null), CancellationToken.None);

        Assert.True(tab.IsSplit);
        Assert.Equal(P(@"C:\a"), tab.History.Current);
        Assert.Equal(P(@"C:\v"), tab.Secondary!.History.Current);
    }

    [Fact]
    public async Task TabCommands_StillActOnTheTab_WhateverPaneIsFocused()
    {
        var (window, commands) = Open();
        var tab = window.Tabs.Selected!;
        await tab.ReloadAsync(CancellationToken.None);
        await commands.ExecuteAsync(CommandIds.PaneSideBySide, new CommandContext(null, null), CancellationToken.None);
        tab.FocusPane(1);
        var count = window.Tabs.Tabs.Count;

        await commands.ExecuteAsync(CommandIds.TabDuplicate, new CommandContext(null, null), CancellationToken.None);

        Assert.Equal(count + 1, window.Tabs.Tabs.Count);
        var copy = window.Tabs.Selected!;
        Assert.NotSame(tab, copy);
        Assert.True(copy.IsSplit);
        Assert.Equal(1, copy.FocusedPane);
    }
}
