using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Windows;

/// <summary>A rectangle in screen pixels.</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    /// <summary>Right edge.</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge.</summary>
    public int Bottom => Y + Height;

    /// <summary>Area of the overlap with <paramref name="other"/>, in square pixels (0 when apart).</summary>
    public long OverlapArea(ScreenRect other)
    {
        var width = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var height = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return width <= 0 || height <= 0 ? 0 : (long)width * height;
    }
}

/// <summary>A monitor's usable area (excluding the taskbar).</summary>
/// <param name="WorkArea">Usable rectangle in virtual-screen pixels.</param>
/// <param name="IsPrimary">True for the primary monitor.</param>
public sealed record MonitorInfo(ScreenRect WorkArea, bool IsPrimary);

/// <summary>Where a window was and whether it was maximized.</summary>
/// <param name="Rect">Restored (not maximized) bounds.</param>
/// <param name="IsMaximized">True when maximized.</param>
public sealed record WindowBounds(ScreenRect Rect, bool IsMaximized = false);

/// <summary>
/// Puts saved window bounds somewhere visible. A monitor that has been unplugged, or a resolution change, must never leave a
/// window off screen (spec §4.2).
/// </summary>
public static class BoundsFitter
{
    /// <summary>Smallest window the app will restore.</summary>
    public static readonly (int Width, int Height) MinimumSize = (480, 320);

    /// <summary>
    /// Returns <paramref name="saved"/> unchanged when a reasonable part of it (at least 100 by 50 pixels including the top edge)
    /// is on a monitor; otherwise centers a window of the same size, shrunk to fit, on the primary monitor.
    /// </summary>
    public static WindowBounds Fit(WindowBounds saved, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            return saved;
        }

        var rect = saved.Rect;
        var width = Math.Max(rect.Width, MinimumSize.Width);
        var height = Math.Max(rect.Height, MinimumSize.Height);
        rect = new ScreenRect(rect.X, rect.Y, width, height);

        // The title bar strip along the top must overlap a monitor enough to be grabbed.
        var titleStrip = new ScreenRect(rect.X, rect.Y, rect.Width, 32);
        var best = monitors.OrderByDescending(m => titleStrip.OverlapArea(m.WorkArea)).First();
        if (titleStrip.OverlapArea(best.WorkArea) >= 100 * 32 && rect.OverlapArea(best.WorkArea) >= 100 * 50)
        {
            return saved with { Rect = rect };
        }

        var home = (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).WorkArea;
        var fitWidth = Math.Min(rect.Width, home.Width);
        var fitHeight = Math.Min(rect.Height, home.Height);
        return saved with { Rect = new ScreenRect(home.X + ((home.Width - fitWidth) / 2), home.Y + ((home.Height - fitHeight) / 2), fitWidth, fitHeight) };
    }
}

/// <summary>One top-level window: its tabs and where it sits.</summary>
public sealed partial class WindowViewModel : ObservableObject
{
    [ObservableProperty]
    private WindowBounds _bounds;

    /// <summary>Creates a window.</summary>
    public WindowViewModel(TabStripViewModel tabs, WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        Tabs = tabs;
        _bounds = bounds;
    }

    /// <summary>Stable identity.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The tab strip.</summary>
    public TabStripViewModel Tabs { get; }

    /// <summary>True when <see cref="Bounds"/> was chosen on purpose (a torn-off tab's drop point) and the shell must move the window there; false lets Windows place it.</summary>
    public bool PlaceExplicitly { get; set; }
}

/// <summary>
/// All windows of the process (spec §4.2, D9): they share caches, the tag index and the worker connection, so a tab torn off
/// into a new window or merged into another keeps its history and listing. The last window closing ends the UI process but never
/// a busy worker, which is the app host's business.
/// </summary>
public sealed class WindowManager
{
    private readonly TabServices _services;
    private readonly List<WindowViewModel> _windows = [];

    /// <summary>Creates the manager.</summary>
    public WindowManager(TabServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>Open windows, oldest first.</summary>
    public IReadOnlyList<WindowViewModel> Windows => _windows;

    /// <summary>Size and offset of a new window when no bounds are given.</summary>
    public static ScreenRect DefaultRect { get; } = new(120, 80, 1280, 800);

    /// <summary>Raised after a window is opened.</summary>
    public event EventHandler<WindowViewModel>? WindowOpened;

    /// <summary>Raised after a window is closed.</summary>
    public event EventHandler<WindowViewModel>? WindowClosed;

    /// <summary>Raised when the last window has closed.</summary>
    public event EventHandler? AllWindowsClosed;

    /// <summary>Ctrl+N: opens a window with one tab at <paramref name="location"/> (Home by default), cascaded from the newest window.</summary>
    public WindowViewModel OpenWindow(LocationPath? location = null, WindowBounds? bounds = null)
    {
        var strip = new TabStripViewModel(_services);
        strip.NewTab(location);
        return Register(strip, bounds ?? Cascade());
    }

    /// <summary>Opens a window around an existing set of restored tabs (startup profiles).</summary>
    public WindowViewModel OpenWindow(TabStripViewModel strip, WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return Register(strip, bounds);
    }

    /// <summary>
    /// Drags a tab out of the strip: it becomes the only tab of a new window whose top-left corner is the drop point. Tearing
    /// off the only tab of a window is not a move and does nothing (returns null).
    /// </summary>
    public WindowViewModel? TearOff(WindowViewModel source, TabViewModel tab, int dropX, int dropY)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tab);
        if (!source.Tabs.Tabs.Contains(tab) || source.Tabs.Tabs.Count < 2)
        {
            return null;
        }

        source.Tabs.Detach(tab);
        var strip = new TabStripViewModel(_services);
        strip.Attach(tab);
        var size = source.Bounds.Rect;
        var torn = Register(strip, new WindowBounds(new ScreenRect(dropX, dropY, size.Width, size.Height)));
        torn.PlaceExplicitly = true;
        return torn;
    }

    /// <summary>The window whose strip holds <paramref name="tab"/>, or null.</summary>
    public WindowViewModel? FindWindowOf(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _windows.FirstOrDefault(w => w.Tabs.Tabs.Contains(tab));
    }

    /// <summary>Drops a tab onto another window's strip. The source window closes if it has no tabs left.</summary>
    public bool Merge(WindowViewModel from, TabViewModel tab, WindowViewModel to, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(to);
        if (from == to || !from.Tabs.Detach(tab))
        {
            return false;
        }

        to.Tabs.Attach(tab, index);
        if (from.Tabs.Tabs.Count == 0)
        {
            Close(from);
        }

        return true;
    }

    /// <summary>Closes a window and its tabs.</summary>
    public void Close(WindowViewModel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!_windows.Remove(window))
        {
            return;
        }

        foreach (var tab in window.Tabs.Tabs.ToList())
        {
            tab.Dispose();
        }

        WindowClosed?.Invoke(this, window);
        if (_windows.Count == 0)
        {
            AllWindowsClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private WindowViewModel Register(TabStripViewModel strip, WindowBounds bounds)
    {
        var window = new WindowViewModel(strip, bounds);
        strip.LastTabClosed += (_, _) => Close(window);
        _windows.Add(window);
        WindowOpened?.Invoke(this, window);
        return window;
    }

    private WindowBounds Cascade()
    {
        if (_windows.Count == 0)
        {
            return new WindowBounds(DefaultRect);
        }

        var last = _windows[^1].Bounds.Rect;
        return new WindowBounds(new ScreenRect(last.X + 32, last.Y + 32, last.Width, last.Height));
    }
}
