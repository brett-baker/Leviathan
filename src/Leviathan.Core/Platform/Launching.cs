using Leviathan.Core.Paths;

namespace Leviathan.Core.Platform;

/// <summary>Terminals offered by "Open terminal here" (spec §15.6).</summary>
public enum TerminalKind
{
    /// <summary>Windows Terminal.</summary>
    WindowsTerminal,

    /// <summary>Windows PowerShell.</summary>
    PowerShell,

    /// <summary>Command Prompt.</summary>
    CommandPrompt,

    /// <summary>The default WSL distribution.</summary>
    Wsl,
}

/// <summary>Outcome of asking Windows to start something.</summary>
/// <param name="Started">True when the process or dialog was started.</param>
/// <param name="Error">User-facing reason when it was not, or null.</param>
/// <param name="Cancelled">True when the user declined (for example a UAC prompt); not an error to report loudly.</param>
public sealed record LaunchResult(bool Started, string? Error = null, bool Cancelled = false)
{
    /// <summary>A started launch.</summary>
    public static LaunchResult Ok { get; } = new(true);
}

/// <summary>The default app for a file type.</summary>
/// <param name="Name">Friendly name such as "Notepad".</param>
/// <param name="Executable">Path of the executable, or null for packaged apps.</param>
/// <param name="ProgId">The ProgID of the association, or null.</param>
public sealed record DefaultApp(string Name, string? Executable, string? ProgId);

/// <summary>Opens files and folders the way Explorer does (spec §6.2, §8.5, §15.6). Nothing here blocks the caller's thread.</summary>
public interface IFileLauncher
{
    /// <summary>Opens with the default app (double-click, Enter).</summary>
    Task<LaunchResult> OpenAsync(LocationPath path, CancellationToken cancellationToken);

    /// <summary>Opens elevated ("Run as administrator"). Only that program is elevated, never Leviathan (spec §15.6).</summary>
    Task<LaunchResult> RunAsAdministratorAsync(LocationPath path, CancellationToken cancellationToken);

    /// <summary>Opens <paramref name="path"/> with the program at <paramref name="executable"/>.</summary>
    Task<LaunchResult> OpenWithAsync(LocationPath path, string executable, CancellationToken cancellationToken);

    /// <summary>Shows the system "How do you want to open this file?" dialog.</summary>
    Task<LaunchResult> ChooseAppAsync(LocationPath path, CancellationToken cancellationToken);

    /// <summary>Opens a terminal in <paramref name="folder"/>.</summary>
    Task<LaunchResult> OpenTerminalAsync(LocationPath folder, TerminalKind kind, CancellationToken cancellationToken);

    /// <summary>Shows the Windows Properties dialog (Alt+Enter).</summary>
    Task<LaunchResult> ShowPropertiesAsync(LocationPath path, CancellationToken cancellationToken);
}

/// <summary>Looks up file associations. Reads the registry; call it off the UI thread.</summary>
public interface IFileAssociations
{
    /// <summary>The default app for an extension (with or without the dot), or null when none is registered.</summary>
    DefaultApp? GetDefaultApp(string extension);
}
