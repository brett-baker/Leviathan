using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Sessions;

/// <summary>Turns the open windows into a <see cref="SessionState"/> and back (spec §17.1). Run on the UI thread: it reads view models.</summary>
public static class SessionCapture
{
    /// <summary>Captures every window that has at least one tab.</summary>
    public static SessionState Capture(WindowManager windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var captured = new List<SessionWindow>();
        foreach (var window in windows.Windows)
        {
            var tabs = window.Tabs.Tabs.Select(t => t.CaptureState()).ToList();
            if (tabs.Count == 0)
            {
                continue;
            }

            var selected = window.Tabs.Selected is { } tab ? Math.Max(0, window.Tabs.Tabs.IndexOf(tab)) : 0;
            captured.Add(new SessionWindow(window.Bounds, window.ShowPreview, selected, tabs, window.SidebarWidth, window.SidebarCollapsed));
        }

        return new SessionState(captured);
    }

    /// <summary>Opens every window of <paramref name="state"/>. Returns how many windows opened.</summary>
    public static int Restore(WindowManager windows, SessionState state)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(state);
        var opened = 0;
        foreach (var window in state.Windows)
        {
            windows.OpenWindow(window);
            opened++;
        }

        return opened;
    }
}
