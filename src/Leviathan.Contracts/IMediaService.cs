using Leviathan.Media;

namespace Leviathan.Contracts;

/// <summary>Information the media process reports about itself.</summary>
/// <param name="ContractMajor">Contract major version the process speaks (the same number as the worker's).</param>
/// <param name="ContractMinor">Contract minor version.</param>
/// <param name="AppVersion">Informational version including the commit SHA.</param>
/// <param name="ProcessId">Process ID.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="FfprobePath">Where ffprobe was found, or null when it was not.</param>
/// <param name="FfprobeSource">Which rule found it ("setting", "app folder", "PATH", "not found").</param>
/// <param name="QuarantinedAtStart">Files that were being read when the previous process died, now marked unreadable.</param>
public sealed record MediaHostInfo(int ContractMajor, int ContractMinor, string AppVersion, int ProcessId, DateTimeOffset StartedAt, string? FfprobePath, string FfprobeSource, int QuarantinedAtStart);

/// <summary>One file to ask about: which version of it (path, size, modified) and whether it is on a network share.</summary>
/// <param name="Path">The Win32 path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ModifiedUtcTicks">Last write time, UTC ticks.</param>
/// <param name="IsNetwork">True on a share, where ffprobe reads only the container header at first.</param>
public sealed record MediaProbeRequest(string Path, long Size, long ModifiedUtcTicks, bool IsNetwork);

/// <summary>How a probe ended, as the UI needs to know it.</summary>
public enum MediaProbeStatus
{
    /// <summary>The metadata is in <see cref="MediaProbeAnswer.Info"/>.</summary>
    Ok,

    /// <summary>The file is unreadable (damaged, not media, timed out, or it stopped the process) until it changes; see the message.</summary>
    Unreadable,

    /// <summary>ffprobe is not installed; nothing was remembered about the file.</summary>
    ToolMissing,
}

/// <summary>The answer for one file.</summary>
/// <param name="Path">The file the answer is for.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Info">The metadata (<see cref="MediaProbeStatus.Ok"/> only).</param>
/// <param name="Message">Why the file is unreadable or the tool missing.</param>
public sealed record MediaProbeAnswer(string Path, MediaProbeStatus Status, MediaInfo? Info, string? Message);

/// <summary>How the media process is doing (the Background tab, spec §16.1).</summary>
/// <param name="Running">Probes running now.</param>
/// <param name="Waiting">Probes waiting for a slot.</param>
/// <param name="Probed">Files probed since the process started.</param>
/// <param name="Unreadable">Files currently marked unreadable.</param>
public sealed record MediaHostStatus(int Running, int Waiting, long Probed, int Unreadable);

/// <summary>A video thumbnail wanted from the media process (spec §10.4).</summary>
/// <param name="Path">Win32 path of the video.</param>
/// <param name="Size">Size in bytes (part of the cache identity).</param>
/// <param name="ModifiedUtcTicks">Last write time in UTC ticks.</param>
/// <param name="IsNetwork">True for a file on a network drive (read with limits).</param>
/// <param name="Edge">Longest edge of the picture in pixels.</param>
public sealed record MediaThumbnailRequest(string Path, long Size, long ModifiedUtcTicks, bool IsNetwork, int Edge);

/// <summary>Whether a thumbnail could be drawn.</summary>
public enum MediaThumbnailStatus
{
    /// <summary>The JPEG is in <see cref="MediaThumbnailAnswer.Jpeg"/>.</summary>
    Ok,

    /// <summary>No frame could be drawn from the file.</summary>
    Failed,

    /// <summary>ffmpeg is not installed.</summary>
    ToolMissing,
}

/// <summary>A thumbnail as a small JPEG, or why there is none.</summary>
/// <param name="Path">The video.</param>
/// <param name="Status">What happened.</param>
/// <param name="Jpeg">The picture.</param>
/// <param name="Message">Why there is none.</param>
public sealed record MediaThumbnailAnswer(string Path, MediaThumbnailStatus Status, byte[]? Jpeg, string? Message);

/// <summary>A contact sheet wanted from the media process (spec §10.4).</summary>
/// <param name="Path">Win32 path of the video.</param>
/// <param name="Size">Size in bytes (part of the cache identity).</param>
/// <param name="ModifiedUtcTicks">Last write time in UTC ticks.</param>
/// <param name="IsNetwork">True for a file on a network drive (read with limits).</param>
/// <param name="Columns">Frames per row, 3 to 6.</param>
/// <param name="Rows">Rows, 3 to 6.</param>
/// <param name="CellWidth">Width of one frame in pixels, 96 to 480.</param>
public sealed record MediaContactSheetRequest(string Path, long Size, long ModifiedUtcTicks, bool IsNetwork, int Columns, int Rows, int CellWidth);

/// <summary>A contact sheet as one JPEG with the moments its frames were taken at, or why there is none.</summary>
/// <param name="Path">The video.</param>
/// <param name="Status">What happened.</param>
/// <param name="Jpeg">The sheet.</param>
/// <param name="Columns">Frames per row as drawn.</param>
/// <param name="Rows">Rows as drawn.</param>
/// <param name="CellWidth">Width of one frame as drawn.</param>
/// <param name="CellHeight">Height of one frame as drawn.</param>
/// <param name="FrameSeconds">Where in the video each frame is from, row by row, so a click on a frame can play from there.</param>
/// <param name="Message">Why there is none.</param>
public sealed record MediaContactSheetAnswer(string Path, MediaThumbnailStatus Status, byte[]? Jpeg, int Columns, int Rows, int CellWidth, int CellHeight, double[] FrameSeconds, string? Message);

/// <summary>
/// The media process (Leviathan.Media), called by the UI over a named pipe (spec §2.2, §9.3): ffprobe metadata with a cache, in a process
/// of its own so a file that hangs or kills a media tool costs a restart of this process, never the window. Cancelling a call (the
/// folder changed, the row scrolled away) cancels the probe if it has not started.
/// </summary>
public interface IMediaService
{
    /// <summary>Returns versions and where ffprobe is; also a heartbeat.</summary>
    Task<MediaHostInfo> PingAsync(CancellationToken cancellationToken);

    /// <summary>The metadata of one file: from the cache when this version has been probed, otherwise by probing it (up to 10 s).</summary>
    Task<MediaProbeAnswer> ProbeAsync(MediaProbeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// What is already known about these files, in one round trip and without starting a probe. A file that has not been probed is left
    /// out of the answer. A grid asks this first for the rows it shows, then probes only the rest.
    /// </summary>
    Task<IReadOnlyList<MediaProbeAnswer>> PeekAsync(IReadOnlyList<MediaProbeRequest> requests, CancellationToken cancellationToken);

    /// <summary>Counters for the Background tab.</summary>
    Task<MediaHostStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Forgets what is known about a path, so the next request probes it again ("retry this file"). True when there was an answer.</summary>
    Task<bool> RetryAsync(string path, CancellationToken cancellationToken);

    /// <summary>The files currently marked unreadable, newest first, with their reasons.</summary>
    Task<IReadOnlyList<MediaProbeAnswer>> GetUnreadableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A frame of a video as a JPEG, from the disk cache or drawn by ffmpeg now (spec §10.4). Two are drawn at a time; a call whose
    /// token is cancelled before its turn never starts, and one already running is stopped.
    /// </summary>
    Task<MediaThumbnailAnswer> GetThumbnailAsync(MediaThumbnailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// A grid of 3×3 to 6×6 frames taken evenly across a video by keyframe seeking, as one JPEG (spec §10.4), from the disk cache or
    /// drawn now. One is drawn at a time; a call whose token is cancelled before its turn never starts, and one already running is stopped.
    /// </summary>
    Task<MediaContactSheetAnswer> GetContactSheetAsync(MediaContactSheetRequest request, CancellationToken cancellationToken);
}
