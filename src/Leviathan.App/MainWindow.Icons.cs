using Leviathan.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The window's icons come from the chosen icon pack (spec §4.3, D33): the fixed buttons are bound once and redrawn when the pack
/// or the theme changes; menus and dynamic parts ask for their icons when they are built. The pack is chosen from the Options menu
/// until the settings sheet (slice I10) takes that over.
/// </summary>
public sealed partial class MainWindow
{
    private void BindChromeIcons()
    {
        var icons = App.Host.Icons;
        if (Content is FrameworkElement root)
        {
            icons.SetDark(root.ActualTheme == ElementTheme.Dark);
            root.ActualThemeChanged += (s, _) => icons.SetDark(s.ActualTheme == ElementTheme.Dark);
            root.Loaded += (s, _) => icons.SetDark(((FrameworkElement)s).ActualTheme == ElementTheme.Dark);
        }

        Icons.Bind(BackButton, static (b, i) => b.Content = i, "back");
        Icons.Bind(ForwardButton, static (b, i) => b.Content = i, "forward");
        Icons.Bind(UpButton, static (b, i) => b.Content = i, "up");
        Icons.Bind(ReloadButton, static (b, i) => b.Content = i, "reload");
        Icons.Bind(AllTabsButton, static (b, i) => b.Content = i, "chevron-down", 12);
        Icons.Bind(NewButton, static (b, i) => ((StackPanel)b.Content).Children[0] = i, "add");
        Icons.Bind(CutButton, static (b, i) => b.Icon = i, "cut", 20);
        Icons.Bind(CopyButton, static (b, i) => b.Icon = i, "copy", 20);
        Icons.Bind(PasteButton, static (b, i) => b.Content = i, "paste", 20);
        Icons.Bind(RenameButton, static (b, i) => b.Icon = i, "rename", 20);
        Icons.Bind(DeleteButton, static (b, i) => b.Icon = i, "delete", 20);
        Icons.Bind(UndoButton, static (b, i) => b.Icon = i, "undo", 20);
        Icons.Bind(RedoButton, static (b, i) => b.Icon = i, "redo", 20);
        Icons.Bind(OptionsButton, static (b, i) => b.Content = i, "settings");
        Icons.Bind(DetailsSwitch, static (b, i) => b.Content = i, "layout-details");
        Icons.Bind(ListSwitch, static (b, i) => b.Content = i, "layout-list");
        Icons.Bind(ThumbsSwitch, static (b, i) => b.Content = i, "layout-thumbnails");
        Icons.Bind(CardHideButton, static (b, i) => b.Content = i, "close", 12);

        icons.Changed += OnIconsChanged;
    }

    // The pack or the theme changed: bound icons were redrawn by the service; rebuild what is built from data.
    private void OnIconsChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        Sidebar.ItemsSource = null;
        Sidebar.ItemsSource = _sidebar.Rows;
        Address.RefreshIcons();
        FileList.RefreshIcons();
        FlowList.RefreshIcons();
        foreach (var button in _contextButtons)
        {
            if (_barButtons.TryGetValue(button, out var model))
            {
                button.Icon = Icons.OfGlyph(model.Glyph, 20);
            }
        }

        foreach (var ui in _tabUi.Values)
        {
            ui.RefreshIcons();
        }

        UpdateStatus();
        UpdateState();
    }
}
