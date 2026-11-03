namespace Leviathan.ViewModels.Windows;

/// <summary>
/// The width rules of the sidebar (spec §4): a splitter between the tree and the file view with minimum and maximum widths,
/// double-click to collapse and restore, and keys for resizing. Pure, so the limits are tested and the window and the saved
/// session agree on them.
/// </summary>
public static class SidebarLayout
{
    /// <summary>Narrowest the sidebar can be dragged to: names and the usage bar stay readable.</summary>
    public const double MinWidth = 140;

    /// <summary>Width of a new window's sidebar.</summary>
    public const double DefaultWidth = 220;

    /// <summary>Widest the sidebar can be, whatever the window.</summary>
    public const double MaxWidth = 520;

    /// <summary>The sidebar never takes more than this share of the window, so the file view always has room.</summary>
    public const double MaxFraction = 0.5;

    /// <summary>What an arrow key on the splitter moves it by.</summary>
    public const double KeyStep = 16;

    /// <summary>Clamps <paramref name="width"/> into the allowed range for a window <paramref name="windowWidth"/> wide.</summary>
    public static double Clamp(double width, double windowWidth)
    {
        var upper = Math.Max(MinWidth, Math.Min(MaxWidth, windowWidth * MaxFraction));
        return double.IsNaN(width) ? DefaultWidth : Math.Clamp(width, MinWidth, upper);
    }
}
