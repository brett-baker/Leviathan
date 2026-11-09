using Leviathan.Core.Model;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>The edit part of the details pane (spec §9.2): title, comments and rating written back through the shell's property handler.</summary>
public sealed partial class MainWindow
{
    private static readonly ShellPropertyWriter s_propertyWriter = new();

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
            var result = await editor.SaveAsync(entry.Location.ToWin32Path(), s_propertyWriter, token);
            status.Text = result.Message;
            status.Visibility = Visibility.Visible;
            if (!result.Saved)
            {
                save.IsEnabled = true;
            }
            else
            {
                MessageText.Text = $"Saved the properties of {entry.Name}.";
            }
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
