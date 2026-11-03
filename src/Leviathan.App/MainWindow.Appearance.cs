using Leviathan.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// The look of this window (spec 17.2 Appearance): the Mica backdrop, the colour of the caption buttons, and a redraw of what
/// depends on the density and the icon size. The theme itself is the application's and is chosen when it starts (see ThemePeek). The accent and the sizes are app-wide and set by <see cref="AppHost"/>, which
/// raises <c>AppearanceChanged</c>; every window listens.
/// </summary>
public sealed partial class MainWindow
{
    private void InitAppearance()
    {
        App.Host.AppearanceChanged += OnAppearanceChanged;
        ApplyAppearance();
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ApplyBackground();
        }
    }

    // Without Mica the window has a plain background. It is the colour of the theme the window is in, not the one of the application
    // (a window set to Dark on a Light system would otherwise keep a light background behind dark controls).
    private void ApplyBackground()
    {
        if (_closed || Content is not FrameworkElement root)
        {
            return;
        }

        if (App.Host.Settings.Current.Appearance.Mica)
        {
            Root.Background = null;
            return;
        }

        var dark = root.ActualTheme == ElementTheme.Dark;
        Root.Background = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3));
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        ApplyAppearance();
        FileList.RefreshMetrics();
        Sidebar.ItemsSource = null;
        Sidebar.ItemsSource = _sidebar.Rows;
    });

    private void ApplyAppearance()
    {
        if (_closed || Content is not FrameworkElement root)
        {
            return;
        }

        var look = App.Host.Settings.Current.Appearance;
        SystemBackdrop = look.Mica ? new Microsoft.UI.Xaml.Media.MicaBackdrop() : null;
        ApplyBackground();

        // The caption buttons are drawn by Windows. When the theme was chosen in the settings they need the colours of the theme the
        // content is drawn in (the application's, fixed when it started); with "same as Windows" they follow the system.
        var buttons = AppWindow.TitleBar;
        Windows.UI.Color? foreground = look.Theme == ThemeMode.System
            ? null
            : Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
        buttons.ButtonForegroundColor = foreground;
        buttons.ButtonHoverForegroundColor = foreground;
        buttons.ButtonPressedForegroundColor = foreground;
        buttons.ButtonInactiveForegroundColor = foreground is { } c ? Windows.UI.Color.FromArgb(255, (byte)((c.R + 128) / 2), (byte)((c.G + 128) / 2), (byte)((c.B + 128) / 2)) : null;
    }
}
