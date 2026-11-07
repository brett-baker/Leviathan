using Leviathan.Core.Model;
using Leviathan.ViewModels.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Graphics;

namespace Leviathan.App;

/// <summary>
/// The large floating preview that Space opens, Quick Look style (spec §9): a window over the file view showing the selection, closed
/// by Space or Esc. This is its shell; the renderers of spec §9.1 fill it in the next slice, so for now it shows the same words as the
/// pane's video area, larger.
/// </summary>
public sealed class QuickPreviewWindow : Window
{
    private readonly Grid _root = new() { Padding = new Thickness(24), IsTabStop = true };

    /// <summary>Opens a floating preview of <paramref name="entries"/> (never empty).</summary>
    public QuickPreviewWindow(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Title = entries.Count == 1 ? $"Preview · {entries[0].Name}" : $"Preview · {entries.Count:N0} items";
        _root.Children.Add(new TextBlock
        {
            Text = PreviewText.Placeholder(entries),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 20,
        });
        _root.KeyDown += OnKeyDown;
        Content = _root;
        AppWindow.Resize(new SizeInt32(900, 560));
        Activated += (_, _) => _root.Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Space or VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
