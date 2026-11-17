using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Tabs;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>
/// Everything the command areas share: the registry they register into, the window and its strip, and the services a window was
/// built with. Services that may be absent are null here, and their commands are disabled (the window is built without them in tests).
/// </summary>
internal sealed record CommandScope(
    CommandRegistry Registry,
    WindowViewModel Window,
    WindowManager Windows,
    AddressResolver Resolver,
    IFileLauncher Launcher,
    IShellActions Shell,
    Func<LocationPath> Home,
    WindowServices Services)
{
    /// <summary>The favourites list, or null.</summary>
    public Sidebar.FavoritesList? Favorites => Services.Favorites;
    /// <summary>The New menu, or null.</summary>
    public NewMenu.NewItemService? NewItems => Services.NewItems;
    /// <summary>The view options, or null.</summary>
    public Views.ViewOptions? ViewOptions => Services.ViewOptions;
    /// <summary>Inline rename, or null.</summary>
    public Folders.RenameService? Renamer => Services.Renamer;
    /// <summary>Copy and move, or null.</summary>
    public Transfers.ITransferService? TransferService => Services.Transfers;
    /// <summary>The drive card preference, or null.</summary>
    public Drives.DriveCardPreferences? DriveCards => Services.DriveCards;
    /// <summary>Delete, or null.</summary>
    public Transfers.IDeleteService? Deleter => Services.Deleter;
    /// <summary>Undo and redo, or null.</summary>
    public Undo.UndoService? UndoService => Services.Undo;
    /// <summary>The file clipboard, or null.</summary>
    public Transfers.IFileClipboard? Clipboard => Services.Clipboard;
    /// <summary>Folder profiles, or null.</summary>
    public Sessions.ProfileService? Profiles => Services.Profiles;
    /// <summary>Archives, or null.</summary>
    public Archives.IArchiveService? ArchiveService => Services.Archives;
    /// <summary>The terminal choice, or null.</summary>
    public Func<TerminalKind>? Terminal => Services.Terminal;
    /// <summary>Quick targets, or null.</summary>
    public IQuickTargets? QuickTargetStore => Services.QuickTargets;

    /// <summary>The window's tab strip.</summary>
    public TabStripViewModel Strip => Window.Tabs;
}
