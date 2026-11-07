namespace Leviathan.ViewModels.Folders;

/// <summary>
/// The arithmetic of hover scrub (spec §10.5): moving the mouse across a video's picture slides through the video, the horizontal
/// position mapping to a time. Pure, so the mapping is tested without a window.
/// </summary>
public static class HoverScrub
{
    /// <summary>How long the mouse rests on a video before its strip is asked for, so sweeping across a folder draws nothing.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(250);

    /// <summary>How far through the video the pointer is, 0 at the left edge and 1 at the right edge (kept in range).</summary>
    public static double Fraction(double x, double width) => width <= 0 ? 0 : Math.Clamp(x / width, 0, 1);

    /// <summary>The frame of a strip of <paramref name="frames"/> frames to show at <paramref name="fraction"/> (0 to 1).</summary>
    public static int FrameAt(double fraction, int frames) =>
        frames <= 0 ? 0 : Math.Clamp((int)(Math.Clamp(fraction, 0, 1) * frames), 0, frames - 1);

    /// <summary>
    /// Where to put the strip's picture so that frame <paramref name="frame"/> alone shows, centred in a box: one frame is scaled to fit
    /// the box keeping its proportions, the strip is that wide times <paramref name="frames"/>, and the box is clipped to the frame so
    /// its neighbours never peek in at the sides.
    /// </summary>
    /// <param name="frame">The frame to show.</param>
    /// <param name="frames">Frames in the strip.</param>
    /// <param name="cellWidth">Width of one frame in the strip picture.</param>
    /// <param name="cellHeight">Height of one frame in the strip picture.</param>
    /// <param name="boxWidth">Width of the space the picture is shown in.</param>
    /// <param name="boxHeight">Height of that space.</param>
    public static ScrubLayout Place(int frame, int frames, int cellWidth, int cellHeight, double boxWidth, double boxHeight)
    {
        if (cellWidth <= 0 || cellHeight <= 0 || boxWidth <= 0 || boxHeight <= 0 || frames <= 0)
        {
            return new ScrubLayout(0, 0, 0, 0, 0, 0, 0);
        }

        var scale = Math.Min(boxWidth / cellWidth, boxHeight / cellHeight);
        var width = cellWidth * scale;
        var height = cellHeight * scale;
        var left = (boxWidth - width) / 2;
        var top = (boxHeight - height) / 2;
        return new ScrubLayout(left, top, width, height, left - Math.Clamp(frame, 0, frames - 1) * width, width * frames, height);
    }
}

/// <summary>Where the hover-scrub picture goes in its box (see <see cref="HoverScrub.Place"/>).</summary>
/// <param name="ClipLeft">Left edge of the visible frame.</param>
/// <param name="ClipTop">Top edge of the visible frame.</param>
/// <param name="ClipWidth">Width of the visible frame.</param>
/// <param name="ClipHeight">Height of the visible frame.</param>
/// <param name="StripLeft">Left edge of the whole strip picture, so the wanted frame lands in the clip.</param>
/// <param name="StripWidth">Width of the whole strip picture.</param>
/// <param name="StripHeight">Height of the strip picture.</param>
public readonly record struct ScrubLayout(double ClipLeft, double ClipTop, double ClipWidth, double ClipHeight, double StripLeft, double StripWidth, double StripHeight);
