// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>How the folder is shown: layout, zoom, sort, group, filter, hidden items, columns, and the side panels (spec §6.3, §8.7).</summary>
internal sealed class ViewCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var window = Scope.Window;
        var shell = Scope.Shell;
        var viewOptions = Scope.ViewOptions;
        var driveCards = Scope.DriveCards;

        Add(CommandIds.ViewLayout, "Change layout", "View", (c, _) => Run(() =>
        {
            Current()!.SetLayout((Views.ViewLayout)c.Parameter!);
        }), c => Current() is not null && c.Parameter is Views.ViewLayout);

        Add(CommandIds.ViewDetails, "Details layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Details)), _ => Current() is not null, shortcut: "Ctrl+Shift+6");
        Add(CommandIds.ViewList, "List layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.List)), _ => Current() is not null, shortcut: "Ctrl+Shift+5");
        Add(CommandIds.ViewThumbnails, "Thumbnails layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Thumbnails)), _ => Current() is not null, shortcut: "Ctrl+Shift+2");
        Add(CommandIds.ViewTiles, "Tiles layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Tiles)), _ => Current() is not null, shortcut: "Ctrl+Shift+4");
        Add(CommandIds.ViewGallery, "Gallery layout", "View", (_, _) => Run(() => Current()!.SetLayout(Views.ViewLayout.Gallery)), _ => Current() is not null, shortcut: "Ctrl+Shift+1");

        Add(CommandIds.ViewZoom, "Resize thumbnails", "View", (c, _) => Run(() => Current()!.ZoomThumbnails((int)c.Parameter!)),
            c => Current() is { Layout: Views.ViewLayout.Thumbnails } && c.Parameter is int);

        Add(CommandIds.ViewThumbnailSize, "Set thumbnail size", "View", (c, _) => Run(() => Current()!.SetThumbnailSize((int)c.Parameter!)),
            c => Current() is { Layout: Views.ViewLayout.Thumbnails } && c.Parameter is int);

        // Fold or unfold the summary above every pane (spec §8.7); one choice for all panes, remembered.
        Add(CommandIds.DriveCardToggle, "Fold or unfold the drive and folder summary", "View",
            (_, _) => Run(() => driveCards!.SetCollapsed(!driveCards.IsCollapsed)), _ => driveCards is not null);

        // Sort, group, filter (spec §6.3).
        Add(CommandIds.SortBy, "Sort by", "View", (c, _) => Run(() => Current()!.SetSort(c.Target!)),
            c => Current() is not null && Folders.FileColumns.TryGet(c.Target ?? string.Empty, out _));
        Add(CommandIds.SortDirection, "Sort direction", "View", (c, _) => Run(() =>
        {
            var tab = Current()!;
            tab.SetSort(tab.Items.Sort[0].Column, c.Target!.Equals("desc", StringComparison.OrdinalIgnoreCase) ? Views.SortDirection.Descending : Views.SortDirection.Ascending);
        }), c => Current() is not null && c.Target is not null && (c.Target.Equals("asc", StringComparison.OrdinalIgnoreCase) || c.Target.Equals("desc", StringComparison.OrdinalIgnoreCase)));
        Add(CommandIds.GroupBy, "Group by", "View", (c, _) => Run(() => Current()!.SetGroupBy(string.IsNullOrEmpty(c.Target) ? null : c.Target)),
            c => Current() is not null && (string.IsNullOrEmpty(c.Target) || Folders.FileColumns.TryGet(c.Target, out var column) && column.Group is not null));
        Add(CommandIds.FolderPlacement, "Folders position", "View", (c, _) => Run(() => Current()!.SetFolderPlacement(Enum.Parse<Views.FolderPlacement>(c.Target!, ignoreCase: true))),
            c => Current() is not null && Enum.TryParse<Views.FolderPlacement>(c.Target, ignoreCase: true, out _));
        Add(CommandIds.FilterSet, "Filter this folder", "View", (c, _) => Run(() => Current()!.SetFilter(c.Target)), _ => Current() is not null);
        Add(CommandIds.FilterFocus, "Focus filter box", "View", (_, _) => Run(shell.FocusFilterBox), shortcut: "Ctrl+Shift+F");
        Add(CommandIds.ViewHidden, "Show hidden items", "View", async (_, ct) =>
        {
            viewOptions!.ShowHidden = !viewOptions.ShowHidden;
            await Current()!.ReloadAsync(ct);
        }, _ => viewOptions is not null && Current() is not null, shortcut: "Ctrl+H");

        Add(CommandIds.ViewColumns, "Choose columns", "View", (c, _) => Run(() =>
        {
            var choice = (Folders.ColumnChoice)c.Parameter!;
            Current()!.ApplyColumns(choice.Ids, choice.ForAllOfType);
        }), c => Current() is not null && c.Parameter is Folders.ColumnChoice);
        Add(CommandIds.SettingsOpen, "Settings", "View", (c, _) => Run(() => window.SettingsSheet.Toggle(c.Parameter as string)), shortcut: "Ctrl+,");
        Add(CommandIds.HubToggle, "Status hub", "View", (c, _) => Run(() => window.Hub.Toggle(c.Parameter as Hub.HubTab?)), shortcut: "Ctrl+Shift+O");
        Add(CommandIds.ViewSidebar, "Sidebar", "View", (_, _) => Run(() => window.SidebarCollapsed = !window.SidebarCollapsed), shortcut: "Ctrl+B");
        Add(CommandIds.PreviewPane, "Preview pane", "View", (_, _) => Run(() => window.ShowPreview = !window.ShowPreview), shortcut: "Alt+P");
        Add(CommandIds.PreviewQuick, "Quick preview", "View", (_, _) => Run(shell.ToggleQuickPreview), shortcut: "Space");
        Add(CommandIds.PreviewVideoSmall, "Preview video: small", "View", (_, _) => Run(() => window.SetVideoSize(VideoSize.Small)));
        Add(CommandIds.PreviewVideoMedium, "Preview video: medium", "View", (_, _) => Run(() => window.SetVideoSize(VideoSize.Medium)));
        Add(CommandIds.PreviewVideoLarge, "Preview video: large", "View", (_, _) => Run(() => window.SetVideoSize(VideoSize.Large)));
    }
}
