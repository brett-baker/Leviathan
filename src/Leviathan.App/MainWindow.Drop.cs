using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sidebar;
using Leviathan.ViewModels.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>Dropping files on the tab strip, the sidebar tree, the breadcrumb and the file views (spec §4.1, §5.2, §7, §8.4).</summary>
public sealed partial class MainWindow
{
    /// <summary>Hovering over a tab this long switches to it, so the drop can go into one of its subfolders (spec §4.1).</summary>
    private static readonly TimeSpan TabHoverDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>Hovering over a collapsed tree node this long expands it (spring-loading, spec §7).</summary>
    private static readonly TimeSpan TreeHoverDelay = TimeSpan.FromMilliseconds(700);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverTimer;
    private object? _hoverTarget;

    /// <summary>Carries out a drop on a folder: the command checks the rules and hands the work to the transfer service.</summary>
    private void OnItemsDropped(object? sender, ItemDropRequest request) =>
        Run(CommandIds.DropItems, request.Destination.Value, new ItemDrop(request.Sources, request.Modifiers));

    /// <summary>A copy or move the worker was running ended: say how it went and refresh the folder if it was touched.</summary>
    private void OnTransferFinished(object? sender, TransferFinished finished) => OnUi(() =>
    {
        var message = finished.Describe();

        if (_bound is { } tab)
        {
            var here = tab.History.Current;
            if (here == finished.Destination || finished.Sources.Any(s => s.Parent == here))
            {
                Run(CommandIds.NavReload);
            }
        }

        // A failure is not written into the footer: the failure mark next to the operations chip says it, and the Operations tab has
        // the full text. Everything else (done, cancelled) is a one-line message.
        if (finished.Job.State != Leviathan.Contracts.JobState.Failed)
        {
            MessageText.Text = message;
        }
    });

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var tcs = new TaskCompletionSource<bool>();
        OnUi(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            tcs.SetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
        });
        return await tcs.Task;
    }

    /// <summary>Starts (or keeps) the countdown for <paramref name="target"/>; <paramref name="fire"/> runs if the pointer stays there.</summary>
    private void HoverOver(object target, TimeSpan delay, Action fire)
    {
        if (ReferenceEquals(_hoverTarget, target))
        {
            return;
        }

        StopHover();
        _hoverTarget = target;
        _hoverTimer ??= DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = delay;
        _hoverTimer.IsRepeating = false;
        void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
        {
            timer.Tick -= Tick;
            _hoverTarget = null;
            fire();
        }

        _hoverTimer.Tick += Tick;
        _hoverTimer.Start();
    }

    private void StopHover()
    {
        _hoverTarget = null;
        _hoverTimer?.Stop();
    }

    // Tab strip --------------------------------------------------------------------------------------------------------

    private void MakeTabAFileDropTarget(TabViewItem item)
    {
        // The tab view handles drags of its own tabs on the items and may mark any drag handled, so listen even then.
        item.AllowDrop = true;
        item.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnTabItemDragOver), handledEventsToo: true);
        item.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler((_, _) => StopHover()), handledEventsToo: true);
        item.AddHandler(UIElement.DropEvent, new DragEventHandler(OnTabItemDrop), handledEventsToo: true);
    }

    private void OnTabItemDragOver(object sender, DragEventArgs e)
    {
        if (sender is not TabViewItem { Tag: TabViewModel tab } || !FileDrag.Accepts(e))
        {
            return;
        }

        e.Handled = true;
        FileDrag.Over(e, tab.History.Current);
        if (!ReferenceEquals(_viewModel.Tabs.Selected, tab))
        {
            HoverOver(tab, TabHoverDelay, () =>
            {
                App.Actions.Record("tab.hoverSwitch", tab.History.Current.Value);
                _viewModel.Tabs.Select(tab);
            });
        }
    }

    private async void OnTabItemDrop(object sender, DragEventArgs e)
    {
        StopHover();
        if (sender is not TabViewItem { Tag: TabViewModel tab } || !FileDrag.Accepts(e))
        {
            return;
        }

        e.Handled = true;
        var modifiers = FileDrag.ModifiersOf(e);
        var deferral = e.GetDeferral();
        try
        {
            var sources = await FileDrag.SourcesAsync(e);
            if (sources.Count > 0)
            {
                OnItemsDropped(this, new ItemDropRequest(tab.History.Current, sources, modifiers));
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Sidebar tree -----------------------------------------------------------------------------------------------------

    private static Brush SidebarDropBrush =>
        Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Windows.UI.Color accent
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, accent.R, accent.G, accent.B))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0x00, 0x78, 0xD4));

    private static bool IsFavoritesHeader(SidebarNode node) => node.Kind == SidebarNodeKind.Section && node.Title == "Favorites";

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        if (sender is not Grid { DataContext: SidebarNode node } row || !FileDrag.Accepts(e))
        {
            return;
        }

        e.Handled = true;
        if (IsFavoritesHeader(node))
        {
            // Anything can be pinned; a pin is neither a copy nor a move.
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Add to Favorites";
            e.DragUIOverride.IsCaptionVisible = true;
            row.BorderBrush = SidebarDropBrush;
            return;
        }

        if (!node.IsFolderLike || node.Location is not { } destination)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            row.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }

        var decision = FileDrag.Over(e, destination);
        row.BorderBrush = decision.Allowed ? SidebarDropBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (node.CanExpand && !node.IsExpanded)
        {
            HoverOver(node, TreeHoverDelay, () => _ = _sidebar.ExpandAsync(node, _closing.Token));
        }
    }

    private void OnSidebarDragLeave(object sender, DragEventArgs e)
    {
        StopHover();
        if (sender is Grid row)
        {
            row.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private async void OnSidebarDrop(object sender, DragEventArgs e)
    {
        StopHover();
        if (sender is not Grid { DataContext: SidebarNode node } row || !FileDrag.Accepts(e))
        {
            return;
        }

        e.Handled = true;
        row.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var modifiers = FileDrag.ModifiersOf(e);
        var deferral = e.GetDeferral();
        try
        {
            var sources = await FileDrag.SourcesAsync(e);
            if (IsFavoritesHeader(node))
            {
                foreach (var source in sources.Where(s => s.Kind is Core.Paths.LocationKind.Drive or Core.Paths.LocationKind.Unc))
                {
                    Run(CommandIds.FavoriteAdd, source.Value);
                }
            }
            else if (node.IsFolderLike && node.Location is { } destination && sources.Count > 0)
            {
                OnItemsDropped(this, new ItemDropRequest(destination, sources, modifiers));
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
