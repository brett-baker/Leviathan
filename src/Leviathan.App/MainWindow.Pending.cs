using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Settings;

namespace Leviathan.App;

/// <summary>Showing a delete, move or copy in the file lists as soon as it is queued (the "UI updates: Immediate" setting).</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// The worker took a job: with Immediate updates, hide what a delete or move takes away and show a dimmed copy of what a move or
    /// copy brings (spec §3.2, §12.5). Rows come from the lists already on screen; an item no list shows is simply not previewed in
    /// its destination. When the job ends the folders are read again, which also undoes all of this for a job that failed.
    /// </summary>
    private void OnTransferSubmitted(object? sender, TransferSubmitted submitted) => OnUi(() =>
    {
        var pending = App.Host.Pending;
        if (App.Host.Settings.Current.Files.UiUpdates != UiUpdateMode.Immediate || pending.Knows(submitted.Job))
        {
            return;
        }

        var hide = submitted.Kind is Leviathan.Contracts.JobKind.Delete or Leviathan.Contracts.JobKind.Move ? submitted.Sources : [];
        var ghosts = new List<FileEntry>();
        if (submitted.Destination is { } destination && submitted.Kind is Leviathan.Contracts.JobKind.Move or Leviathan.Contracts.JobKind.Copy)
        {
            var wanted = submitted.Sources.ToHashSet();
            var found = new HashSet<LocationPath>();
            foreach (var pane in ShownPanes())
            {
                if (pane.Folder.Location is not { } here || !submitted.Sources.Any(s => s.Parent == here))
                {
                    continue;
                }

                foreach (var entry in pane.Folder.Entries.ToArray())
                {
                    if (wanted.Contains(entry.Location) && found.Add(entry.Location))
                    {
                        ghosts.Add(entry with { Location = destination.Combine(entry.Name), Name = entry.Name });
                    }
                }
            }
        }

        pending.Begin(submitted.Job, hide, submitted.Destination, ghosts);
    });

    private IEnumerable<ViewModels.Tabs.TabViewModel> ShownPanes()
    {
        foreach (var tab in _viewModel.Tabs.Tabs)
        {
            yield return tab;
            if (tab.Secondary is { } second)
            {
                yield return second;
            }
        }
    }
}
