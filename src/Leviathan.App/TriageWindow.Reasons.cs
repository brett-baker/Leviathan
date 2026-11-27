using Leviathan.ViewModels.Triage.Learning;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// The reject-reason chips under the video: the user's own short titles, always visible, one click to discard with that reason (or
/// Shift plus the chip's number). The description a reason carries is for the learning and is never shown here. The list starts empty.
/// </summary>
internal sealed partial class TriageWindow
{
    private static readonly SolidColorBrush ChipBackground = new(ColorHelper.FromArgb(0xFF, 0x3A, 0x1F, 0x22));
    private static readonly SolidColorBrush ChipBorder = new(ColorHelper.FromArgb(0xFF, 0x7A, 0x3B, 0x41));
    private static readonly SolidColorBrush ChipText = new(ColorHelper.FromArgb(0xFF, 0xFF, 0xB3, 0xB7));
    private static readonly SolidColorBrush ChipHint = new(ColorHelper.FromArgb(0xFF, 0xA8, 0xA8, 0xA8));

    private bool _reasonsHooked;

    // Whether learning was on when this triage began (Settings, Triage, Learning). Off, there is no chip bar, no Shift+number discard
    // and no history; a change in Settings applies from the next triage.
    private bool _learning;

    // The list is read once off the UI thread (it comes from a file), so the keys and the chips never wait for the disk.
    private async Task LoadReasonsAsync()
    {
        ReasonBar.Visibility = _learning ? Visibility.Visible : Visibility.Collapsed;
        if (!_learning)
        {
            return;
        }

        await Task.Run(() => App.Host.RejectReasons.Reasons.Count);
        if (!_reasonsHooked)
        {
            _reasonsHooked = true;
            App.Host.RejectReasons.Changed += OnReasonsChanged;
        }

        ShowReasonChips();
    }

    private void OnReasonsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowReasonChips);

    private void UnhookReasons()
    {
        UnhookLearning();
        if (_reasonsHooked)
        {
            _reasonsHooked = false;
            App.Host.RejectReasons.Changed -= OnReasonsChanged;
        }
    }

    // The id of the reason in position n (1 to 9) of the list, for Shift+n.
    private string? ReasonAt(int number) => _learning ? App.Host.RejectReasons.Reasons.ElementAtOrDefault(number - 1)?.Id : null;

    private void ShowReasonChips()
    {
        ReasonChips.Children.Clear();
        var reasons = App.Host.RejectReasons.Reasons;
        for (var i = 0; i < reasons.Count; i++)
        {
            ReasonChips.Children.Add(NewChip(reasons[i], i + 1));
        }

        var add = new Button
        {
            Content = reasons.Count == 0 ? "+ Add a reject reason" : "+",
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            IsTabStop = false,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(add, "Add a reason for discarding. Only its title shows here.");
        add.Click += async (_, _) =>
        {
            await EditReasonAsync(null);
            Root.Focus(FocusState.Programmatic);
        };
        ReasonChips.Children.Add(add);
    }

    private Button NewChip(RejectReason reason, int position)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new TextBlock { Text = reason.Title, Foreground = ChipText, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (position <= 9)
        {
            label.Children.Add(new TextBlock { Text = $"⇧{position}", Foreground = ChipHint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }

        var chip = new Button
        {
            Content = label,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            IsTabStop = false,
            Background = ChipBackground,
            BorderBrush = ChipBorder,
            Tag = reason.Id,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Discard because: {reason.Title}");
        chip.Click += async (_, _) => await DiscardBecauseAsync(reason.Id);

        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit this reason…" };
        edit.Click += async (_, _) =>
        {
            await EditReasonAsync(reason);
            Root.Focus(FocusState.Programmatic);
        };
        var remove = new MenuFlyoutItem { Text = "Remove this reason" };
        remove.Click += async (_, _) =>
        {
            await Task.Run(() => App.Host.RejectReasons.Remove(reason.Id));
            Root.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(edit);
        menu.Items.Add(remove);
        chip.ContextFlyout = menu;
        return chip;
    }

    private async Task DiscardBecauseAsync(string reasonId)
    {
        if (_session is { } session)
        {
            await session.DiscardAsync(reasonId);
            Refresh();
        }

        Root.Focus(FocusState.Programmatic); // the keys must keep working after a click
    }

    // A reason is a title and, only for the learning, what the title means.
    private async Task EditReasonAsync(RejectReason? existing)
    {
        var title = new TextBox { Text = existing?.Title ?? string.Empty, PlaceholderText = "Title, as it shows on the chip (e.g. Too short)", MaxLength = 40 };
        var description = new TextBox
        {
            Text = existing?.Description ?? string.Empty,
            PlaceholderText = "What it means, so the learning can use it. Never shown here.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
        };
        var content = new StackPanel { Spacing = 10, MinWidth = 380 };
        content.Children.Add(title);
        content.Children.Add(description);
        var dialog = new ContentDialog
        {
            Title = existing is null ? "Add a reject reason" : "Edit reject reason",
            Content = content,
            PrimaryButtonText = existing is null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var text = title.Text;
            var meaning = description.Text;
            await Task.Run(() =>
            {
                if (existing is null)
                {
                    App.Host.RejectReasons.Add(text, meaning);
                }
                else
                {
                    App.Host.RejectReasons.Update(existing.Id, text, meaning);
                }
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = ex.Message;
            ErrorBox.Visibility = Visibility.Visible;
        }
    }
}
