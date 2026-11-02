using Leviathan.Core.Model;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Drives;
using Leviathan.ViewModels.StatusBar;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>The drive and share summary card above the file list (spec §8.7).</summary>
public sealed partial class MainWindow
{
    /// <summary>Shows the card when the tab is at the top of a drive or share and its card is not hidden; fills it from the drive's details.</summary>
    private void UpdateDriveCard()
    {
        var location = _bound?.History.Current;
        var drive = location is not null && DriveSummary.IsDriveRoot(location) && !App.Host.DriveCards.IsHidden(location)
            ? StatusBarText.DriveOf(location, _drives)
            : null;
        if (drive is null)
        {
            DriveCard.Visibility = Visibility.Collapsed;
            return;
        }

        var summary = DriveSummary.From(drive);
        CardTitle.Text = summary.Title;
        CardType.Text = summary.TypeText;
        CardUsage.Text = summary.UsageText;
        CardBar.Value = summary.UsedFraction * 100;
        CardBar.Foreground = SidebarFormat.BarBrush(summary.IsLowOnSpace);
        DriveCard.Visibility = Visibility.Visible;
    }

    private void OnCardTerminalClick(object sender, RoutedEventArgs e) => Run(CommandIds.TerminalHere);

    private void OnCardPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (_bound?.History.Current is { } root)
        {
            var entry = new FileEntry(root.Name, root, FileEntryKind.Folder, 0, DateTimeOffset.Now, DateTimeOffset.Now, FileAttributes.Directory);
            Run(CommandIds.Properties, root.Value, entry);
        }
    }

    private void OnCardHideClick(object sender, RoutedEventArgs e)
    {
        if (_bound?.History.Current is { } root)
        {
            Run(CommandIds.DriveCardToggle, root.Value);
        }
    }
}
