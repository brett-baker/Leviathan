using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The column chooser (spec §8.2), opened from View → Columns… and from "More columns…" at the bottom of the header's right-click
/// list. The dialog only draws <see cref="ColumnChooserModel"/>: a grouped, searchable list with tick boxes (columns whose data
/// does not exist yet are listed greyed), the order of the chosen columns with drag and Up / Down, Reset to default and "Apply to
/// all folders of this type". Applying runs a command.
/// </summary>
public sealed partial class MainWindow
{
    private async Task ShowColumnChooserAsync()
    {
        if (_bound is not { } tab)
        {
            return;
        }

        var model = new ColumnChooserModel(tab.Columns.Ids);
        var updating = false;
        var search = new TextBox { PlaceholderText = "Search columns", Margin = new Thickness(0, 0, 0, 8) };
        var list = new StackPanel { Spacing = 0 };
        var scroll = new ScrollViewer { Content = list, Height = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var order = new ListView { SelectionMode = ListViewSelectionMode.Single, CanReorderItems = true, AllowDrop = true, Height = 300 };
        var up = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 4, 8, 4) };
        var down = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(up, "Move left");
        ToolTipService.SetToolTip(down, "Move right");
        var reset = new Button { Content = "Reset to default" };
        var forType = new CheckBox { Content = "Apply to all folders of this type" };

        string TitleOf(string id) => FileColumns.TryGet(id, out var column) ? column.Title : id;

        void FillOrder()
        {
            updating = true;
            var selected = order.SelectedItem as string;
            order.Items.Clear();
            foreach (var id in model.Order)
            {
                order.Items.Add(TitleOf(id));
            }

            order.SelectedItem = selected;
            updating = false;
        }

        string IdAt(int index) => model.Order[index];

        void FillList()
        {
            list.Children.Clear();
            foreach (var group in model.Groups())
            {
                list.Children.Add(new TextBlock { Text = group.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 10, 0, 2) });
                foreach (var column in group.Columns)
                {
                    var id = column.Id;
                    var box = new CheckBox { Content = column.Title, IsChecked = model.IsChecked(id), IsEnabled = column.Available && id != FileColumns.Name.Id, MinHeight = 28 };
                    if (!column.Available)
                    {
                        ToolTipService.SetToolTip(box, ColumnCatalog.LaterUpdate);
                        box.Content = $"{column.Title}  ·  {ColumnCatalog.LaterUpdate.ToLowerInvariant()}";
                    }

                    box.Click += (_, _) =>
                    {
                        model.SetChecked(id, box.IsChecked == true);
                    };
                    list.Children.Add(box);
                }
            }

            if (list.Children.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No column matches.", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            }
        }

        model.Changed += (_, _) =>
        {
            FillOrder();
            FillList();
        };
        search.TextChanged += (_, _) =>
        {
            model.Search = search.Text;
            FillList();
        };
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(1);
        reset.Click += (_, _) => model.Reset();
        order.DragItemsCompleted += (_, _) =>
        {
            if (updating)
            {
                return;
            }

            // The list was reordered by dragging: read its order back into the model.
            var titles = order.Items.OfType<string>().ToList();
            var ids = model.Order.ToList();
            var reordered = titles.Select(t => ids.First(i => TitleOf(i) == t)).ToList();
            for (var i = 0; i < reordered.Count; i++)
            {
                model.Move(reordered[i], i);
            }
        };

        void MoveSelected(int by)
        {
            if (order.SelectedIndex is >= 0 and var index)
            {
                var id = IdAt(index);
                model.Move(id, index + by);
                order.SelectedIndex = Math.Clamp(index + by, 0, model.Order.Count - 1);
            }
        }

        FillOrder();
        FillList();

        var left = new StackPanel { Width = 300, Children = { search, scroll } };
        var right = new StackPanel
        {
            Width = 220,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Order, left to right", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                order,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { up, down, reset } },
            },
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { left, right } },
                forType,
            },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Choose columns",
            Content = content,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Run(CommandIds.ViewColumns, parameter: new ColumnChoice(model.Result, forType.IsChecked == true));
        }
    }
}
