using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Engine.Supervision;
using Leviathan.Interop;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.ContextMenu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Leviathan.App;

/// <summary>
/// The right-click menu (spec §8.5). Leviathan's own items are built from the command registry and shown at once. The shell's items,
/// the ones 7-Zip, Git and the rest add, come from <c>Leviathan.ShellHost</c> afterwards and are filled in while the menu is open: a
/// slow or crashing extension can delay or lose its own items, never the menu. They go under "More options", or inline when Shift is
/// held, as in Explorer's classic menu.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan ShellOpenLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShellInvokeLimit = TimeSpan.FromSeconds(30);
    private const int MaxSubMenuDepth = 3;

    /// <summary>The shell's part of one open menu: where its items go and what to release when the menu closes.</summary>
    private sealed class ShellSection(IList<MenuFlyoutItemBase> host, MenuFlyoutItemBase placeholder, string workingDirectory)
    {
        public IList<MenuFlyoutItemBase> Host { get; } = host;

        public MenuFlyoutItemBase Placeholder { get; } = placeholder;

        public string WorkingDirectory { get; } = workingDirectory;

        public CancellationTokenSource Cancel { get; } = new();

        public Guid? MenuId { get; set; }

        public bool Closed { get; set; }

        /// <summary>A command was run from this menu. Its menu is then left to the host's idle sweep: closing it would queue behind a dialog the command may have opened.</summary>
        public bool Invoked { get; set; }
    }

    private void OnRowContextRequested(object? sender, RowContextRequest request)
    {
        if (_bound is not { } tab)
        {
            return;
        }

        var entries = RightClickMenu.Build(tab.Selection.SelectedEntries, tab.History.Current, _commands);
        var flyout = new MenuFlyout();
        foreach (var entry in entries)
        {
            flyout.Items.Add(entry.Kind == MenuEntryKind.Separator ? new MenuFlyoutSeparator() : OwnItem(entry));
        }

        if (tab.Selection.SelectedEntries.Any(e => e.Kind == FileEntryKind.File))
        {
            AddTagMenu(flyout);
        }

        // The space map of a folder: the one selected, or the one shown when nothing is (spec §14).
        if (tab.Selection.SelectedEntries is { Count: 1 } single && single[0].Kind == FileEntryKind.Folder)
        {
            flyout.Items.Add(OwnItem(new MenuEntry(MenuEntryKind.Command, "Space map of this folder", CommandIds.SpaceMap, new CommandContext(single[0].Location.Value, null), "Ctrl+Shift+M", true)));
        }
        else if (tab.Selection.SelectedEntries.Count == 0 && tab.History.Current.Kind is LocationKind.Drive or LocationKind.Unc)
        {
            flyout.Items.Add(OwnItem(new MenuEntry(MenuEntryKind.Command, "Space map of this folder", CommandIds.SpaceMap, new CommandContext(tab.History.Current.Value, null), "Ctrl+Shift+M", true)));
        }

        var extended = KeyTranslator.IsDown(VirtualKey.Shift);
        var shellRequest = ShellRequestFor(tab, extended);
        ShellSection? section = null;
        if (shellRequest is not null)
        {
            section = AddShellSection(flyout, extended, tab.History.Current.ToWin32Path());
            flyout.Closed += (_, _) => OnContextMenuClosed(section);
        }

        flyout.ShowAt(request.Target, new FlyoutShowOptions { Position = request.Position });
        if (section is not null)
        {
            _ = LoadShellItemsAsync(section, shellRequest!);
        }
    }

    // "Tag" (spec §11.1): the colour tags of the user's catalog with their number keys, so the keys can be found; each one toggles the tag
    // on the selected files, as the key does, and "Clear tags" is Ctrl+0.
    private void AddTagMenu(MenuFlyout flyout)
    {
        var tags = App.Host.Settings.Current.Tags.Catalog().Tags;
        if (tags.Count == 0)
        {
            return;
        }

        var menu = new MenuFlyoutSubItem { Text = "Tag" };
        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            var key = i + 1 <= ViewModels.Tags.TagCatalog.KeyedTags ? i + 1 : 0;
            var item = new MenuFlyoutItem { Text = tag.Name, KeyboardAcceleratorTextOverride = key == 0 ? string.Empty : key.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var colour = GridRow.TagColour(tag.Colour);
            item.Icon = new FontIcon { Glyph = "", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, colour.R, colour.G, colour.B)) };
            var number = key;
            if (number > 0)
            {
                item.Click += (_, _) => OnTagKey(this, number);
            }
            else
            {
                item.IsEnabled = false; // only the first nine tags have a key
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear tags", KeyboardAcceleratorTextOverride = "Ctrl+0" };
        clear.Click += (_, _) => OnTagKey(this, 0);
        menu.Items.Add(clear);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(menu);
    }

    private MenuFlyoutItem OwnItem(MenuEntry entry)
    {
        var item = new MenuFlyoutItem { Text = entry.Title, IsEnabled = entry.Enabled, KeyboardAcceleratorTextOverride = entry.Shortcut ?? string.Empty };
        item.Click += (_, _) => Run(entry.Command!.Value, entry.Context?.Target, entry.Context?.Parameter);
        return item;
    }

    /// <summary>The request for the shell's items, or null where the shell has none to give (Home, the network list, a drive list).</summary>
    private static ShellMenuRequest? ShellRequestFor(ViewModels.Tabs.TabViewModel tab, bool extended)
    {
        var here = tab.History.Current;
        // A server on its own (\\nas, the list of its shares) is not a folder the shell can build a menu for, and has no Win32 path.
        if (here.Kind is not (LocationKind.Drive or LocationKind.Unc) || (here.Kind == LocationKind.Unc && here.Share is null))
        {
            return null;
        }

        var selected = tab.Selection.SelectedEntries;
        if (selected.Count == 0)
        {
            return new ShellMenuRequest([here.ToWin32Path()], Background: true, extended);
        }

        return selected.All(e => e.Location.Kind is LocationKind.Drive or LocationKind.Unc)
            ? new ShellMenuRequest([.. selected.Select(e => e.Location.ToWin32Path())], Background: false, extended)
            : null;
    }

    private static ShellSection AddShellSection(MenuFlyout flyout, bool inline, string workingDirectory)
    {
        var loading = new MenuFlyoutItem { Text = "Loading shell extensions…", IsEnabled = false };
        if (flyout.Items.Count > 0 && flyout.Items[^1] is not MenuFlyoutSeparator)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        if (inline)
        {
            flyout.Items.Add(loading);
            return new ShellSection(flyout.Items, loading, workingDirectory);
        }

        var more = new MenuFlyoutSubItem { Text = "More options" };
        more.Items.Add(loading);
        flyout.Items.Add(more);
        return new ShellSection(more.Items, loading, workingDirectory);
    }

    private static void OnContextMenuClosed(ShellSection section)
    {
        section.Closed = true;
        section.Cancel.Cancel();
        if (section.MenuId is { } id && !section.Invoked)
        {
            _ = CloseShellMenuAsync(id);
        }
    }

    private static async Task CloseShellMenuAsync(Guid id)
    {
        try
        {
            var host = App.Host.ShellHost;
            var service = await host.GetServiceAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            await service.CloseContextMenuAsync(id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            // The host sweeps idle menus itself; nothing the user needs to hear.
            UiTrace.Mark($"shell menu: close failed: {ex.Message}");
        }
    }

    private async Task LoadShellItemsAsync(ShellSection section, ShellMenuRequest request)
    {
        try
        {
            var host = App.Host.ShellHost;
            var service = await host.GetServiceAsync(TimeSpan.FromSeconds(8), section.Cancel.Token);
            var menu = await service.OpenContextMenuAsync(request, section.Cancel.Token).WaitAsync(ShellOpenLimit, section.Cancel.Token);
            section.MenuId = menu.Id;
            if (section.Closed)
            {
                _ = CloseShellMenuAsync(menu.Id);
                return;
            }

            var items = ShellMenuFilter.Prepare(menu.Items);
            ReplacePlaceholder(section, items.Count == 0 ? [Notice("No other items")] : BuildShellItems(section, items, [], 0));
        }
        catch (OperationCanceledException)
        {
            // The menu closed before the shell answered.
        }
        catch (Exception ex)
        {
            // A slow, stuck or crashing extension, or a host that is not there: the built-in items stay, and the menu says why.
            UiTrace.Mark($"shell menu: {ex.GetType().Name}: {ex.Message}");
            if (!section.Closed)
            {
                ReplacePlaceholder(section, [Notice(ex is ShellHostUnavailableException unavailable ? unavailable.Message : "Shell extensions did not answer; showing the built-in items only.", ex.Message)]);
            }
        }
    }

    private static MenuFlyoutItem Notice(string text, string? detail = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = false };
        if (detail is not null)
        {
            ToolTipService.SetToolTip(item, detail);
        }

        return item;
    }

    private static void ReplacePlaceholder(ShellSection section, IReadOnlyList<MenuFlyoutItemBase> items)
    {
        var index = section.Host.IndexOf(section.Placeholder);
        if (index < 0)
        {
            return;
        }

        section.Host.RemoveAt(index);
        foreach (var item in items)
        {
            section.Host.Insert(index++, item);
        }
    }

    private List<MenuFlyoutItemBase> BuildShellItems(ShellSection section, IReadOnlyList<ShellMenuItem> items, IReadOnlyList<int> parentPath, int depth)
    {
        var result = new List<MenuFlyoutItemBase>(items.Count);
        foreach (var item in items)
        {
            if (item.Kind == ShellMenuItemKind.Separator)
            {
                result.Add(new MenuFlyoutSeparator());
                continue;
            }

            var (title, shortcut) = ShellMenuText.Split(item.Label);
            if (title.Length == 0)
            {
                continue;
            }

            if (item.Kind == ShellMenuItemKind.SubMenu)
            {
                var sub = new MenuFlyoutSubItem { Text = title, Icon = IconOf(item.Icon) };
                var path = new List<int>(parentPath) { item.Position };
                if (item.Children is { } loaded)
                {
                    FillSubMenu(section, sub, loaded, path, depth + 1);
                }
                else if (depth < MaxSubMenuDepth)
                {
                    sub.Items.Add(Notice("Loading…"));
                    _ = LoadSubMenuAsync(section, sub, path, depth + 1);
                }

                result.Add(sub);
                continue;
            }

            MenuFlyoutItem entry = item.Checked
                ? new ToggleMenuFlyoutItem { IsChecked = true }
                : new MenuFlyoutItem();
            entry.Text = title;
            entry.IsEnabled = item.Enabled;
            entry.Icon = IconOf(item.Icon);
            if (shortcut is not null)
            {
                entry.KeyboardAcceleratorTextOverride = shortcut;
            }

            var captured = item;
            entry.Click += (_, _) => _ = InvokeShellAsync(section, captured, title);
            result.Add(entry);
        }

        return result;
    }

    private async Task LoadSubMenuAsync(ShellSection section, MenuFlyoutSubItem sub, IReadOnlyList<int> path, int depth)
    {
        try
        {
            var service = await App.Host.ShellHost.GetServiceAsync(TimeSpan.FromSeconds(2), section.Cancel.Token);
            var children = await service.LoadSubMenuAsync(section.MenuId!.Value, path, section.Cancel.Token).WaitAsync(ShellOpenLimit, section.Cancel.Token);
            if (!section.Closed)
            {
                FillSubMenu(section, sub, children, path, depth);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed meanwhile.
        }
        catch (Exception ex)
        {
            UiTrace.Mark($"shell submenu: {ex.GetType().Name}: {ex.Message}");
            if (!section.Closed)
            {
                sub.Items.Clear();
                sub.Items.Add(Notice("Unavailable", ex.Message));
            }
        }
    }

    private void FillSubMenu(ShellSection section, MenuFlyoutSubItem sub, IReadOnlyList<ShellMenuItem> children, IReadOnlyList<int> path, int depth)
    {
        sub.Items.Clear();
        var prepared = ShellMenuFilter.Prepare(children);
        foreach (var child in BuildShellItems(section, prepared, path, depth))
        {
            sub.Items.Add(child);
        }

        if (sub.Items.Count == 0)
        {
            sub.Items.Add(Notice("(empty)"));
        }
    }

    private async Task InvokeShellAsync(ShellSection section, ShellMenuItem item, string title)
    {
        if (section.MenuId is not { } menuId)
        {
            return;
        }

        section.Invoked = true;
        try
        {
            var host = App.Host.ShellHost;
            var service = await host.GetServiceAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            if (host.ProcessId is { } hostProcess)
            {
                ForegroundPermission.Allow(hostProcess);
            }

            var owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var result = await service.InvokeAsync(menuId, new ShellInvokeRequest(item.Id, null, section.WorkingDirectory, owner), CancellationToken.None).WaitAsync(ShellInvokeLimit);
            if (!result.Succeeded)
            {
                ShowMessage($"{title} did not run: {result.Error}");
            }
            else
            {
                // Many shell commands change files (Extract here, Git, Send to). Look again.
                Run(CommandIds.NavReload);
            }
        }
        catch (Exception ex)
        {
            UiTrace.Mark($"shell invoke: {ex.GetType().Name}: {ex.Message}");
            ShowMessage($"{title} did not run: {(ex is ShellHostUnavailableException ? ex.Message : "the shell extension did not answer.")}");
        }
    }

    private static ImageIcon? IconOf(ShellMenuIcon? icon)
    {
        if (icon is null || icon.Bgra.Length != icon.Width * icon.Height * 4)
        {
            return null;
        }

        var bitmap = new WriteableBitmap(icon.Width, icon.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(icon.Bgra, 0, icon.Bgra.Length);
        }

        return new ImageIcon { Source = bitmap };
    }
}
