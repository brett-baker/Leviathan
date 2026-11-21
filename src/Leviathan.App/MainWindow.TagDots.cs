using Leviathan.ViewModels.Tags;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace Leviathan.App;

/// <summary>
/// The colour tags as dots in the action row (spec §11.1): click one to tag the selection with it, or to take it off when every selected
/// file has it, exactly as the keys 1 to 9 do. The dots follow the user's tag list (name, colour, order) and are enabled with a selection.
/// </summary>
public sealed partial class MainWindow
{
    private void InitializeTagDots()
    {
        BuildTagDots();
        App.Host.Settings.Changed += (_, _) => OnUi(BuildTagDots);
    }

    private void BuildTagDots()
    {
        TagDots.Children.Clear();
        var tags = App.Host.Settings.Current.Tags.Catalog().Tags;
        for (var i = 0; i < Math.Min(tags.Count, TagCatalog.KeyedTags); i++)
        {
            var number = i + 1;
            var tag = tags[i];
            var button = new Button
            {
                Padding = new Thickness(5),
                MinWidth = 0,
                MinHeight = 0,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new Ellipse { Width = 14, Height = 14, Fill = SidebarFormat.IconBrush(tag.Colour) },
            };
            ToolTipService.SetToolTip(button, $"{tag.Name} ({number}): tag or untag the selection");
            AutomationProperties.SetName(button, "Tag " + tag.Name);
            button.IsEnabled = _bound?.Selection.Count > 0;
            button.Click += (_, _) => OnTagKey(this, number);
            TagDots.Children.Add(button);
        }
    }
}
