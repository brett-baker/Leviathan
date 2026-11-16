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
    Sidebar.FavoritesList? Favorites,
    NewMenu.NewItemService? NewItems,
    Views.ViewOptions? ViewOptions,
    Folders.RenameService? Renamer,
    Transfers.ITransferService? TransferService,
    Drives.DriveCardPreferences? DriveCards,
    Transfers.IDeleteService? Deleter,
    Undo.UndoService? UndoService,
    Transfers.IFileClipboard? Clipboard,
    Sessions.ProfileService? Profiles,
    Archives.IArchiveService? ArchiveService,
    Func<TerminalKind>? Terminal,
    IQuickTargets? QuickTargetStore)
{
    /// <summary>The window's tab strip.</summary>
    public TabStripViewModel Strip => Window.Tabs;
}
