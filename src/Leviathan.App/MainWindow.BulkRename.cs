using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Rename;
using Leviathan.ViewModels.Rename;

namespace Leviathan.App;

/// <summary>Bulk rename (spec §15.3): F2 with several items selected, or Shift+F2.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Opens the bulk-rename dialog for the selected items of the folder shown (folders on this PC and on Windows shares). The presets
    /// are read on the thread pool before the window opens; the renames themselves are the worker's journaled restore job, and the
    /// whole batch is one entry in the undo history. Afterwards the folder is reloaded with the renamed items selected.
    /// </summary>
    public void BulkRename() => OnUi(async () =>
    {
        var tab = _bound;
        if (tab is null || tab.History.Current is not { Kind: LocationKind.Drive or LocationKind.Unc } || tab.Selection.SelectedEntries.Count == 0)
        {
            return;
        }

        var items = tab.Selection.SelectedEntries
            .Select(e => new RenameItem(e.Location.ToWin32Path(), e.Kind != FileEntryKind.File, e.Modified.UtcDateTime))
            .ToList();
        var host = App.Host;
        var presets = await Task.Run(() => new RenamePresets(new JsonRenamePresetStore()));
        var model = new BulkRenameModel(items, SiblingNames);
        new BulkRenameWindow(
            model,
            new WorkerUndoActions(() => host.WorkerConnection, () => host.Transfers.KeepOwnTrashSource()),
            moves => host.Undo.RecordBulkRename(moves),
            presets,
            new MediaRenameSource(() => host.MediaHost),
            result =>
            {
                OnUi(async () =>
                {
                    MessageText.Text = result.Renamed > 0
                        ? $"Renamed {result.Renamed:N0} item{(result.Renamed == 1 ? string.Empty : "s")}." + (result.Problems.Count > 0 ? $" {result.Problems.Count:N0} could not be renamed: {result.Problems[0]}" : string.Empty)
                        : result.Problems.Count > 0 ? result.Problems[0] : "No names changed.";
                    if (result.Renamed > 0 && ReferenceEquals(_bound, tab))
                    {
                        await tab.ReloadAndSelectAsync([.. result.Touched.Select(LocationPath.Parse)], CancellationToken.None);
                    }
                });
            }).Activate();
    });

    // The names in a folder, so a new name that is already taken is caught in the preview. Read once per folder, off the UI thread.
    private static HashSet<string> SiblingNames(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }
}
