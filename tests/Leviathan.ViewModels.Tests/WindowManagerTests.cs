using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

public sealed class WindowManagerTests
{
    private static WindowManager Manager() => new(TabTestSupport.Services());

    private static Leviathan.Core.Paths.LocationPath P(string path) => TabTestSupport.P(path);

    [Fact]
    public void OpenWindow_HasOneTabAtHome_AndCascadesFromTheNewest()
    {
        var manager = Manager();

        var first = manager.OpenWindow();
        var second = manager.OpenWindow(P(@"C:\a"));

        Assert.Equal(P(@"C:\Home"), first.Tabs.Selected?.History.Current);
        Assert.Equal(P(@"C:\a"), second.Tabs.Selected?.History.Current);
        Assert.Equal(WindowManager.DefaultRect, first.Bounds.Rect);
        Assert.Equal(first.Bounds.Rect.X + 32, second.Bounds.Rect.X);
        Assert.Equal(2, manager.Windows.Count);
    }

    [Fact]
    public void TearOff_MovesTheTabIntoANewWindowAtTheDropPoint_KeepingItsHistory()
    {
        var manager = Manager();
        var window = manager.OpenWindow(P(@"C:\a"));
        var tab = window.Tabs.NewTab(P(@"C:\v"));
        _ = tab.NavigateAsync(P(@"C:\v"), CancellationToken.None);

        var torn = manager.TearOff(window, tab, 900, 200);

        Assert.NotNull(torn);
        Assert.Single(window.Tabs.Tabs);
        Assert.Same(tab, torn.Tabs.Selected);
        Assert.Equal(900, torn.Bounds.Rect.X);
        Assert.Equal(200, torn.Bounds.Rect.Y);
        Assert.False(window.Tabs.CanReopen);
        Assert.Equal(2, manager.Windows.Count);
    }

    [Fact]
    public void TearOff_OfTheOnlyTab_DoesNothing()
    {
        var manager = Manager();
        var window = manager.OpenWindow();

        Assert.Null(manager.TearOff(window, window.Tabs.Tabs[0], 10, 10));
        Assert.Single(manager.Windows);
    }

    [Fact]
    public void Merge_MovesTheTab_AndClosesTheEmptiedWindow()
    {
        var manager = Manager();
        var from = manager.OpenWindow(P(@"C:\a"));
        var to = manager.OpenWindow(P(@"C:\v"));
        var tab = from.Tabs.Tabs[0];
        var closed = new List<WindowViewModel>();
        manager.WindowClosed += (_, w) => closed.Add(w);

        Assert.True(manager.Merge(from, tab, to, 0));

        Assert.Equal(["a", "v"], to.Tabs.Tabs.Select(t => t.Title));
        Assert.Equal([from], closed);
        Assert.Single(manager.Windows);
    }

    [Fact]
    public void Merge_IntoTheSameWindowOrAForeignTab_IsRejected()
    {
        var manager = Manager();
        var a = manager.OpenWindow(P(@"C:\a"));
        var b = manager.OpenWindow(P(@"C:\v"));

        Assert.False(manager.Merge(a, a.Tabs.Tabs[0], a));
        Assert.False(manager.Merge(a, b.Tabs.Tabs[0], b));
    }

    [Fact]
    public void ClosingTheLastTab_ClosesTheWindow_AndTheLastWindowRaisesAllWindowsClosed()
    {
        var manager = Manager();
        var window = manager.OpenWindow();
        var allClosed = false;
        manager.AllWindowsClosed += (_, _) => allClosed = true;

        window.Tabs.Close(window.Tabs.Tabs[0]);

        Assert.Empty(manager.Windows);
        Assert.True(allClosed);
    }

    [Fact]
    public void Close_RaisesWindowClosedOnce_AndIgnoresAnUnknownWindow()
    {
        var manager = Manager();
        var window = manager.OpenWindow();
        var other = Manager().OpenWindow();
        var count = 0;
        manager.WindowClosed += (_, _) => count++;

        manager.Close(other);
        manager.Close(window);
        manager.Close(window);

        Assert.Equal(1, count);
    }

    private static readonly MonitorInfo Primary = new(new ScreenRect(0, 0, 1920, 1040), IsPrimary: true);
    private static readonly MonitorInfo Second = new(new ScreenRect(1920, 0, 2560, 1400), IsPrimary: false);

    [Fact]
    public void Fit_BoundsOnAMonitor_AreKept()
    {
        var saved = new WindowBounds(new ScreenRect(2000, 100, 1200, 800), IsMaximized: true);

        var fitted = BoundsFitter.Fit(saved, [Primary, Second]);

        Assert.Equal(saved, fitted);
    }

    [Fact]
    public void Fit_AWindowHangingOffTheBottomOrRight_IsPulledFullyOntoItsMonitor()
    {
        var fitted = BoundsFitter.Fit(new WindowBounds(new ScreenRect(1000, 600, 1280, 800)), [Primary]);

        Assert.Equal(new ScreenRect(640, 240, 1280, 800), fitted.Rect);
    }

    [Fact]
    public void Fit_ALargerThanMonitorWindowStillVisible_IsLeftWhereItIs()
    {
        var saved = new WindowBounds(new ScreenRect(-200, 10, 2400, 1200));

        Assert.Equal(saved, BoundsFitter.Fit(saved, [Primary]));
    }

    [Fact]
    public void Fit_MonitorGone_CentersOnThePrimary()
    {
        var saved = new WindowBounds(new ScreenRect(2400, 200, 1200, 800));

        var fitted = BoundsFitter.Fit(saved, [Primary]);

        Assert.Equal(new ScreenRect(360, 120, 1200, 800), fitted.Rect);
    }

    [Fact]
    public void Fit_WindowLargerThanTheMonitor_IsShrunkToFit()
    {
        var saved = new WindowBounds(new ScreenRect(-5000, -5000, 3000, 2000));

        var fitted = BoundsFitter.Fit(saved, [Primary]);

        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), fitted.Rect);
    }

    [Fact]
    public void Fit_TitleBarOffTheTop_IsBroughtBack()
    {
        var saved = new WindowBounds(new ScreenRect(100, -400, 1000, 800));

        var fitted = BoundsFitter.Fit(saved, [Primary]);

        Assert.True(fitted.Rect.Y >= 0);
    }

    [Fact]
    public void Fit_TinyBounds_AreRaisedToTheMinimumSize()
    {
        var fitted = BoundsFitter.Fit(new WindowBounds(new ScreenRect(100, 100, 50, 50)), [Primary]);

        Assert.Equal(BoundsFitter.MinimumSize.Width, fitted.Rect.Width);
        Assert.Equal(BoundsFitter.MinimumSize.Height, fitted.Rect.Height);
    }

    [Fact]
    public void Fit_NoMonitorInfo_ReturnsTheSavedBounds()
    {
        var saved = new WindowBounds(new ScreenRect(1, 2, 3, 4));

        Assert.Equal(saved, BoundsFitter.Fit(saved, []));
    }
}
