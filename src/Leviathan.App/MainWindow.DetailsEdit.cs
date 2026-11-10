using Leviathan.Core.Model;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>The edit part of the details pane (spec §9.2): title, comments and rating written back through the shell's property handler.</summary>
public sealed partial class MainWindow
{
    private static readonly Leviathan.Interop.ShellPropertyWriter s_propertyWriter = new();

    /// <summary>Stops the preview and closes the file it shows, so nothing in this window holds the selected file open.</summary>
    private async Task ReleasePreviewAsync()
    {
        _coordinator?.Cancel();
        ShowOnly(null);
        if (_player is { } player)
        {
            await player.CloseFileAsync();
        }
    }

    // Up and Down pressed on a button or other plain control of the pane carry on in the file list, as if the list had the focus
    // (text boxes, the rating stars, sliders and the web view keep their own arrow keys).
    private void OnPreviewPaneKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
            || e.OriginalSource is TextBox or RatingControl or Slider or ComboBox or NumberBox or ListViewBase or Microsoft.UI.Xaml.Controls.WebView2
            || KeyTranslator.IsDown(Windows.System.VirtualKey.Control) || KeyTranslator.IsDown(Windows.System.VirtualKey.Menu))
        {
            return;
        }

        e.Handled = true;
        FocusFiles();
        if (FileList.Visibility == Visibility.Visible && _bound is { } tab)
        {
            tab.Selection.Move(e.Key == Windows.System.VirtualKey.Down ? 1 : -1, KeyTranslator.IsDown(Windows.System.VirtualKey.Shift));
        }
    }

    private Expander EditExpander(FileEntry entry, IReadOnlyDictionary<string, object?> values, CancellationToken token)
    {
        var editor = new PropertyEditor(values);
        var title = new TextBox { Header = "Title", Text = editor.Title };
        var comment = new TextBox { Header = "Comments", Text = editor.Comment, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        var rating = new RatingControl { MaxRating = 5, Value = editor.Stars == 0 ? -1 : editor.Stars, HorizontalAlignment = HorizontalAlignment.Left };
        var save = new Button { Content = "Save", IsEnabled = false };
        var status = Muted(string.Empty);
        status.Visibility = Visibility.Collapsed;

        void Changed()
        {
            editor.Title = title.Text;
            editor.Comment = comment.Text;
            editor.Stars = rating.Value < 0 ? 0 : (int)rating.Value;
            save.IsEnabled = editor.IsDirty;
            status.Visibility = Visibility.Collapsed;
        }

        title.TextChanged += (_, _) => Changed();
        comment.TextChanged += (_, _) => Changed();
        rating.ValueChanged += (_, _) => Changed();
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            status.Text = "Saving…";
            status.Visibility = Visibility.Visible;

            // The shell's property handlers open the file exclusively, so the pane must let go of it first (a picture, a video or a
            // document being previewed would otherwise make Windows refuse with "unauthorized operation"); it is shown again afterwards.
            await ReleasePreviewAsync();
            PropertySaveResult result;
            try
            {
                result = await editor.SaveAsync(entry.Location.ToWin32Path(), s_propertyWriter, token);
            }
            finally
            {
                _previewKey = string.Empty;
                UpdatePreview();
            }

            status.Text = result.Message;
            if (!result.Saved)
            {
                save.IsEnabled = true;
            }
            else
            {
                MessageText.Text = $"Saved the properties of {entry.Name}.";
            }

            FocusFiles(); // arrow keys keep moving between files
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title);
        panel.Children.Add(comment);
        panel.Children.Add(new TextBlock { Text = "Rating", Opacity = 0.65 });
        panel.Children.Add(rating);
        panel.Children.Add(save);
        panel.Children.Add(status);
        return new Expander { Header = "Edit title, comments, rating", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = panel };
    }
}
