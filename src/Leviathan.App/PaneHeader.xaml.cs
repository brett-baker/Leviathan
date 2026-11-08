using Leviathan.ViewModels.Drives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The summary above one pane (spec §8.7): the drive or share the folder is on, and what Leviathan has counted in the folder. The
/// window owns what it says; this only draws it, folds to a thin bar, and reports the three buttons. Every pane has its own, so in a
/// split each header is as wide as the pane it describes.
/// </summary>
internal sealed partial class PaneHeader : UserControl
{
    /// <summary>Creates the header, hidden until it is shown.</summary>
    public PaneHeader() => InitializeComponent();

    /// <summary>"Open terminal here" was clicked.</summary>
    public event EventHandler? TerminalRequested;

    /// <summary>"Properties" was clicked.</summary>
    public event EventHandler? PropertiesRequested;

    /// <summary>The fold or unfold button was clicked.</summary>
    public event EventHandler? FoldToggled;

    /// <summary>Hides the header (the pane has no drive to describe).</summary>
    public void Clear() => Visibility = Visibility.Collapsed;

    /// <summary>Shows the summary of <paramref name="summary"/>, with <paramref name="counted"/> under it when the folder has a count.</summary>
    /// <param name="path">The folder the pane shows: all the folded bar says, and the first line when unfolded.</param>
    /// <param name="summary">The drive or share the folder is on.</param>
    /// <param name="counted">What is counted in the folder, or empty.</param>
    /// <param name="folded">True to show only the thin bar.</param>
    /// <param name="canTerminal">False when a terminal cannot be opened in this folder (a network path).</param>
    public void Show(string path, DriveSummary summary, string counted, bool folded, bool canTerminal)
    {
        PathText.Text = path;
        TitleText.Text = summary.Title;
        TypeText.Text = summary.TypeText;
        UsageText.Text = summary.UsageText;
        UsageBar.Value = summary.UsedFraction * 100;
        UsageBar.Foreground = SidebarFormat.BarBrush(summary.IsLowOnSpace);
        CountedText.Text = counted;
        CountedText.Visibility = counted.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TerminalButton.IsEnabled = canTerminal;

        Details.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        FoldedText.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
        FoldedText.Text = path;
        TerminalButton.Visibility = PropertiesButton.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        FoldIcon.Glyph = folded ? "" : "";
        ToolTipService.SetToolTip(FoldButton, folded ? "Show the drive and folder summary" : "Fold the drive and folder summary");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FoldButton, folded ? "Show the summary" : "Fold the summary");
        ((Microsoft.UI.Xaml.Controls.Border)Content).Padding = folded ? new Thickness(16, 6, 16, 6) : new Thickness(16, 12, 16, 12);
        Visibility = Visibility.Visible;
    }

    private void OnTerminalClick(object sender, RoutedEventArgs e) => TerminalRequested?.Invoke(this, EventArgs.Empty);

    private void OnPropertiesClick(object sender, RoutedEventArgs e) => PropertiesRequested?.Invoke(this, EventArgs.Empty);

    private void OnFoldClick(object sender, RoutedEventArgs e) => FoldToggled?.Invoke(this, EventArgs.Empty);
}
