using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// The footer's note about rows that came from the cache (spec §3.3): a spinner and "Showing the listing from 3 hours ago while the
/// folder is checked; items may change" while the real folder is being listed, "Updated: 2 added, 1 removed" for a few seconds
/// afterwards, and a warning when the check could not be made. Nothing shows when the rows are exactly what the folder holds.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The rows of a share that does not answer are greyed (spec §3.3), so they cannot be mistaken for a fresh listing.</summary>
    private const double OfflineRowsOpacity = 0.55;

    // The banner above the rows (spec §3.3, §8.6): "\\nas is not responding — showing the listing as of 14:02", with Retry and
    // Work offline (or Go online, when the user already chose to work offline). Hidden while the rows are a live listing.
    private void UpdateOfflineBanner()
    {
        var info = _bound?.Folder.OfflineListing;
        OfflineBar.IsOpen = info is not null;
        var opacity = info is null ? 1.0 : OfflineRowsOpacity;
        FileList.Opacity = opacity;
        FlowList.Opacity = opacity;
        if (info is null)
        {
            return;
        }

        OfflineBar.Title = info.WorkingOffline ? "Working offline" : "Not responding";
        OfflineBar.Message = info.Text(DateTimeOffset.UtcNow);
        OfflineToggleButton.Content = info.WorkingOffline ? "Go online" : "Work offline";
    }

    private void OnOfflineRetryClick(object sender, RoutedEventArgs e) => Run(CommandIds.NavReload);

    // Work offline stops all network calls to the server until the user goes online again; going online (or Retry on a server that
    // is back) lists the folder for real. Offline mode lasts for this run.
    private void OnOfflineToggleClick(object sender, RoutedEventArgs e)
    {
        if (_bound?.Folder is not { OfflineListing: { } info, Location: { } location })
        {
            return;
        }

        App.Host.WorkOffline.Set(location, !info.WorkingOffline);
        Run(CommandIds.NavReload);
    }

    private void ShowListingNotice(ListingNotice? notice)
    {
        if (notice is null)
        {
            NoticePart.Visibility = Visibility.Collapsed;
            NoticeRing.IsActive = false;
            return;
        }

        NoticePart.Visibility = Visibility.Visible;
        var checking = notice.Kind == ListingNoticeKind.Checking;
        NoticeRing.IsActive = checking;
        NoticeRing.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        NoticeIcon.Visibility = checking ? Visibility.Collapsed : Visibility.Visible;
        var failed = notice.Kind == ListingNoticeKind.RefreshFailed;
        NoticeIcon.Content = checking ? null : Icons.Of(failed ? "warning" : "check", 14);
        var brush = (Brush)Application.Current.Resources[failed ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
        NoticeText.Foreground = brush;
        NoticeText.Text = notice.Text;
        ToolTipService.SetToolTip(NoticePart, notice.Text);
    }
}
