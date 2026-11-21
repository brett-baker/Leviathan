namespace Leviathan.ViewModels.Panes;

/// <summary>The optional parts of a pane's footer.</summary>
[Flags]
public enum FooterParts
{
    /// <summary>Only the item count, the selection, the filter note and the layout switches.</summary>
    None = 0,

    /// <summary>The share health and link speed ("1 Gb/s").</summary>
    Health = 1,

    /// <summary>Free space on the drive, with its bar.</summary>
    FreeSpace = 2,

    /// <summary>Everything.</summary>
    All = Health | FreeSpace,
}

/// <summary>
/// What a pane's footer has room for (spec §8.7, §15.1). Each pane has its own footer, so a pane of a split can be narrow: the free-space bar
/// goes first, then the health note; the count, selection and layout switches stay. Pure, so the thresholds are tested without a window.
/// </summary>
public static class PaneFooterLayout
{
    /// <summary>The width the always-visible parts need: count, selection, filter note, three layout switches.</summary>
    public const double CoreWidth = 380;

    /// <summary>The width of the health note.</summary>
    public const double HealthWidth = 110;

    /// <summary>The width of free space and its bar.</summary>
    public const double FreeSpaceWidth = 240;

    /// <summary>The optional parts that fit in a footer <paramref name="width"/> wide.</summary>
    public static FooterParts Visible(double width)
    {
        if (double.IsNaN(width))
        {
            return FooterParts.All;
        }

        var parts = FooterParts.None;
        if (width >= CoreWidth + HealthWidth)
        {
            parts |= FooterParts.Health;
        }

        if (width >= CoreWidth + HealthWidth + FreeSpaceWidth)
        {
            parts |= FooterParts.FreeSpace;
        }

        return parts;
    }
}
