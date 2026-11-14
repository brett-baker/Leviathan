using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Palette;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace Leviathan.App;

/// <summary>
/// The command palette (Ctrl+Shift+P) and the go-to palette (Ctrl+P) (spec §5.3, §15.6): a box at the top of the window, a list of what
/// matches under it, Up and Down to choose, Enter to go, Shift+Enter to open a folder in a new tab, Esc to close. The matching is the
/// view model's (<see cref="PaletteModel"/>); this file only draws rows and passes keys.
/// </summary>
public sealed partial class MainWindow
{
    private bool _paletteOpen;

    /// <inheritdoc />
    public void OpenPalette(PaletteMode mode) => OnUi(() => _ = ShowPaletteAsync(mode));

    private async Task ShowPaletteAsync(PaletteMode mode)
    {
        if (_paletteOpen)
        {
            return;
        }

        _paletteOpen = true;
        try
        {
            var host = App.Host;
            var model = new PaletteModel(
                _commands,
                host.Keys,
                () => [.. host.Favorites.Items.Select(f => f.Location)],
                () => host.Recent.Snapshot(),
                () => [.. _viewModel.Tabs.Tabs.Select((t, i) => new PaletteTab(i + 1, t.Title, t.History.Current))]);

            var box = new TextBox
            {
                PlaceholderText = mode == PaletteMode.Commands ? "Type a command" : "Go to a folder, tab or command (start with > for commands only)",
                MinWidth = 620,
            };
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420, IsTabStop = false };
            list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.MinHeightProperty, 0.0), new Setter(Control.PaddingProperty, new Thickness(10, 5, 10, 5)) } };
            var rows = new List<PaletteItem>();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(box);
            panel.Children.Add(list);
            var dialog = new ContentDialog
            {
                Content = panel,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.None,
                XamlRoot = Content.XamlRoot,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 48, 0, 0),
            };

            void Refresh()
            {
                rows = [.. model.Search(mode, box.Text)];
                list.Items.Clear();
                foreach (var row in rows)
                {
                    list.Items.Add(Row(row));
                }

                if (rows.Count > 0)
                {
                    list.SelectedIndex = 0;
                }
            }

            PaletteItem? chosen = null;
            var inNewTab = false;
            box.TextChanged += (_, _) => Refresh();
            box.PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case VirtualKey.Down when rows.Count > 0:
                        list.SelectedIndex = Math.Min(rows.Count - 1, list.SelectedIndex + 1);
                        list.ScrollIntoView(list.SelectedItem);
                        e.Handled = true;
                        break;
                    case VirtualKey.Up when rows.Count > 0:
                        list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                        list.ScrollIntoView(list.SelectedItem);
                        e.Handled = true;
                        break;
                    case VirtualKey.Enter when list.SelectedIndex >= 0 && list.SelectedIndex < rows.Count:
                        chosen = rows[list.SelectedIndex];
                        inNewTab = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                        e.Handled = true;
                        dialog.Hide();
                        break;
                }
            };
            list.IsItemClickEnabled = true;
            list.ItemClick += (_, e) =>
            {
                var index = list.Items.IndexOf(e.ClickedItem);
                if (index >= 0 && index < rows.Count)
                {
                    chosen = rows[index];
                    dialog.Hide();
                }
            };
            dialog.Opened += (_, _) =>
            {
                box.Focus(FocusState.Programmatic);
                Refresh();
            };
            await dialog.ShowAsync();

            if (chosen is not null)
            {
                await model.RunAsync(chosen, inNewTab, null, _closing.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        finally
        {
            _paletteOpen = false;
        }
    }

    // One row: the title with the letters that matched in bold, the category or path in grey, the shortcut at the right.
    private static Grid Row(PaletteItem item)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var matches = item.TitleMatches.ToHashSet();
        for (var i = 0; i < item.Title.Length;)
        {
            var bold = matches.Contains(i);
            var start = i;
            while (i < item.Title.Length && matches.Contains(i) == bold)
            {
                i++;
            }

            title.Inlines.Add(new Run { Text = item.Title[start..i], FontWeight = bold ? FontWeights.Bold : FontWeights.Normal });
        }

        var detail = new TextBlock { Text = item.Kind == PaletteKind.Command ? item.Detail : $"{KindLabel(item.Kind)}  ·  {item.Detail}", FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        var left = new StackPanel { Spacing = 1 };
        left.Children.Add(title);
        left.Children.Add(detail);
        grid.Children.Add(left);
        if (item.Shortcut is { Length: > 0 } shortcut)
        {
            var key = new TextBlock { Text = shortcut, FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(key, 1);
            grid.Children.Add(key);
        }

        return grid;
    }

    private static string KindLabel(PaletteKind kind) => kind switch
    {
        PaletteKind.Tab => "Open tab",
        PaletteKind.Favorite => "Favorite",
        PaletteKind.Recent => "Recent",
        _ => "Command",
    };
}
