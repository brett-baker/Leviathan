using Leviathan.Core.Paths;
using Leviathan.ViewModels.Triage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>Triage inboxes (spec §10.1 "Working at scale"): the chooser and the start of a session of N.</summary>
public sealed partial class MainWindow
{
    private bool _inboxOpen;

    /// <summary>
    /// Alt+Shift+T: pick a saved inbox (or save the current folder as one), read it for new arrivals, and triage the next batch of
    /// undecided files in the inbox's order. Decisions are saved when the triage window closes, so the next session carries on.
    /// </summary>
    public void StartInboxTriage() => OnUi(async () =>
    {
        if (_inboxOpen)
        {
            return;
        }

        _inboxOpen = true;
        try
        {
            await ChooseInboxAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Providers.LocationException or ArgumentException)
        {
            MessageText.Text = $"The inbox could not be opened: {ex.Message}";
        }
        finally
        {
            _inboxOpen = false;
        }
    });

    private async Task ChooseInboxAsync()
    {
        var here = _bound?.History.Current;
        var catalog = App.Host.Inboxes;
        var saved = await catalog.ListAsync(CancellationToken.None);

        const string NewItem = "New inbox from this folder";
        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = "Inbox" };
        foreach (var inbox in saved)
        {
            choice.Items.Add(inbox);
        }

        var canCreate = here is { Kind: LocationKind.Drive or LocationKind.Unc };
        if (canCreate)
        {
            choice.Items.Add(NewItem);
        }

        if (choice.Items.Count == 0)
        {
            MessageText.Text = "Open a folder first: an inbox is a saved queue over a folder.";
            return;
        }

        choice.DisplayMemberPath = nameof(TriageInbox.Name);
        choice.SelectedIndex = 0;
        var name = new TextBox { Header = "Name", Text = here?.Name ?? "Inbox" };
        var recursive = new CheckBox { Content = "Include sub-folders", IsChecked = true };
        var order = new ComboBox { Header = "Order", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Oldest first", "Newest first", "Largest first", "Random" }, SelectedIndex = 0 };
        var batch = new NumberBox { Header = "Files per session", Minimum = 1, Maximum = TriageQueue.MaxItems, Value = 1000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };

        void Sync()
        {
            var isNew = choice.SelectedItem is string;
            name.Visibility = recursive.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            if (choice.SelectedItem is TriageInbox inbox)
            {
                order.SelectedIndex = (int)inbox.Order;
                batch.Value = inbox.BatchSize;
                detail.Text = $"{inbox.Folder}\n{inbox.Decided.Count:N0} triaged of {inbox.Known.Count:N0} seen" + (inbox.New.Count > 0 ? $" · {inbox.New.Count:N0} new" : string.Empty);
            }
            else
            {
                detail.Text = here is null ? string.Empty : here.ToWin32Path();
            }
        }

        choice.SelectionChanged += (_, _) => Sync();
        Sync();

        var dialog = new ContentDialog
        {
            Title = "Triage an inbox",
            Content = new StackPanel { Spacing = 10, MinWidth = 380, Children = { choice, name, recursive, order, batch, detail } },
            PrimaryButtonText = "Start",
            SecondaryButtonText = "Delete inbox",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        var picked = choice.SelectedItem;
        var which = (TriageOrder)Math.Max(0, order.SelectedIndex);
        var size = (int)Math.Clamp(double.IsNaN(batch.Value) ? 1000 : batch.Value, 1, TriageQueue.MaxItems);

        if (result == ContentDialogResult.Secondary)
        {
            if (picked is TriageInbox doomed)
            {
                await catalog.RemoveAsync(doomed, CancellationToken.None);
                MessageText.Text = $"Removed the inbox \"{doomed.Name}\". Its files were not touched.";
            }

            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        TriageInbox target;
        if (picked is TriageInbox existing)
        {
            target = existing;
            await catalog.SetSessionOptionsAsync(existing, which, size, CancellationToken.None);
        }
        else
        {
            target = await catalog.AddAsync(string.IsNullOrWhiteSpace(name.Text) ? "Inbox" : name.Text, here!.ToWin32Path(), recursive.IsChecked == true, which, size, CancellationToken.None);
        }

        MessageText.Text = $"Reading the inbox \"{target.Name}\"…";
        var folder = LocationPath.Parse(target.Folder);
        var scan = await catalog.ScanAsync(target, App.Host.Providers.Resolve(folder), CancellationToken.None);
        var session = InboxCatalog.NextSession(target, scan);
        if (session.Queue.Count == 0)
        {
            MessageText.Text = scan.All.Count == 0
                ? $"The inbox \"{target.Name}\" has no videos."
                : $"Nothing left to triage in \"{target.Name}\": all {scan.All.Count:N0} videos are decided.";
            return;
        }

        MessageText.Text = scan.Arrived > 0 ? $"{scan.Arrived:N0} new in \"{target.Name}\"." : string.Empty;
        OpenTriageWindow(session.Queue, 0, target.Name, session);
    }
}
