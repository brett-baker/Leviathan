using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Leviathan.App;

/// <summary>How the user left the rename box.</summary>
/// <param name="Text">The name in the box.</param>
/// <param name="Commit">True to rename; false (Esc) to leave the name alone.</param>
/// <param name="Next">True when Tab asked to go on to the next item.</param>
internal sealed record RenameResult(string Text, bool Commit, bool Next);

/// <summary>
/// The inline rename box of the file views (spec §8.4): opens with the name selected without its extension, Enter or a click
/// elsewhere renames, Esc cancels, Tab renames and moves on to the next item. A name that is refused leaves the box open, so
/// the next Enter or click-away tries again once the text has changed.
/// </summary>
internal sealed partial class RenameBox : TextBox
{
    private bool _committed = true;

    /// <summary>Creates a hidden box.</summary>
    public RenameBox()
    {
        Visibility = Visibility.Collapsed;
        MinHeight = 0;
        Padding = new Thickness(4, 1, 4, 1);
        VerticalAlignment = VerticalAlignment.Center;
        AcceptsReturn = false;
        KeyDown += OnKey;
        LostFocus += (_, _) => Finish(commit: true, next: false);
        TextChanged += (_, _) => _committed = false;
    }

    /// <summary>Raised when the user finishes with the box.</summary>
    public event EventHandler<RenameResult>? Finished;

    /// <summary>True while the box is showing.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Shows the box with <paramref name="entry"/>'s name, the part before the extension selected, and focuses it.</summary>
    public void Begin(FileEntry entry)
    {
        Text = entry.Name;
        Visibility = Visibility.Visible;
        var stem = RenameRules.StemLength(entry);
        _committed = false;

        // The row may have been realized a moment ago; wait until the box is in the tree before taking the focus.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsOpen && Focus(FocusState.Programmatic))
            {
                Select(0, stem);
            }
        });
    }

    /// <summary>Hides the box without renaming. Returns true when it had the keyboard focus, so the caller can give it back to the list.</summary>
    public bool Hide()
    {
        var hadFocus = IsOpen && FocusState != FocusState.Unfocused;
        _committed = true;
        Visibility = Visibility.Collapsed;
        return hadFocus;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                Finish(commit: true, next: false);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                _committed = true;
                Finished?.Invoke(this, new RenameResult(Text, Commit: false, Next: false));
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                Finish(commit: true, next: !KeyTranslator.IsDown(VirtualKey.Shift));
                break;
        }
    }

    private void Finish(bool commit, bool next)
    {
        if (!IsOpen || _committed)
        {
            return;
        }

        _committed = true;
        Finished?.Invoke(this, new RenameResult(Text, commit, next));
    }
}
