using Leviathan.Core.Model;
using Leviathan.SpaceMap;
using Leviathan.ViewModels.Hub;
using Leviathan.ViewModels.SpaceMap;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Leviathan.App;

/// <summary>What the Space tab needs from the file view: the items listed and the counted size of a folder.</summary>
/// <param name="Entries">The items in the folder shown.</param>
/// <param name="FolderBytes">The counted total of a folder, or null when it is not counted yet.</param>
internal sealed record SpaceTabSource(IReadOnlyList<FileEntry> Entries, Func<FileEntry, long?> FolderBytes);

/// <summary>
/// The Space tab of the status hub (spec §14, §16.1): a compact treemap of the folder in the file view with its largest items beside it. Clicking a rectangle or a
/// line selects that item in the file view; "Open space map" opens the full map. It follows the folder while the tab is open and fills in as folder sizes are counted.
/// </summary>
internal sealed partial class StatusHubPanel
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _spaceTimer;
    private SpaceTabData? _spaceData;
    private string _spaceStamp = string.Empty;
    private IReadOnlyList<SpaceTabItem> _spaceItems = [];

    /// <summary>Where the tab reads the folder shown; set by the window.</summary>
    internal Func<SpaceTabSource?>? SpaceSource { get; set; }

    /// <summary>An item of the tab was clicked: select it in the file view.</summary>
    internal event EventHandler<FileEntry>? SpaceItemChosen;

    /// <summary>"Open space map" was clicked.</summary>
    internal event EventHandler? OpenSpaceMapRequested;

    private void SyncSpaceTimer()
    {
        var visible = _model.IsOpen && _model.Tab == HubTab.Space;
        if (visible)
        {
            _spaceTimer ??= CreateSpaceTimer();
            if (!_spaceTimer.IsRunning)
            {
                _spaceTimer.Start();
            }

            _spaceStamp = string.Empty;
            RefreshSpace();
        }
        else
        {
            _spaceTimer?.Stop();
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSpaceTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Tick += (_, _) => RefreshSpace();
        return timer;
    }

    /// <summary>Redraws the tab if the folder or its sizes changed since it was last drawn.</summary>
    internal void RefreshSpace()
    {
        if (!_ready || !_model.IsOpen || _model.Tab != HubTab.Space || SpaceSource?.Invoke() is not { } source)
        {
            return;
        }

        var width = (int)SpaceMapHost.ActualWidth;
        var height = (int)SpaceMapHost.ActualHeight;
        if (width < 40 || height < 40)
        {
            return;
        }

        // A cheap fingerprint of what the tab would show, so a quiet folder costs nothing to keep open.
        long sum = 0;
        var counted = 0;
        foreach (var entry in source.Entries)
        {
            if (entry.Kind == FileEntryKind.Folder)
            {
                if (source.FolderBytes(entry) is { } total)
                {
                    sum += total;
                    counted++;
                }
            }
            else
            {
                sum += entry.Size;
            }
        }

        var stamp = $"{source.Entries.Count}|{sum}|{counted}|{width}x{height}";
        if (stamp == _spaceStamp)
        {
            return;
        }

        _spaceStamp = stamp;
        var data = SpaceTabModel.Build([.. source.Entries], source.FolderBytes, width, height, DateTimeOffset.UtcNow);
        _spaceData = data;
        _spaceItems = data.Items;
        SpaceSummary.Text = data.Summary;
        var dark = ActualTheme == ElementTheme.Dark;
        SpaceMapImage.Source = TreemapBitmap.Render(data.Tiles, ColorMode.Type, width, height, dark, null);
        SpaceItems.ItemsSource = data.Items.Select(i => $"{i.Entry.Name}{(i.Entry.Kind == FileEntryKind.Folder ? "/" : string.Empty)}  ·  {i.SizeText}{(i.Fraction >= 0.01 ? $"  ·  {i.Fraction:0%}" : string.Empty)}").ToList();
    }

    private void OnSpaceMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _spaceStamp = string.Empty;
        RefreshSpace();
    }

    private void OnSpaceMapPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SpaceMapImage).Position;
        if (_spaceData is not null && SpaceTabModel.EntryAt(_spaceData, point.X, point.Y) is { } entry)
        {
            SpaceItemChosen?.Invoke(this, entry);
        }
    }

    private void OnSpaceItemClick(object sender, ItemClickEventArgs e)
    {
        var at = SpaceItems.Items.IndexOf(e.ClickedItem);
        if (at >= 0 && at < _spaceItems.Count)
        {
            SpaceItemChosen?.Invoke(this, _spaceItems[at].Entry);
        }
    }

    private void OnOpenSpaceMapClick(object sender, RoutedEventArgs e) => OpenSpaceMapRequested?.Invoke(this, EventArgs.Empty);
}
