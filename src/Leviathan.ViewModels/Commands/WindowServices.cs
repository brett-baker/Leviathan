using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.Commands;

/// <summary>
/// The optional services a window's commands use. Each one that is null leaves its commands disabled, which is how the window is built in
/// tests (docs/simplify/window-commands.md, item 6). One record means a new service is added here and where it is created, not at every call.
/// </summary>
public sealed record WindowServices
{
    /// <summary>The favourites list.</summary>
    public Sidebar.FavoritesList? Favorites { get; init; }

    /// <summary>The New menu.</summary>
    public NewMenu.NewItemService? NewItems { get; init; }

    /// <summary>The view options (hidden items).</summary>
    public Views.ViewOptions? ViewOptions { get; init; }

    /// <summary>Inline rename.</summary>
    public Folders.RenameService? Renamer { get; init; }

    /// <summary>Copy and move.</summary>
    public Transfers.ITransferService? Transfers { get; init; }

    /// <summary>The drive card fold preference.</summary>
    public Drives.DriveCardPreferences? DriveCards { get; init; }

    /// <summary>Delete.</summary>
    public Transfers.IDeleteService? Deleter { get; init; }

    /// <summary>Undo and redo.</summary>
    public Undo.UndoService? Undo { get; init; }

    /// <summary>Cut, copy and paste of files.</summary>
    public Transfers.IFileClipboard? Clipboard { get; init; }

    /// <summary>Folder profiles.</summary>
    public Sessions.ProfileService? Profiles { get; init; }

    /// <summary>Archives.</summary>
    public Archives.IArchiveService? Archives { get; init; }

    /// <summary>Which terminal to open.</summary>
    public Func<TerminalKind>? Terminal { get; init; }

    /// <summary>Quick move and copy targets.</summary>
    public IQuickTargets? QuickTargets { get; init; }
}
