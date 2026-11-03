using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

public sealed class SidebarLayoutTests
{
    [Theory]
    [InlineData(100, 1600, 140)]
    [InlineData(139.9, 1600, 140)]
    [InlineData(300, 1600, 300)]
    [InlineData(900, 1600, 520)]
    [InlineData(400, 600, 300)]
    [InlineData(400, 200, 140)]
    public void Clamp_KeepsTheWidthBetweenTheMinimumAndTheSmallerOfTheMaximumAndHalfTheWindow(double width, double window, double expected) =>
        Assert.Equal(expected, SidebarLayout.Clamp(width, window));

    [Fact]
    public void Clamp_OfNotANumber_IsTheDefault() => Assert.Equal(SidebarLayout.DefaultWidth, SidebarLayout.Clamp(double.NaN, 1600));

    [Fact]
    public void ANewWindow_HasTheDefaultWidth_AndIsNotCollapsed()
    {
        var window = new WindowManager(TabTestSupport.Services()).OpenWindow(LocationPath.Parse(@"C:\a"));

        Assert.Equal(220, window.SidebarWidth);
        Assert.False(window.SidebarCollapsed);
    }

    [Fact]
    public async Task TheSidebarCommand_TogglesItAndKeepsTheWidth()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        h.Window.SidebarWidth = 333;

        await h.Run(CommandIds.ViewSidebar);
        var collapsed = h.Window.SidebarCollapsed;
        await h.Run(CommandIds.ViewSidebar);

        Assert.True(collapsed);
        Assert.False(h.Window.SidebarCollapsed);
        Assert.Equal(333, h.Window.SidebarWidth);
        Assert.Equal(CommandIds.ViewSidebar, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Ctrl+B"), isSplit: false));
    }

    [Fact]
    public async Task TheWidthAndCollapse_AreSavedInTheSession_AndComeBack()
    {
        var source = new WindowManager(TabTestSupport.Services());
        var window = source.OpenWindow(LocationPath.Parse(@"C:\a"));
        window.SidebarWidth = 301;
        window.SidebarCollapsed = true;
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        Assert.Equal(301, target.Windows[0].SidebarWidth);
        Assert.True(target.Windows[0].SidebarCollapsed);
    }

    [Fact]
    public async Task ASessionSavedBeforeTheSplitterExisted_OpensWithTheDefaults()
    {
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        var source = new WindowManager(TabTestSupport.Services());
        source.OpenWindow(LocationPath.Parse(@"C:\a"));
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);
        var text = fs.File.ReadAllText(@"C:\data\session.json");
        var old = System.Text.RegularExpressions.Regex.Replace(text, "\\s*\"Sidebar(Width|Collapsed)\":\\s*[^,\\r\\n}]+,?", string.Empty);
        fs.File.WriteAllText(@"C:\data\session.json", old);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        Assert.DoesNotContain("SidebarWidth", old, StringComparison.Ordinal);
        Assert.Equal(220, target.Windows[0].SidebarWidth);
        Assert.False(target.Windows[0].SidebarCollapsed);
    }

    [Fact]
    public void ASavedWidthOutsideTheLimits_IsBroughtBackInside()
    {
        var state = new SessionState([new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 0,
            [new Tabs.TabState(new Navigation.NavigationSnapshot([], LocationPath.Parse(@"C:\a"), []), Views.FolderViewSettings.Default, null, null, false, false, false, [])], 9999, false)]);

        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, state);

        Assert.Equal(SidebarLayout.MaxWidth, target.Windows[0].SidebarWidth);
    }
}
