using Leviathan.Core.Model;
using Leviathan.Media;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Triage;

/// <summary>The two lines of the triage overlay (spec §10.1): the file name, and what is known about the file.</summary>
/// <param name="Title">The file name.</param>
/// <param name="Detail">"1.4 GB · 1:42:10 · 3840×2160 · HEVC · in \nas\inbox", leaving out what is not known yet.</param>
public sealed record OverlayText(string Title, string Detail);

/// <summary>
/// What the overlay says about the current file: size from the listing, duration from the player (known as soon as it opens),
/// resolution and codec from the cached probe when it has arrived, and the folder, so a recursive queue says where a file is from. Pure
/// text, so it is tested without a window and a late probe only adds to the line.
/// </summary>
public static class TriageOverlayText
{
    /// <summary>Describes <paramref name="file"/>.</summary>
    /// <param name="file">The file.</param>
    /// <param name="duration">Its length when the player knows it.</param>
    /// <param name="info">The probe result when it has arrived.</param>
    public static OverlayText Describe(FileEntry file, TimeSpan? duration, MediaInfo? info)
    {
        ArgumentNullException.ThrowIfNull(file);
        var parts = new List<string> { DisplayFormat.Size(file.Size) };
        if ((duration is { } d && d > TimeSpan.Zero ? d : info?.Duration) is { } length && length > TimeSpan.Zero)
        {
            parts.Add(MediaColumns.Duration(length));
        }

        if (info?.PrimaryVideo is { } video)
        {
            if (video.Width is > 0 && video.Height is > 0)
            {
                parts.Add($"{video.Width}×{video.Height}");
            }

            if (!string.IsNullOrWhiteSpace(video.Codec))
            {
                parts.Add(video.Codec.ToUpperInvariant());
            }
        }

        if (file.Location.Parent is { } folder)
        {
            parts.Add("in " + folder.Value);
        }

        return new OverlayText(file.Name, string.Join(" · ", parts));
    }
}
