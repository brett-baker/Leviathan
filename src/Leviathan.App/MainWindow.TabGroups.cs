using System.ComponentModel;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Leviathan.App;

/// <summary>
/// The label chip of a tab group in the strip (spec §4.1): a coloured pill with the group's name, which shows the tab count and
/// folds the group away when clicked. WinUI's <c>TabView</c> has no notion of groups, so the chip is an item of the strip that
/// can be neither closed nor selected (a click on it folds or unfolds the group and the selection goes back to the real tab).
/// </summary>
internal sealed class TabGroupUi
{
    private readonly TextBlock _label = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 8, 1), MinHeight = 20, MinWidth = 20, VerticalAlignment = VerticalAlignment.Center };

    public TabGroupUi(TabGroup group)
    {
        Group = group;
        _pill.Child = _label;
        Item = new TabViewItem { Tag = group, Header = _pill, IsClosable = false, MinWidth = 0, Padding = new Thickness(2, 0, 2, 0) };
    }

    public TabGroup Group { get; }

    public TabViewItem Item { get; }

    /// <summary>Redraws the chip for the group's current name, colour and state, with <paramref name="tabCount"/> tabs in it.</summary>
    public void Refresh(int tabCount)
    {
        var color = TabItemUi.TryParseColor(Group.Color, out var parsed) ? parsed : Colors.Gray;
        _pill.Background = new SolidColorBrush(color);

        // Black or white text, whichever reads better on the group's colour.
        var luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255;
        _label.Foreground = new SolidColorBrush(luminance > 0.6 ? Colors.Black : Colors.White);

        var text = Group.IsCollapsed
            ? (Group.Name.Length == 0 ? tabCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Group.Name} · {tabCount}")
            : Group.Name;
        _label.Text = text;
        _label.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var name = Group.Name.Length == 0 ? "Tab group" : Group.Name;
        ToolTipService.SetToolTip(Item, $"{name}: {tabCount} tab{(tabCount == 1 ? string.Empty : "s")}. Click to {(Group.IsCollapsed ? "show" : "fold away")} the group.");
        AutomationProperties.SetName(Item, $"{name}, {tabCount} tabs, {(Group.IsCollapsed ? "folded" : "open")}");
    }
}

public sealed partial class MainWindow
{
    private readonly Dictionary<TabGroup, TabGroupUi> _groupUi = [];

    /// <summary>What the user last started dragging in this strip (a tab or a chip's group); read once when the strip has reordered.</summary>
    private object? _draggedItem;

    private void OnGroupsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnUi(SyncTabs);

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnUi(SyncTabs);

    /// <summary>The group a strip item stands for: a chip is its group, a tab is the group it is in.</summary>
    private TabGroup? GroupOfItem(object? item) => item switch
    {
        TabViewItem { Tag: TabGroup group } => group,
        TabViewItem { Tag: TabViewModel tab } => _viewModel.Tabs.GroupOf(tab),
        _ => null,
    };

    /// <summary>A click landed on a chip: fold or unfold the group, and put the selection back on the tab that has it.</summary>
    private void HandleChipSelected(TabGroup group)
    {
        Run(CommandIds.TabGroupCollapse, null, group);
        SyncSelectedTab();
    }

    private TabGroupUi EnsureChip(TabGroup group)
    {
        if (_groupUi.TryGetValue(group, out var ui))
        {
            return ui;
        }

        ui = new TabGroupUi(group);
        ui.Item.ContextFlyout = BuildGroupMenu(group);
        group.PropertyChanged += OnGroupPropertyChanged;
        _groupUi[group] = ui;
        return ui;
    }

    private void RemoveGoneChips()
    {
        foreach (var gone in _groupUi.Keys.Except(_viewModel.Tabs.Groups).ToList())
        {
            gone.PropertyChanged -= OnGroupPropertyChanged;
            TabsView.TabItems.Remove(_groupUi[gone].Item);
            _groupUi.Remove(gone);
        }
    }

    // Menus ------------------------------------------------------------------------------------------------------------

    private MenuFlyout BuildGroupMenu(TabGroup group)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var fold = new MenuFlyoutItem { Text = group.IsCollapsed ? "Expand group" : "Collapse group" };
            fold.Click += (_, _) => Run(CommandIds.TabGroupCollapse, null, group);
            var rename = new MenuFlyoutItem { Text = "Rename group…" };
            rename.Click += async (_, _) => await RenameGroupAsync(group);

            var colour = new MenuFlyoutSubItem { Text = "Group colour" };
            foreach (var (title, hex) in TabGroupColors.All)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = title,
                    IsChecked = string.Equals(group.Color, hex, StringComparison.OrdinalIgnoreCase),
                    Icon = new FontIcon { Glyph = "", Foreground = TabItemUi.TryParseColor(hex, out var c) ? new SolidColorBrush(c) : null },
                };
                item.Click += (_, _) => Run(CommandIds.TabGroupColor, hex, group);
                colour.Items.Add(item);
            }

            var newTab = new MenuFlyoutItem { Text = "New tab in group" };
            newTab.Click += (_, _) => Run(CommandIds.TabGroupNewTab, null, group);
            var ungroup = new MenuFlyoutItem { Text = "Ungroup" };
            ungroup.Click += (_, _) => Run(CommandIds.TabGroupUngroup, null, group);
            var close = new MenuFlyoutItem { Text = "Close group" };
            close.Click += (_, _) => Run(CommandIds.TabGroupClose, null, group);
            var toWindow = new MenuFlyoutItem { Text = "Move group to new window" };
            toWindow.Click += (_, _) => Run(CommandIds.TabGroupToWindow, null, group);

            menu.Items.Add(fold);
            menu.Items.Add(rename);
            menu.Items.Add(colour);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(newTab);
            menu.Items.Add(ungroup);
            menu.Items.Add(close);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(toWindow);
        };
        return menu;
    }

    /// <summary>The tab menu's group entries: add to a new group, add to an existing one, remove from the group.</summary>
    private void AddGroupEntries(MenuFlyout menu, TabViewModel tab)
    {
        var strip = _viewModel.Tabs;
        var pinned = tab.IsPinned;
        var newGroup = new MenuFlyoutItem { Text = "Add tab to new group", IsEnabled = !pinned };
        newGroup.Click += async (_, _) =>
        {
            Run(CommandIds.TabGroupNew, string.Empty, tab);
            if (strip.GroupOf(tab) is { } created)
            {
                // The name is asked for right away, as the spec says "name it inline"; leaving it empty is fine.
                await RenameGroupAsync(created);
            }
        };
        menu.Items.Add(newGroup);

        var others = strip.Groups.Where(g => g.Id != tab.GroupId).ToList();
        if (others.Count > 0)
        {
            var existing = new MenuFlyoutSubItem { Text = "Add tab to group", IsEnabled = !pinned };
            foreach (var group in others)
            {
                var item = new MenuFlyoutItem
                {
                    Text = group.Name.Length == 0 ? "Unnamed group" : group.Name,
                    Icon = new FontIcon { Glyph = "", Foreground = TabItemUi.TryParseColor(group.Color, out var c) ? new SolidColorBrush(c) : null },
                };
                var target = group;
                item.Click += (_, _) => Run(CommandIds.TabGroupAdd, null, new TabGroupAdd(tab, target));
                existing.Items.Add(item);
            }

            menu.Items.Add(existing);
        }

        if (tab.GroupId is not null)
        {
            var remove = new MenuFlyoutItem { Text = "Remove from group" };
            remove.Click += (_, _) => Run(CommandIds.TabGroupRemove, null, tab);
            menu.Items.Add(remove);
        }
    }

    private async Task RenameGroupAsync(TabGroup group)
    {
        var box = new TextBox { Text = group.Name, PlaceholderText = "Group name", MaxLength = 40, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = "Name this group",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                dialog.Hide();
                Run(CommandIds.TabGroupRename, box.Text, group);
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Run(CommandIds.TabGroupRename, box.Text, group);
        }
    }

    // Drag and drop: after the strip reordered itself, work out what the user meant ---------------------------------------

    /// <summary>
    /// The strip reordered its own items while the user dragged within it. A chip that moved moves its group; a tab that moved
    /// into the middle of a group joins it, a tab dragged out of its group leaves it, anything else is a plain reorder (spec §4.1).
    /// </summary>
    private void ReconcileStripOrder()
    {
        var shown = TabsView.TabItems.OfType<TabViewItem>()
            .Select(i => i.Tag switch { TabViewModel tab => (StripEntry?)new TabEntry(tab), TabGroup group => new ChipEntry(group), _ => null })
            .OfType<StripEntry>()
            .ToList();

        var dragged = _draggedItem switch
        {
            TabViewModel tab => (StripEntry)new TabEntry(tab),
            TabGroup group => new ChipEntry(group),
            _ => null,
        };
        _draggedItem = null;
        var change = StripReorder.Decide(_viewModel.Tabs, shown, dragged);
        switch (change.Kind)
        {
            case StripChangeKind.MoveGroup:
                Run(CommandIds.TabGroupMove, null, new TabGroupMove(change.Group!, change.Index));
                break;
            case StripChangeKind.AddToGroup:
                Run(CommandIds.TabGroupAdd, null, new TabGroupAdd(change.Tab!, change.Group!, change.Index));
                break;
            case StripChangeKind.LeaveGroupAndMove:
                Run(CommandIds.TabGroupRemove, null, change.Tab!);
                Run(CommandIds.TabMove, change.Tab!.History.Current.Value, new TabMove(change.Tab!, change.Index));
                break;
            case StripChangeKind.MoveTab:
                Run(CommandIds.TabMove, change.Tab!.History.Current.Value, new TabMove(change.Tab!, change.Index));
                break;
        }

        // Whatever happened, the strip shows what the model says.
        SyncTabs();
    }
}
