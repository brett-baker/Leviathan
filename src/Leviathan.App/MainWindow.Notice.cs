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
        NoticeIcon.Glyph = notice.Kind == ListingNoticeKind.RefreshFailed ? "\uE7BA" : "\uE73E";
        var brush = (Brush)Application.Current.Resources[notice.Kind == ListingNoticeKind.RefreshFailed ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
        NoticeText.Foreground = brush;
        NoticeIcon.Foreground = brush;
        NoticeText.Text = notice.Text;
        ToolTipService.SetToolTip(NoticePart, notice.Text);
    }
}
