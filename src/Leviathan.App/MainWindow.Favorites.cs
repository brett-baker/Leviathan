using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sidebar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Leviathan.App;

/// <summary>
/// The favorite star in the address bar (spec §5.2) and its right-click menu: rename, icon, colour. The star follows the favorites
/// list, so it changes whether the folder was pinned with Ctrl+D, the star, the tree or a drop.
/// </summary>
public sealed partial class MainWindow
{
    private void UpdateFavoriteStar()
    {
        if (_bound is not { } tab)
        {
            Address.SetFavorite(false);
            return;
        }

        var here = tab.History.Current;
        var favorite = App.Host.Favorites.Items.FirstOrDefault(f => f.Location == here);
        Address.SetFavorite(favorite is not null, FavoriteStyles.GlyphOf(favorite?.Icon), favorite?.Color);
    }

    private void OnAddressFavoriteMenu(object? sender, RightTappedRoutedEventArgs e)
    {
        if (_bound is not { } tab)
        {
            return;
        }

        var here = tab.History.Current;
        var favorite = App.Host.Favorites.Items.FirstOrDefault(f => f.Location == here);
        var menu = new MenuFlyout();
        if (favorite is null)
        {
            var add = new MenuFlyoutItem { Text = "Add to Favorites", KeyboardAcceleratorTextOverride = "Ctrl+D" };
            add.Click += (_, _) => Run(CommandIds.FavoriteToggle);
            menu.Items.Add(add);
        }
        else
        {
            var rename = new MenuFlyoutItem { Text = "Rename…" };
            rename.Click += async (_, _) => await RenameFavoriteAsync(here, favorite.Name ?? here.Name);
            menu.Items.Add(rename);

            var icon = new MenuFlyoutSubItem { Text = "Icon" };
            foreach (var (key, title, glyph) in FavoriteStyles.Icons)
            {
                var item = new ToggleMenuFlyoutItem { Text = title, IsChecked = (favorite.Icon ?? "star") == key, Icon = Icons.OfGlyph(glyph) };
                item.Click += (_, _) => Run(CommandIds.FavoriteStyle, here.Value, new FavoriteStyleChange(key == "star" ? null : key, favorite.Color));
                icon.Items.Add(item);
            }

            var color = new MenuFlyoutSubItem { Text = "Colour" };
            var none = new ToggleMenuFlyoutItem { Text = "Default", IsChecked = favorite.Color is null };
            none.Click += (_, _) => Run(CommandIds.FavoriteStyle, here.Value, new FavoriteStyleChange(favorite.Icon, null));
            color.Items.Add(none);
            foreach (var (title, hex) in FavoriteStyles.Colors)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = title,
                    IsChecked = string.Equals(favorite.Color, hex, StringComparison.OrdinalIgnoreCase),
                    Icon = Icons.OfGlyph("", 16, hex),
                };
                item.Click += (_, _) => Run(CommandIds.FavoriteStyle, here.Value, new FavoriteStyleChange(favorite.Icon, hex));
                color.Items.Add(item);
            }

            menu.Items.Add(icon);
            menu.Items.Add(color);
            menu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "Remove from Favorites", KeyboardAcceleratorTextOverride = "Ctrl+D" };
            remove.Click += (_, _) => Run(CommandIds.FavoriteToggle);
            menu.Items.Add(remove);
        }

        menu.ShowAt(Address, e.GetPosition(Address));
    }
}
