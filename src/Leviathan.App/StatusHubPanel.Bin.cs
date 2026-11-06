using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Hub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The Soft-delete bin tab (spec §12.5): what was deleted from network shares, with Restore, Delete permanently and a button that
/// applies the retention limits now. Reading and acting are the worker's; this only lists, asks for a confirmation before anything
/// that cannot be undone, and says what it started.
/// </summary>
internal sealed partial class StatusHubPanel
{
    private BinModel? _bin;

    private void StartBin(BinModel bin)
    {
        _bin = bin;
        bin.Changed += OnBinChanged;
        App.Host.Transfers.Finished += OnBinJobFinished;
        Unloaded += (_, _) =>
        {
            bin.Changed -= OnBinChanged;
            App.Host.Transfers.Finished -= OnBinJobFinished;
        };
    }

    // The tab was opened: read the bin (the worker does the reading; a share that is away costs this tab a line, not the window).
    private void LoadBin()
    {
        if (_bin is null || !_ready || _model.Tab != HubTab.Deleted)
        {
            return;
        }

        _ = LoadBinAsync();
    }

    private async Task LoadBinAsync()
    {
        try
        {
            await _bin!.RefreshAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Closed while reading.
        }
    }

    // A restore, purge or delete just ended, so what the bin holds changed; refresh only when it is on screen.
    private void OnBinJobFinished(object? sender, TransferFinished done)
    {
        if (done.Kind is Contracts.JobKind.Restore or Contracts.JobKind.Purge or Contracts.JobKind.Delete)
        {
            DispatcherQueue.TryEnqueue(LoadBin);
        }
    }

    private void OnBinChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshBin);

    private void RefreshBin()
    {
        if (_bin is null || !_ready)
        {
            return;
        }

        var selected = SelectedBinPaths();
        var rows = _bin.Rows(BinSearch.Text);
        BinList.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains(r.Entry.Trashed)))
        {
            BinList.SelectedItems.Add(row);
        }

        BinSummary.Text = _bin.IsLoading && rows.Count == 0 ? "Reading the bin…" : _bin.Summary();
        UpdateBinButtons();
    }

    private HashSet<string> SelectedBinPaths() =>
        [.. BinList.SelectedItems.OfType<BinRow>().Select(r => r.Entry.Trashed)];

    private List<BinRow> SelectedBinRows() => [.. BinList.SelectedItems.OfType<BinRow>()];

    private void UpdateBinButtons()
    {
        var any = BinList.SelectedItems.Count > 0;
        BinRestoreButton.IsEnabled = any;
        BinDeleteButton.IsEnabled = any;
    }

    private void ShowBinMessage(string text)
    {
        BinMessage.Text = text;
        BinMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBinSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBinButtons();

    private void OnBinSearchChanged(object sender, TextChangedEventArgs e) => RefreshBin();

    private void OnBinRefreshClick(object sender, RoutedEventArgs e)
    {
        ShowBinMessage(string.Empty);
        LoadBin();
    }

    private async void OnBinRestoreClick(object sender, RoutedEventArgs e)
    {
        var rows = SelectedBinRows();
        if (rows.Count == 0 || _bin is null)
        {
            return;
        }

        ShowBinMessage((await _bin.RestoreAsync(rows, CancellationToken.None)).Message);
    }

    private async void OnBinPurgeClick(object sender, RoutedEventArgs e)
    {
        var rows = SelectedBinRows();
        if (rows.Count == 0 || _bin is null)
        {
            return;
        }

        var what = rows.Count == 1 ? rows[0].Name : $"these {rows.Count} items";
        if (!await ConfirmAsync("Delete permanently?", $"{what} will be deleted for good and cannot be restored.", "Delete"))
        {
            return;
        }

        ShowBinMessage((await _bin.PurgeAsync(rows, CancellationToken.None)).Message);
    }

    private async void OnBinPurgeOldClick(object sender, RoutedEventArgs e)
    {
        if (_bin is null)
        {
            return;
        }

        var files = App.Host.Settings.Current.Files;
        var policy = files.BinPolicy();
        if (policy.MaxAgeDays <= 0 && policy.MaxBytes <= 0)
        {
            ShowBinMessage("Both limits are off. Set how long to keep deleted items in Settings first.");
            return;
        }

        var rule = new List<string>();
        if (policy.MaxAgeDays > 0)
        {
            rule.Add($"deleted {policy.MaxAgeDays} days ago or longer");
        }

        if (policy.MaxBytes > 0)
        {
            rule.Add($"the oldest ones beyond {DisplayFormat.Size(policy.MaxBytes)}");
        }

        if (!await ConfirmAsync("Delete old items for good?", $"This deletes what was {string.Join(", then ", rule)}. It cannot be undone.", "Delete"))
        {
            return;
        }

        ShowBinMessage((await _bin.PurgeOldAsync(CancellationToken.None)).Message);
    }

    private async Task<bool> ConfirmAsync(string title, string text, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = text,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
