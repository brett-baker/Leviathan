using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Sessions;

/// <summary>One window of a saved session: where it sits and its tabs (spec §17.1).</summary>
/// <param name="Bounds">Position, size and whether it was maximized.</param>
/// <param name="ShowPreview">Whether the preview pane was open.</param>
/// <param name="SelectedTab">Index of the selected tab.</param>
/// <param name="Tabs">The tabs in strip order, with history, view, pin, lock, label, colour, split state and selection.</param>
/// <param name="SidebarWidth">Width of the sidebar in pixels.</param>
/// <param name="SidebarCollapsed">Whether the sidebar was collapsed.</param>
/// <param name="Groups">The tab groups (spec §4.1): name, colour and collapsed state; each tab names its group. Null in files saved before groups existed.</param>
/// <param name="Pane">The preview pane's width, video size and collapsed parts (spec §9). Null in files saved before the pane had them.</param>
public sealed record SessionWindow(WindowBounds Bounds, bool ShowPreview, int SelectedTab, IReadOnlyList<TabState> Tabs, double SidebarWidth = SidebarLayout.DefaultWidth, bool SidebarCollapsed = false, IReadOnlyList<TabGroupInfo>? Groups = null, PreviewPaneState? Pane = null);

/// <summary>How a window's preview pane was laid out (spec §9): remembered per window.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="VideoSize">The S / M / L preset, or null when the height was dragged.</param>
/// <param name="VideoHeight">The dragged height of the video area.</param>
/// <param name="PreviewCollapsed">Whether the preview part was collapsed.</param>
/// <param name="DetailsCollapsed">Whether the details part was collapsed.</param>
public sealed record PreviewPaneState(double Width, VideoSize? VideoSize, double VideoHeight, bool PreviewCollapsed, bool DetailsCollapsed);

/// <summary>A set of windows: the content of Last session and of every named profile.</summary>
/// <param name="Windows">The windows, oldest first.</param>
public sealed record SessionState(IReadOnlyList<SessionWindow> Windows)
{
    /// <summary>True when there is nothing worth restoring: no window with a tab.</summary>
    public bool IsEmpty => Windows.All(w => w.Tabs.Count == 0);

    /// <summary>Number of tabs in all windows.</summary>
    public int TabCount => Windows.Sum(w => w.Tabs.Count);
}

/// <summary>The built-in "Last session" profile as stored on disk.</summary>
/// <param name="SchemaVersion">Version of the file layout; unknown properties are ignored so an older build reads a newer file.</param>
/// <param name="SavedAt">When it was written.</param>
/// <param name="State">The windows.</param>
public sealed record SessionFile(int SchemaVersion, DateTimeOffset SavedAt, SessionState State)
{
    /// <summary>The newest layout this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>A named profile (spec §17.1), e.g. "NAS triage".</summary>
/// <param name="Name">Name, unique ignoring case.</param>
/// <param name="SavedAt">When it was saved.</param>
/// <param name="State">The windows.</param>
public sealed record SavedProfile(string Name, DateTimeOffset SavedAt, SessionState State);

/// <summary>The named profiles as stored on disk.</summary>
/// <param name="SchemaVersion">Version of the file layout.</param>
/// <param name="Profiles">The profiles in the order they were first saved.</param>
public sealed record ProfilesFile(int SchemaVersion, IReadOnlyList<SavedProfile> Profiles)
{
    /// <summary>The newest layout this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>No profiles.</summary>
    public static ProfilesFile Empty { get; } = new(CurrentSchemaVersion, []);
}
