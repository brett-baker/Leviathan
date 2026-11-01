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

    /// <summary>Key overrides: command id to gesture text (empty text unbinds). Only the user's changes are stored.</summary>
    public Dictionary<string, string> KeyBindings { get; init; } = [];
}
