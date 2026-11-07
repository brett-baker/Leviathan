namespace Leviathan.Core.Platform;

/// <summary>Where a player is in a video.</summary>
public enum PlaybackState
{
    /// <summary>Nothing is open.</summary>
    Idle,

    /// <summary>A file is being opened and buffered.</summary>
    Opening,

    /// <summary>The picture is moving.</summary>
    Playing,

    /// <summary>Open and stopped, so a frame step is possible.</summary>
    Paused,

    /// <summary>The end of the file was reached.</summary>
    Ended,

    /// <summary>The file could not be opened or played; see <see cref="PlaybackSnapshot.Error"/>.</summary>
    Failed,
}

/// <summary>An audio stream the player can switch to.</summary>
/// <param name="Id">The engine's number for it.</param>
/// <param name="Label">What to show in a menu ("English · AC3 5.1").</param>
/// <param name="Language">ISO language code, when the file says one.</param>
public sealed record AudioTrack(int Id, string Label, string? Language);

/// <summary>Everything the user interface needs to know about a player, in one immutable value.</summary>
/// <param name="State">Where it is.</param>
/// <param name="Position">Current position (zero until something is open).</param>
/// <param name="Duration">Length of the file (zero while unknown).</param>
/// <param name="Speed">Playback speed, 1 = normal.</param>
/// <param name="Volume">Volume from 0 to 1.</param>
/// <param name="Muted">True when the sound is off.</param>
/// <param name="AudioTracks">The audio streams of the file.</param>
/// <param name="AudioTrackId">The one playing, or null.</param>
/// <param name="FramesPerSecond">Frame rate of the picture when known; frame stepping uses it.</param>
/// <param name="Error">Why it failed.</param>
public sealed record PlaybackSnapshot(
    PlaybackState State,
    TimeSpan Position,
    TimeSpan Duration,
    double Speed,
    double Volume,
    bool Muted,
    IReadOnlyList<AudioTrack> AudioTracks,
    int? AudioTrackId,
    double? FramesPerSecond,
    string? Error)
{
    /// <summary>A player with nothing open.</summary>
    public static PlaybackSnapshot Closed { get; } = new(PlaybackState.Idle, TimeSpan.Zero, TimeSpan.Zero, 1, 1, false, [], null, null, null);

    /// <summary>True while a file is open and not failed, so seeking makes sense.</summary>
    public bool IsOpen => State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended;
}

/// <summary>
/// A video player behind the interface the rest of Leviathan uses (spec §2.4, D10): libmpv and LibVLC each implement it, a fake
/// stands in for tests. Every call is asynchronous and may be cancelled; none throws for a file that will not play, it ends in
/// <see cref="PlaybackState.Failed"/> with the reason, because a bad file must never take the window down (spec §3). Playback streams
/// from the file's own location: a file on a share is never copied to be viewed (spec §10.1).
/// <para>
/// <see cref="Changed"/> may be raised on any thread; a view model marshals it to the UI thread and batches it, since a playing video
/// reports its position many times a second (spec §3.2).
/// </para>
/// </summary>
public interface IVideoEngine : IAsyncDisposable
{
    /// <summary>A short name for settings and logs ("libmpv", "LibVLC").</summary>
    string Name { get; }

    /// <summary>The state as of now.</summary>
    PlaybackSnapshot Snapshot { get; }

    /// <summary>Raised whenever the snapshot changes.</summary>
    event EventHandler<PlaybackSnapshot>? Changed;

    /// <summary>Opens <paramref name="path"/> (a Win32 or UNC path) at <paramref name="start"/>, playing at once when <paramref name="autoplay"/>.</summary>
    Task OpenAsync(string path, TimeSpan start, bool autoplay, CancellationToken cancellationToken);

    /// <summary>Plays from the current position.</summary>
    Task PlayAsync(CancellationToken cancellationToken);

    /// <summary>Pauses on the current frame.</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>Goes to exactly <paramref name="position"/> (kept inside the file), not just the nearest keyframe.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    /// <summary>Sets the speed (0.25 to 8); the pitch of the sound is kept.</summary>
    Task SetSpeedAsync(double speed, CancellationToken cancellationToken);

    /// <summary>Moves one frame forward or back, pausing first when playing.</summary>
    Task StepFrameAsync(bool forward, CancellationToken cancellationToken);

    /// <summary>Switches to the audio stream <paramref name="trackId"/>.</summary>
    Task SetAudioTrackAsync(int trackId, CancellationToken cancellationToken);

    /// <summary>Sets the volume (0 to 1).</summary>
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);

    /// <summary>Turns the sound off or on.</summary>
    Task SetMutedAsync(bool muted, CancellationToken cancellationToken);

    /// <summary>Closes the file and goes back to <see cref="PlaybackState.Idle"/>.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
