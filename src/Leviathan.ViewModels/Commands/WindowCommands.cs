// View models are UI-thread affine: everything a control can observe is changed on the thread that started the command, so the
// awaits in the command areas (Areas/) deliberately resume on the caller's context (crash signature 52a0ec04c34d: RPC_E_WRONG_THREAD from a
// continuation that ran on the thread pool). UiThreadAffinityTests guards this.

using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Diagnostics;
using Leviathan.ViewModels.Commands.Areas;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands;

/// <summary>
/// Registers the commands of one window against its tabs (build plan, Phase 1: every user action is a command). The UI
/// buttons, menus, shortcuts and, later, the command palette all call these, so the action trail sees everything the user does.
/// </summary>
public static class WindowCommands
{
    /// <summary>Creates the registry for <paramref name="window"/>.</summary>
    public static CommandRegistry Create(
        ActionTrail trail,
        WindowViewModel window,
        WindowManager windows,
        AddressResolver resolver,
        IFileLauncher launcher,
        IShellActions shell,
        Func<LocationPath> home,
        WindowServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(home);

        var scope = new CommandScope(new CommandRegistry(trail), window, windows, resolver, launcher, shell, home, services ?? new WindowServices());

        // One area per file, in Areas/. Each registers its own commands; none depends on another's.
        new TabCommands(scope).Register();
        new TabGroupCommands(scope).Register();
        new ProfileCommands(scope).Register();
        new NavigationCommands(scope).Register();
        new PaneCommands(scope).Register();
        new ViewCommands(scope).Register();
        new SelectionCommands(scope).Register();
        new FileCommands(scope).Register();
        new EditCommands(scope).Register();
        new ClipboardCommands(scope).Register();
        new ArchiveCommands(scope).Register();
        new ToolCommands(scope).Register();
        new FavoriteCommands(scope).Register();

        return scope.Registry;
    }

    /// <summary>The key bindings out of the box (spec §17.3). Users change them in the settings file.</summary>
    public static KeyMap DefaultKeyMap()
    {
        var map = new KeyMap();
        foreach (var (gesture, command) in new (string, CommandId)[]
        {
            ("Ctrl+T", CommandIds.TabNew),
            ("Ctrl+W", CommandIds.TabClose),
            ("Ctrl+Shift+T", CommandIds.TabReopen),
            ("Ctrl+Shift+K", CommandIds.TabDuplicate),
            ("Ctrl+Tab", CommandIds.TabNext),
            ("Ctrl+Shift+Tab", CommandIds.TabPrevious),
            ("Ctrl+N", CommandIds.WindowNew),
            ("Alt+Left", CommandIds.NavBack),
            ("Alt+Right", CommandIds.NavForward),
            ("Alt+Up", CommandIds.NavUp),
            ("F5", CommandIds.NavReload),
            ("Ctrl+R", CommandIds.NavReload),
            ("Ctrl+L", CommandIds.AddressFocus),
            ("Alt+D", CommandIds.AddressFocus),
            ("Alt+T", CommandIds.TriageFromHere),
            ("Alt+Shift+T", CommandIds.TriageInbox),
            ("Alt+Shift+D", CommandIds.FindDuplicates),
            ("Alt+Shift+F", CommandIds.FindDuplicateFiles),
            ("Ctrl+Shift+PageUp", CommandIds.TabMoveLeft),
            ("Ctrl+Shift+PageDown", CommandIds.TabMoveRight),
            ("Ctrl+D", CommandIds.FavoriteToggle),
            ("Ctrl+Alt+T", CommandIds.TerminalHere),
            ("Ctrl+Alt+1", QuickTargets.MoveId(1)), ("Ctrl+Alt+2", QuickTargets.MoveId(2)), ("Ctrl+Alt+3", QuickTargets.MoveId(3)),
            ("Ctrl+Alt+4", QuickTargets.MoveId(4)), ("Ctrl+Alt+5", QuickTargets.MoveId(5)), ("Ctrl+Alt+6", QuickTargets.MoveId(6)),
            ("Ctrl+Alt+7", QuickTargets.MoveId(7)), ("Ctrl+Alt+8", QuickTargets.MoveId(8)), ("Ctrl+Alt+9", QuickTargets.MoveId(9)),
            ("Ctrl+Alt+Shift+1", QuickTargets.CopyId(1)), ("Ctrl+Alt+Shift+2", QuickTargets.CopyId(2)), ("Ctrl+Alt+Shift+3", QuickTargets.CopyId(3)),
            ("Ctrl+Alt+Shift+4", QuickTargets.CopyId(4)), ("Ctrl+Alt+Shift+5", QuickTargets.CopyId(5)), ("Ctrl+Alt+Shift+6", QuickTargets.CopyId(6)),
            ("Ctrl+Alt+Shift+7", QuickTargets.CopyId(7)), ("Ctrl+Alt+Shift+8", QuickTargets.CopyId(8)), ("Ctrl+Alt+Shift+9", QuickTargets.CopyId(9)),
            ("Ctrl+Shift+P", CommandIds.PaletteCommands),
            ("Ctrl+P", CommandIds.PaletteGoTo),
            ("Ctrl+A", CommandIds.SelectAll),
            ("Ctrl+I", CommandIds.SelectInvert),
            ("Ctrl+Shift+A", CommandIds.SelectNone),
            ("Ctrl+H", CommandIds.ViewHidden),
            ("Alt+Enter", CommandIds.Properties),
            ("Ctrl+Shift+N", CommandIds.NewFolder),
            ("Ctrl+Shift+F", CommandIds.FilterFocus),
            ("Ctrl+F", CommandIds.SearchFocus),
            ("Ctrl+Shift+M", CommandIds.SpaceMap),
            ("Del", CommandIds.EditDelete),
            ("Shift+Del", CommandIds.EditDeletePermanent),
            ("Ctrl+X", CommandIds.EditCut),
            ("Ctrl+C", CommandIds.EditCopy),
            ("Ctrl+V", CommandIds.EditPaste),
            ("Ctrl+Z", CommandIds.EditUndo),
            ("Ctrl+Y", CommandIds.EditRedo),
            ("Ctrl+Shift+Z", CommandIds.EditRedo),
            ("Alt+P", CommandIds.PreviewPane),
            (@"Ctrl+\", CommandIds.PaneSideBySide),
            (@"Ctrl+Shift+\", CommandIds.PaneTopBottom),
            ("Ctrl+B", CommandIds.ViewSidebar),
            ("Ctrl+Shift+O", CommandIds.HubToggle),
            ("Ctrl+,", CommandIds.SettingsOpen),
            ("F2", CommandIds.EditRename),
            ("Shift+F2", CommandIds.BulkRename),
            ("Ctrl+Shift+6", CommandIds.ViewDetails),
            ("Ctrl+Shift+5", CommandIds.ViewList),
            ("Ctrl+Shift+2", CommandIds.ViewThumbnails),
            ("Ctrl+Shift+4", CommandIds.ViewTiles),
            ("Ctrl+Shift+1", CommandIds.ViewGallery),
            ("Ctrl+Shift+S", CommandIds.PaneSwap),
            ("Alt+=", CommandIds.PaneSameFolder),
        })
        {
            map.TryBind(new KeyBinding(KeyGesture.Parse(gesture), command));
        }

        // F5 and F6 mean reload in one pane and copy or move to the other pane while split; the split meaning wins only then (spec §17.3).
        map.TryBind(new KeyBinding(KeyGesture.Parse("F5"), CommandIds.PaneCopyToOther, KeyScope.SplitOnly));
        map.TryBind(new KeyBinding(KeyGesture.Parse("F6"), CommandIds.PaneMoveToOther, KeyScope.SplitOnly));

        return map;
    }
}
