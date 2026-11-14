using Leviathan.Core.Paths;
using Leviathan.ViewModels.Sync;

namespace Leviathan.App;

/// <summary>Compare and sync (spec §15.5): the window for two folders, normally the two panes of a split tab.</summary>
public sealed partial class MainWindow
{
    /// <inheritdoc />
    public void OpenSync(LocationPath? left, LocationPath? right) => OnUi(() =>
    {
        var host = App.Host;
        new SyncWindow(left, right, new WorkerCompare(() => host.WorkerConnection), new SyncRunner(host.Transfers, host.Transfers), host.SyncPairs).Activate();
    });

    /// <inheritdoc />
    public void OpenSyncPair(Guid id) => OnUi(() =>
    {
        var host = App.Host;
        if (host.SyncPairs.Find(id) is not { } pair)
        {
            return;
        }

        // The pair's folders are compared at once, so the preview is already waiting when the window shows.
        var left = LocationPath.TryParse(pair.Left, out var l) ? l : null;
        var right = LocationPath.TryParse(pair.Right, out var r) ? r : null;
        new SyncWindow(left, right, new WorkerCompare(() => host.WorkerConnection), new SyncRunner(host.Transfers, host.Transfers), host.SyncPairs, pair).Activate();
    });

    // Right-click on a saved pair in Favorites: open it, check it now, or remove it.
    private void ShowSyncPairMenu(Guid id, Microsoft.UI.Xaml.FrameworkElement anchor, Windows.Foundation.Point position)
    {
        var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        var open = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Open and compare" };
        open.Click += (_, _) => OpenSyncPair(id);
        var check = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Check now" };
        check.Click += async (_, _) =>
        {
            if (App.Host.SyncPairs.Find(id) is { } pair)
            {
                await Task.Run(() => App.Host.SyncChecker.CheckNowAsync(pair, CancellationToken.None));
            }
        };
        var remove = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Remove sync pair" };
        remove.Click += (_, _) => App.Host.SyncPairs.Remove(id);
        menu.Items.Add(open);
        menu.Items.Add(check);
        menu.Items.Add(remove);
        menu.ShowAt(anchor, position);
    }
}
