using Leviathan.ViewModels.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The navigation buttons and address bar of one pane of a split tab (spec §15.1). The window hands it the pane's tab and hooks the same
/// handlers the window-level bar uses: touching a pane gives it the focus first, so those handlers act on this pane.
/// </summary>
public sealed partial class PaneNavBar : UserControl
{
    /// <summary>Creates the bar.</summary>
    public PaneNavBar() => InitializeComponent();

    /// <summary>The address bar of this pane.</summary>
    public AddressBar AddressBox => Address;

    /// <summary>Back was clicked.</summary>
    public event RoutedEventHandler BackClick { add => BackButton.Click += value; remove => BackButton.Click -= value; }

    /// <summary>Forward was clicked.</summary>
    public event RoutedEventHandler ForwardClick { add => ForwardButton.Click += value; remove => ForwardButton.Click -= value; }

    /// <summary>Up was clicked.</summary>
    public event RoutedEventHandler UpClick { add => UpButton.Click += value; remove => UpButton.Click -= value; }

    /// <summary>Reload was clicked.</summary>
    public event RoutedEventHandler ReloadClick { add => ReloadButton.Click += value; remove => ReloadButton.Click -= value; }

    // The buttons give way to the address bar when the pane is narrow (spec §15.1); NavButtonLayout decides which, in what order.
    private void OnRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var shown = NavButtonLayout.Visible(e.NewSize.Width - Row.Padding.Left - Row.Padding.Right);
        BackButton.Visibility = Show(shown, NavButtons.Back);
        ForwardButton.Visibility = Show(shown, NavButtons.Forward);
        UpButton.Visibility = Show(shown, NavButtons.Up);
        ReloadButton.Visibility = Show(shown, NavButtons.Reload);
    }

    private static Visibility Show(NavButtons shown, NavButtons button) => shown.HasFlag(button) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Draws the four buttons from the icon pack, now and whenever the pack or theme changes.</summary>
    public void BindIcons()
    {
        Icons.Bind(BackButton, static (b, i) => b.Content = i, "back");
        Icons.Bind(ForwardButton, static (b, i) => b.Content = i, "forward");
        Icons.Bind(UpButton, static (b, i) => b.Content = i, "up");
        Icons.Bind(ReloadButton, static (b, i) => b.Content = i, "reload");
    }

    /// <summary>Enables each button by what the pane's history allows.</summary>
    public void SetEnabled(bool back, bool forward, bool up, bool reload)
    {
        BackButton.IsEnabled = back;
        ForwardButton.IsEnabled = forward;
        UpButton.IsEnabled = up;
        ReloadButton.IsEnabled = reload;
    }
}
