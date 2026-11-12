using Leviathan.Core.Paths;
using Leviathan.Interop;
using Leviathan.ViewModels.Duplicates;

namespace Leviathan.App;

/// <summary>Find duplicate files by contents (spec §15.2): the results window for the folder shown, searched with its subfolders.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Alt+Shift+F: opens the duplicate-files window and searches the folder shown and everything under it, in the worker. Folders on this PC
    /// and on Windows shares can be searched; for anything else the window says so instead of opening. Nothing changes until the person
    /// presses Apply in the window.
    /// </summary>
    public void FindDuplicateFiles() => OnUi(() =>
    {
        var here = _bound?.History.Current;
        if (here is not { Kind: LocationKind.Drive or LocationKind.Unc } folder)
        {
            MessageText.Text = "Duplicate files can be searched for in folders on this PC and on Windows shares.";
            return;
        }

        var host = App.Host;
        var cleanup = new DuplicateCleanup(
            new WorkerUndoActions(() => host.WorkerConnection, () => host.Transfers.KeepOwnTrashSource()),
            host.TagStore,
            new FileHardLinker(),
            moves => host.Undo.RecordDelete(moves));
        new DuplicateReviewWindow([folder.ToWin32Path()], new WorkerDuplicateScanner(() => host.WorkerConnection), cleanup, host.Settings.Current.Tags.Catalog().Tags).Activate();
    });
}
