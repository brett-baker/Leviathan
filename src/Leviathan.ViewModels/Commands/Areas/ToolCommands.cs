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
        Add(CommandIds.SyncFolders, "Compare and sync folders...", "Files", (_, _) => Run(() =>
        {
            var tab = window.Tabs.Selected;
            var here = tab?.Focused.History.Current;
            var other = tab?.Other?.History.Current;
            static LocationPath? Usable(LocationPath? p) => p is { Kind: LocationKind.Drive or LocationKind.Unc } ? p : null;
            shell.OpenSync(Usable(here), Usable(other));
        }), _ => Current() is not null);

        // A contact sheet of the video the command was run on, or of the videos in the selection (spec §10.4).
        static bool IsVideo(FileEntry e) => e.Kind == FileEntryKind.File && Leviathan.Media.MediaFileKinds.IsVideo(e.Extension);
        Add(CommandIds.ContactSheet, "Contact sheet", "Media", (c, _) => Run(() =>
        {
            IReadOnlyList<FileEntry> videos = c.Parameter is FileEntry one ? (IsVideo(one) ? [one] : []) : [.. Current()!.Selection.SelectedEntries.Where(IsVideo)];
            if (videos.Count == 1)
            {
                shell.OpenContactSheet(videos[0]);
            }
            else if (videos.Count > 1)
            {
                shell.ExportContactSheets(videos);
            }
        }), c => Current() is not null && (c.Parameter is FileEntry entry ? IsVideo(entry) : Current()!.Selection.SelectedEntries.Any(IsVideo)));

        Add(CommandIds.SearchFocus, "Search", "Tools", (_, _) => Run(shell.FocusSearchBox), shortcut: "Ctrl+F");
        Add(CommandIds.SearchRun, "Search this folder and below", "Tools", async (c, ct) =>
        {
            var plan = Searches.SearchStarter.Plan(c.Target, Current()!.History.Current, viewOptions?.ShowHidden == true);
            if (plan.Results is null)
            {
                shell.ShowMessage(plan.Message ?? "That search could not be started.");
                return;
            }

            await Current()!.NavigateAsync(plan.Results, ct);
        }, _ => Current() is not null);

        // Alt+T, not the spec's plain T: a bare letter is type-ahead in the file list and would be eaten in the filter and address boxes.
        Add(CommandIds.TriageFromHere, "Triage mode from here", "Media", (_, _) => Run(shell.StartTriage), _ => Current() is not null, shortcut: "Alt+T");
        Add(CommandIds.TriageInbox, "Triage an inbox…", "Media", (_, _) => Run(shell.StartInboxTriage), _ => Current() is not null, shortcut: "Alt+Shift+T");
        Add(CommandIds.FindDuplicates, "Find duplicates…", "Tools", (_, _) => Run(shell.FindDuplicates), _ => Current() is not null, shortcut: "Alt+Shift+D");
        Add(CommandIds.FindDuplicateFiles, "Find duplicate files by contents…", "Tools", (_, _) => Run(shell.FindDuplicateFiles), _ => Current() is not null, shortcut: "Alt+Shift+F");
        Add(CommandIds.SpaceMap, "Space map", "Tools", (c, _) => Run(() =>
        {
            var tab = Current()!;
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
        }), _ => Current() is not null, shortcut: "Ctrl+Shift+M");
        Add(CommandIds.SearchIndexToggle, "Index this folder for search (or stop)", "Tools", (_, _) => Run(() =>
        {
            var here = Current()!.History.Current;
            shell.ShowMessage(shell.ToggleSearchIndex(here)
                ? "This folder is now kept in the search index. It is listed in the background, a little at a time; see Background in the status hub."
                : "This folder is no longer kept in the search index.");
        }), _ => Current() is { } tab && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc);
        Add(CommandIds.SearchSave, "Save this search to Favorites", "Tools", (_, _) => Run(() =>
        {
            var here = Current()!.History.Current;
            if (favorites!.Contains(here))
            {
                shell.ShowMessage("This search is already in Favorites.");
                return;
            }

            favorites.Toggle(here);
            shell.ShowMessage("Search saved to Favorites. It runs again each time you open it; right-click it to rename it.");
        }), _ => favorites is not null && Current() is { } tab && Search.SearchLocations.IsSearchLocation(tab.History.Current));

        Add(CommandIds.AppDiagnostics, "Diagnostics", "App", (_, _) => Run(shell.OpenDiagnostics));
    }
}
