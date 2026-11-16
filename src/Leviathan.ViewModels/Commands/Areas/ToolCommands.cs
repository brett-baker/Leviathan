// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Search, the space map, duplicates, triage, contact sheets and compare and sync (spec §10.4, §13, §14, §15.5).</summary>
internal sealed class ToolCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var window = Scope.Window;
        var shell = Scope.Shell;
        var favorites = Scope.Favorites;
        var viewOptions = Scope.ViewOptions;

        // Compare and sync (spec §15.5): this folder against the other pane of a split tab, else the person picks the second one.
        AddAction(CommandIds.SyncFolders, "Compare and sync folders...", "Files", () =>
        {
            var tab = window.Tabs.Selected;
            var here = tab?.Focused.History.Current;
            var other = tab?.Other?.History.Current;
            static LocationPath? Usable(LocationPath? p) => p is { Kind: LocationKind.Drive or LocationKind.Unc } ? p : null;
            shell.OpenSync(Usable(here), Usable(other));
        }, NeedsTab);

        // A contact sheet of the video the command was run on, or of the videos in the selection (spec §10.4).
        static bool IsVideo(FileEntry e) => e.Kind == FileEntryKind.File && Leviathan.Media.MediaFileKinds.IsVideo(e.Extension);
        AddOnCurrent(CommandIds.ContactSheet, "Contact sheet", "Media", (tab, c) =>
        {
            IReadOnlyList<FileEntry> videos = c.Parameter is FileEntry one ? (IsVideo(one) ? [one] : []) : [.. tab.Selection.SelectedEntries.Where(IsVideo)];
            if (videos.Count == 1)
            {
                shell.OpenContactSheet(videos[0]);
            }
            else if (videos.Count > 1)
            {
                shell.ExportContactSheets(videos);
            }
        }, (tab, c) => c.Parameter is FileEntry entry ? IsVideo(entry) : tab.Selection.SelectedEntries.Any(IsVideo));

        AddAction(CommandIds.SearchFocus, "Search", "Tools", shell.FocusSearchBox, shortcut: "Ctrl+F");
        AddOnCurrent(CommandIds.SearchRun, "Search this folder and below", "Tools", async (tab, c, ct) =>
        {
            var plan = Searches.SearchStarter.Plan(c.Target, tab.History.Current, viewOptions?.ShowHidden == true);
            if (plan.Results is null)
            {
                shell.ShowMessage(plan.Message ?? "That search could not be started.");
                return;
            }

            await tab.NavigateAsync(plan.Results, ct);
        });

        // Alt+T, not the spec's plain T: a bare letter is type-ahead in the file list and would be eaten in the filter and address boxes.
        AddAction(CommandIds.TriageFromHere, "Triage mode from here", "Media", shell.StartTriage, NeedsTab, shortcut: "Alt+T");
        AddAction(CommandIds.TriageInbox, "Triage an inbox…", "Media", shell.StartInboxTriage, NeedsTab, shortcut: "Alt+Shift+T");
        AddAction(CommandIds.FindDuplicates, "Find duplicates…", "Tools", shell.FindDuplicates, NeedsTab, shortcut: "Alt+Shift+D");
        AddAction(CommandIds.FindDuplicateFiles, "Find duplicate files by contents…", "Tools", shell.FindDuplicateFiles, NeedsTab, shortcut: "Alt+Shift+F");
        AddOnCurrent(CommandIds.SpaceMap, "Space map", "Tools", (tab, c) =>
        {
            LocationPath? folder = null;
            if (LocationPath.TryParse(c.Target, out var named))
            {
                folder = named;
            }
            else if (tab.Selection.SelectedEntries is { Count: 1 } one && one[0].Kind == FileEntryKind.Folder)
            {
                folder = one[0].Location;
            }
            else
            {
                folder = tab.History.Current;
            }

            if (folder.Kind is LocationKind.Drive or LocationKind.Unc && !(folder.Kind == LocationKind.Unc && folder.Share is null))
            {
                shell.OpenSpaceMap(folder);
            }
            else
            {
                shell.ShowMessage("Open a folder, a drive or a share first: the space map shows what is in it.");
            }
        }, shortcut: "Ctrl+Shift+M");
        AddOnCurrent(CommandIds.SearchIndexToggle, "Index this folder for search (or stop)", "Tools", (tab, _) =>
        {
            var here = tab.History.Current;
            shell.ShowMessage(shell.ToggleSearchIndex(here)
                ? "This folder is now kept in the search index. It is listed in the background, a little at a time; see Background in the status hub."
                : "This folder is no longer kept in the search index.");
        }, (tab, _) => tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc);
        AddOnCurrent(CommandIds.SearchSave, "Save this search to Favorites", "Tools", (tab, _) =>
        {
            var here = tab.History.Current;
            if (favorites!.Contains(here))
            {
                shell.ShowMessage("This search is already in Favorites.");
                return;
            }

            favorites.Toggle(here);
            shell.ShowMessage("Search saved to Favorites. It runs again each time you open it; right-click it to rename it.");
        }, (tab, _) => favorites is not null && Search.SearchLocations.IsSearchLocation(tab.History.Current));

        AddAction(CommandIds.AppDiagnostics, "Diagnostics", "App", shell.OpenDiagnostics);
    }
}
