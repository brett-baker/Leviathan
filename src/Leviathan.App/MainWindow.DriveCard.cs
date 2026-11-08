using Leviathan.Core.Model;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Drives;
using Leviathan.ViewModels.StatusBar;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.App;

/// <summary>
/// The summary above each pane (spec §8.7): the drive or share the folder is on, plus what Leviathan has counted in the folder. In a
/// split every pane has its own, as wide as the pane it describes, and folding one folds both so the two look alike.
/// </summary>
public sealed partial class MainWindow
{
    private void InitHeaders()
    {
        Header1.TerminalRequested += (_, _) => RunForPane(0, CommandIds.TerminalHere);
        Header2.TerminalRequested += (_, _) => RunForPane(1, CommandIds.TerminalHere);
        Header1.PropertiesRequested += (_, _) => OnHeaderProperties(0);
        Header2.PropertiesRequested += (_, _) => OnHeaderProperties(1);
        Header1.FoldToggled += (_, _) => Run(CommandIds.DriveCardToggle);
        Header2.FoldToggled += (_, _) => Run(CommandIds.DriveCardToggle);
    }

    // The buttons of a header act on its own pane, whichever has the focus: the pane takes the focus first.
    private void RunForPane(int pane, ViewModels.Commands.CommandId command)
    {
        OnPaneFocused(pane);
        Run(command);
    }

    private void OnHeaderProperties(int pane)
    {
        OnPaneFocused(pane);
        if (PaneOf(pane)?.History.Current is { } folder)
        {
            var entry = new FileEntry(folder.Name, folder, FileEntryKind.Folder, 0, DateTimeOffset.Now, DateTimeOffset.Now, FileAttributes.Directory);
            Run(CommandIds.Properties, folder.Value, entry);
        }
    }

    private TabViewModel? PaneOf(int pane) => pane == 1 ? _viewModel.Tabs.Selected?.Secondary : _viewModel.Tabs.Selected;

    /// <summary>Fills the header of each pane from the drive its folder is on, and asks for the folder to be counted.</summary>
    private void UpdateHeaders()
    {
        UpdateHeader(Header1, PaneOf(0));
        UpdateHeader(Header2, PaneOf(1));
    }

    private void UpdateHeader(PaneHeader header, TabViewModel? pane)
    {
        var folder = pane?.History.Current;
        var drive = folder is null ? null : StatusBarText.DriveOf(folder, _drives);
        if (pane is null || folder is null || drive is null)
        {
            header.Clear();
            return;
        }

        var folded = App.Host.DriveCards.IsCollapsed;
        var counted = string.Empty;
        if (!folded)
        {
            // The folder's own count, once known; a folder that can be counted is asked for in the background (spec 12.7).
            counted = pane.Sizes.DetailFor(folder);
            if (counted.Length > 0)
            {
                counted = (folder.IsRoot ? "Counted: " : folder.Name + ": ") + counted;
            }

            pane.Sizes.RequestNow(new FileEntry(folder.Name, folder, FileEntryKind.Folder, 0, DateTimeOffset.Now, DateTimeOffset.Now, FileAttributes.Directory));
        }

        header.Show(folder.ToWin32Path(), DriveSummary.From(drive), counted, folded, _commands.CanExecute(CommandIds.TerminalHere, new CommandContext(folder.Value)) && folder.Kind == Core.Paths.LocationKind.Drive);
    }
}
