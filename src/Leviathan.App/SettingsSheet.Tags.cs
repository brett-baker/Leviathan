using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Tags;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Leviathan.App;

/// <summary>The Tags page (spec §11.1, §17.2): rename, recolour and reorder the colour tags, add more, tint rows. Saves at once like every page.</summary>
internal sealed partial class SettingsSheet
{
    // What the rows on the page show, so a save that changes nothing visible does not rebuild them under the user's cursor.
    private string _tagsShown = string.Empty;

    private void FillTags(TagSettings tags)
    {
        _loading = true;
        try
        {
            TintSwitch.IsOn = tags.TintRows;
        }
        finally
        {
            _loading = false;
        }

        var catalog = tags.Catalog();
        var signature = string.Join('|', catalog.Tags.Select(t => $"{t.Id}:{t.Name}:{t.Colour}"));
        if (signature == _tagsShown)
        {
            return;
        }

        _tagsShown = signature;
        TagRows.Children.Clear();
        for (var i = 0; i < catalog.Tags.Count; i++)
        {
            TagRows.Children.Add(TagRow(catalog.Tags[i], i, catalog.Tags.Count));
        }

        AddTagButton.IsEnabled = catalog.Tags.Count < TagCatalog.MaxTags;
    }

    private static Grid TagRow(ColorTag tag, int index, int count)
    {
        var id = tag.Id;
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The key that tags with this one (the first nine), so reordering shows what it changes.
        var key = new TextBlock { Text = index < TagCatalog.KeyedTags ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(key);

        var swatch = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(GridRow.TagColour(tag.Colour)),
            Flyout = ColourFlyout(id),
        };
        ToolTipService.SetToolTip(swatch, "Change the colour");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"Colour of {tag.Name}");
        Grid.SetColumn(swatch, 1);
        row.Children.Add(swatch);

        var name = new TextBox { Text = tag.Name, MaxLength = TagCatalog.MaxNameLength, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, $"Name of tag {index + 1}");
        name.LostFocus += async (_, _) => await CommitNameAsync(id, name, tag.Name);
        name.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await CommitNameAsync(id, name, tag.Name);
            }
        };
        Grid.SetColumn(name, 2);
        row.Children.Add(name);

        var up = MoveButton("", "Move up", enabled: index > 0, async () => await SaveCatalogAsync(c => c.Move(id, index - 1)));
        Grid.SetColumn(up, 3);
        row.Children.Add(up);
        var down = MoveButton("", "Move down", enabled: index < count - 1, async () => await SaveCatalogAsync(c => c.Move(id, index + 1)));
        Grid.SetColumn(down, 4);
        row.Children.Add(down);
        return row;
    }

    private static Button MoveButton(string glyph, string tip, bool enabled, Func<Task> click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, IsEnabled = enabled, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        button.Click += async (_, _) => await click();
        return button;
    }

    private static Flyout ColourFlyout(int id)
    {
        var flyout = new Flyout();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (name, hex) in TagPalette.Colours)
        {
            var button = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(GridRow.TagColour(hex)),
            };
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += async (_, _) =>
            {
                flyout.Hide();
                await SaveCatalogAsync(c => c.Recolour(id, hex));
            };
            panel.Children.Add(button);
        }

        flyout.Content = panel;
        return flyout;
    }

    private static async Task CommitNameAsync(int id, TextBox box, string current)
    {
        var text = box.Text.Trim();
        if (text.Length == 0)
        {
            box.Text = current; // a tag always has a name
            return;
        }

        if (text != current)
        {
            await SaveCatalogAsync(c => c.Rename(id, text));
        }
    }

    private static Task SaveCatalogAsync(Func<TagCatalog, TagCatalog> change) =>
        SaveAsync(s => s with { Tags = s.Tags.WithCatalog(change(s.Tags.Catalog())) });

    private async void OnAddTagClick(object sender, RoutedEventArgs e) =>
        await SaveCatalogAsync(c => c.Tags.Count >= TagCatalog.MaxTags ? c : c.Add("New tag", TagPalette.Colours[0].Hex));

    private async void OnResetTagsClick(object sender, RoutedEventArgs e) => await SaveCatalogAsync(c => c.ResetStandard());

    private async void OnTintToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var on = TintSwitch.IsOn;
        await SaveAsync(s => s with { Tags = s.Tags with { TintRows = on } });
    }
}
