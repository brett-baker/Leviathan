namespace Leviathan.ViewModels.Windows;

/// <summary>The three video sizes of the S / M / L buttons in the preview pane header (spec §9).</summary>
public enum VideoSize
{
    /// <summary>A short strip; room for the details below.</summary>
    Small,

    /// <summary>The default.</summary>
    Medium,

    /// <summary>Fills the pane width at 16:9.</summary>
    Large,
}

/// <summary>
/// The size rules of the preview pane (spec §9): a splitter on its left edge with minimum and maximum widths, and a video area
/// whose height is chosen by the S / M / L buttons or by dragging the handle under it. Pure, so the limits are tested and the
/// window and the saved session agree on them (the sidebar's twin, <see cref="SidebarLayout"/>).
/// </summary>
public static class PreviewLayout
{
    /// <summary>Narrowest the pane can be dragged to: the header buttons and a readable line of details still fit.</summary>
    public const double MinWidth = 220;

    /// <summary>Width of a new window's pane.</summary>
    public const double DefaultWidth = 340;

    /// <summary>Widest the pane can be, whatever the window.</summary>
    public const double MaxWidth = 900;

    /// <summary>The pane never takes more than this share of the window, so the file view always has room.</summary>
    public const double MaxFraction = 0.6;

    /// <summary>What an arrow key on the splitter moves it by.</summary>
    public const double KeyStep = 16;

    /// <summary>Shortest the video area can be made.</summary>
    public const double MinVideoHeight = 80;

    /// <summary>Tallest the video area can be made.</summary>
    public const double MaxVideoHeight = 900;

    /// <summary>Share of the full-width 16:9 height that the small preset takes.</summary>
    public const double SmallShare = 0.5;

    /// <summary>Share of the full-width 16:9 height that the medium preset takes.</summary>
    public const double MediumShare = 0.75;

    /// <summary>Space the pane's own padding and borders take from its width.</summary>
    public const double HorizontalChrome = 32;

    /// <summary>Clamps <paramref name="width"/> into the allowed range for a window <paramref name="windowWidth"/> wide.</summary>
    public static double Clamp(double width, double windowWidth)
    {
        var upper = Math.Max(MinWidth, Math.Min(MaxWidth, windowWidth * MaxFraction));
        return double.IsNaN(width) ? DefaultWidth : Math.Clamp(width, MinWidth, upper);
    }

    /// <summary>
    /// The height of the video area for a preset, as a share of the 16:9 height of the pane's width: large fills the width (spec §9, "L
    /// fills the pane width"), medium is three quarters of that and small half, so the three always differ and follow the pane as it is
    /// resized.
    /// </summary>
    public static double HeightFor(VideoSize size, double paneWidth)
    {
        var full = Math.Max(0, paneWidth - HorizontalChrome) * 9 / 16;
        return ClampHeight(size switch
        {
            VideoSize.Small => full * SmallShare,
            VideoSize.Large => full,
            _ => full * MediumShare,
        });
    }

    /// <summary>Clamps a dragged height into the allowed range; not a number gives the medium height.</summary>
    public static double ClampHeight(double height) => double.IsNaN(height) ? HeightFor(VideoSize.Medium, DefaultWidth) : Math.Clamp(height, MinVideoHeight, MaxVideoHeight);
}
