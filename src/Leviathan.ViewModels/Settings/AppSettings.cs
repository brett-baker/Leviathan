namespace Leviathan.ViewModels.Settings;

/// <summary>Light, dark or follow Windows.</summary>
public enum ThemeMode
{
    /// <summary>Follow the system theme.</summary>
    System,

    /// <summary>Light theme.</summary>
    Light,

    /// <summary>Dark theme.</summary>
    Dark,
}

/// <summary>Row and control spacing (spec D18).</summary>
public enum Density
{
    /// <summary>More items per screen.</summary>
    Compact,

    /// <summary>Roomier rows.</summary>
    Comfortable,
}

/// <summary>What the app opens at startup (spec §17.1).</summary>
public enum StartupMode
{
    /// <summary>Restore the Last session profile.</summary>
    LastSession,

    /// <summary>Open the profile named in <see cref="GeneralSettings.StartupProfile"/>.</summary>
    Profile,

    /// <summary>Show a picker.</summary>
    Ask,
}

/// <summary>General page (spec §17.2).</summary>
public sealed record GeneralSettings
{
    /// <summary>What happens at startup.</summary>
    public StartupMode Startup { get; init; } = StartupMode.LastSession;

    /// <summary>Profile to open when <see cref="Startup"/> is <see cref="StartupMode.Profile"/>.</summary>
    public string? StartupProfile { get; init; }

    /// <summary>Open items with a single click instead of a double click (spec §8.4).</summary>
    public bool SingleClickToOpen { get; init; }

    /// <summary>Location a new tab opens; empty means Home.</summary>
    public string NewTabLocation { get; init; } = string.Empty;
}

/// <summary>Appearance page (spec §17.2).</summary>
public sealed record AppearanceSettings
{
    /// <summary>Theme.</summary>
    public ThemeMode Theme { get; init; } = ThemeMode.System;

    /// <summary>Density.</summary>
    public Density Density { get; init; } = Density.Comfortable;

    /// <summary>Mica backdrop on or off.</summary>
    public bool Mica { get; init; } = true;

    /// <summary>Accent colour as <c>#RRGGBB</c>, or empty to use the system accent.</summary>
    public string Accent { get; init; } = string.Empty;

    /// <summary>
    /// The id of the icon pack that draws the app's own icons (spec §4.3), e.g. <c>fluent</c> or <c>fontawesome</c>. An id that is
    /// not installed falls back to the default pack. Applies without a restart.
    /// </summary>
    public string IconPack { get; init; } = "fluent";
}

/// <summary>The settings people change while working: how much runs at once and what is done over the network (spec 16.1 Quick settings).</summary>
public sealed record PerformanceSettings
{
    /// <summary>Background jobs (folder sizes, thumbnails) that run at the same time, 1 to 8.</summary>
    public int MaxBackgroundJobs { get; init; } = 2;

    /// <summary>Operations (copies, moves, deletes) that run at the same time, 1 to 16.</summary>
    public int MaxRunningJobs { get; init; } = 3;

    /// <summary>Operations that run at the same time on one device or share, 1 to 4.</summary>
    public int MaxPerDevice { get; init; } = 1;

    /// <summary>Count the size of folders on network drives. Each one means walking the share, so turning it off spares a slow NAS.</summary>
    public bool FolderSizesOnNetwork { get; init; } = true;

    /// <summary>Make thumbnails of files on network drives. Each one means reading the file over the network.</summary>
    public bool ThumbnailsOnNetwork { get; init; } = true;

    /// <summary>The same settings with every number inside its limits (a hand-edited file cannot ask for 500 threads).</summary>
    public PerformanceSettings Clamped() => this with
    {
        MaxBackgroundJobs = Math.Clamp(MaxBackgroundJobs, 1, 8),
        MaxRunningJobs = Math.Clamp(MaxRunningJobs, 1, 16),
        MaxPerDevice = Math.Clamp(MaxPerDevice, 1, 4),
    };
}

/// <summary>Files and folders page (spec §17.2).</summary>
public sealed record FilesSettings
{
    /// <summary>Show hidden items.</summary>
    public bool ShowHidden { get; init; }

    /// <summary>Show system items.</summary>
    public bool ShowSystem { get; init; }

    /// <summary>Show file extensions.</summary>
    public bool ShowExtensions { get; init; } = true;

    /// <summary>Show folder sizes in the Size column (computed lazily by the folder-size service).</summary>
    public bool ShowFolderSizes { get; init; } = true;
}

/// <summary>Tabs and windows page (spec §4.1, §17.2).</summary>
public sealed record TabSettings
{
    /// <summary>Closing the last tab resets to Home instead of closing the window.</summary>
    public bool LastTabResetsToHome { get; init; }

    /// <summary>Ctrl+Tab cycles in most-recently-used order.</summary>
    public bool CtrlTabUsesMruOrder { get; init; }
}

/// <summary>
/// All user settings. A JSON file with an <see cref="SchemaVersion"/>; unknown properties are ignored so an older build
/// can read a newer file (spec §17.2).
/// </summary>
public sealed record AppSettings
{
    /// <summary>The newest schema this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>General page.</summary>
    public GeneralSettings General { get; init; } = new();

    /// <summary>Appearance page.</summary>
    public AppearanceSettings Appearance { get; init; } = new();

    /// <summary>Files and folders page.</summary>
    public FilesSettings Files { get; init; } = new();

    /// <summary>Tabs and windows page.</summary>
    public TabSettings Tabs { get; init; } = new();

    /// <summary>What runs at once and what is done over the network (the Quick settings tab).</summary>
    public PerformanceSettings Performance { get; init; } = new();

    /// <summary>Key overrides: command id to gesture text (empty text unbinds). Only the user's changes are stored.</summary>
    public Dictionary<string, string> KeyBindings { get; init; } = [];
}
