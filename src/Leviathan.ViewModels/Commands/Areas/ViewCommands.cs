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

        foreach (var (id, title, layout, key) in new[]
        {
            (CommandIds.ViewDetails, "Details layout", Views.ViewLayout.Details, "Ctrl+Shift+6"),
            (CommandIds.ViewList, "List layout", Views.ViewLayout.List, "Ctrl+Shift+5"),
            (CommandIds.ViewThumbnails, "Thumbnails layout", Views.ViewLayout.Thumbnails, "Ctrl+Shift+2"),
            (CommandIds.ViewTiles, "Tiles layout", Views.ViewLayout.Tiles, "Ctrl+Shift+4"),
            (CommandIds.ViewGallery, "Gallery layout", Views.ViewLayout.Gallery, "Ctrl+Shift+1"),
        })
        {
            AddOnCurrent(id, title, "View", (tab, _) => tab.SetLayout(layout), shortcut: key);
        }

        AddOnCurrentWith<Views.ViewLayout>(CommandIds.ViewLayout, "Change layout", "View", (tab, layout) => tab.SetLayout(layout));
        AddOnCurrentWith<int>(CommandIds.ViewZoom, "Resize thumbnails", "View", (tab, steps) => tab.ZoomThumbnails(steps), (tab, _) => tab.Layout == Views.ViewLayout.Thumbnails);
        AddOnCurrentWith<int>(CommandIds.ViewThumbnailSize, "Set thumbnail size", "View", (tab, size) => tab.SetThumbnailSize(size), (tab, _) => tab.Layout == Views.ViewLayout.Thumbnails);

        // Fold or unfold the summary above every pane (spec §8.7); one choice for all panes, remembered.
        AddAction(CommandIds.DriveCardToggle, "Fold or unfold the drive and folder summary", "View",
            () => driveCards!.SetCollapsed(!driveCards.IsCollapsed), _ => driveCards is not null);

        // Sort, group, filter (spec §6.3).
        AddOnCurrent(CommandIds.SortBy, "Sort by", "View", (tab, c) => tab.SetSort(c.Target!),
            (_, c) => Folders.FileColumns.TryGet(c.Target ?? string.Empty, out var _));
        AddOnCurrent(CommandIds.SortDirection, "Sort direction", "View",
            (tab, c) => tab.SetSort(tab.Items.Sort[0].Column, c.Target!.Equals("desc", StringComparison.OrdinalIgnoreCase) ? Views.SortDirection.Descending : Views.SortDirection.Ascending),
            (_, c) => c.Target is not null && (c.Target.Equals("asc", StringComparison.OrdinalIgnoreCase) || c.Target.Equals("desc", StringComparison.OrdinalIgnoreCase)));
        AddOnCurrent(CommandIds.GroupBy, "Group by", "View", (tab, c) => tab.SetGroupBy(string.IsNullOrEmpty(c.Target) ? null : c.Target),
            (_, c) => string.IsNullOrEmpty(c.Target) || Folders.FileColumns.TryGet(c.Target, out var column) && column.Group is not null);
        AddOnCurrent(CommandIds.FolderPlacement, "Folders position", "View", (tab, c) => tab.SetFolderPlacement(Enum.Parse<Views.FolderPlacement>(c.Target!, ignoreCase: true)),
            (_, c) => Enum.TryParse<Views.FolderPlacement>(c.Target, ignoreCase: true, out var _));
        AddOnCurrent(CommandIds.FilterSet, "Filter this folder", "View", (tab, c) => tab.SetFilter(c.Target));
        AddAction(CommandIds.FilterFocus, "Focus filter box", "View", shell.FocusFilterBox, shortcut: "Ctrl+Shift+F");
        AddOnCurrent(CommandIds.ViewHidden, "Show hidden items", "View", async (tab, _, ct) =>
        {
            viewOptions!.ShowHidden = !viewOptions.ShowHidden;
            await tab.ReloadAsync(ct);
        }, (_, _) => viewOptions is not null, shortcut: "Ctrl+H");

        AddOnCurrentWith<Folders.ColumnChoice>(CommandIds.ViewColumns, "Choose columns", "View", (tab, choice) => tab.ApplyColumns(choice.Ids, choice.ForAllOfType));
        AddAction(CommandIds.SettingsOpen, "Settings", "View", c => window.SettingsSheet.Toggle(c.Parameter as string), shortcut: "Ctrl+,");
        AddAction(CommandIds.HubToggle, "Status hub", "View", c => window.Hub.Toggle(c.Parameter as Hub.HubTab?), shortcut: "Ctrl+Shift+O");
        AddAction(CommandIds.ViewSidebar, "Sidebar", "View", () => window.SidebarCollapsed = !window.SidebarCollapsed, shortcut: "Ctrl+B");
        AddAction(CommandIds.PreviewPane, "Preview pane", "View", () => window.ShowPreview = !window.ShowPreview, shortcut: "Alt+P");
        AddAction(CommandIds.PreviewQuick, "Quick preview", "View", shell.ToggleQuickPreview, shortcut: "Space");
        AddAction(CommandIds.PreviewVideoSmall, "Preview video: small", "View", () => window.SetVideoSize(VideoSize.Small));
        AddAction(CommandIds.PreviewVideoMedium, "Preview video: medium", "View", () => window.SetVideoSize(VideoSize.Medium));
        AddAction(CommandIds.PreviewVideoLarge, "Preview video: large", "View", () => window.SetVideoSize(VideoSize.Large));
    }
}
