using Leviathan.Core.Platform;

namespace Leviathan.Testing;

/// <summary>
/// An <see cref="IVideoEngine"/> that plays nothing but behaves like one: it keeps a position, clamps seeks, steps by whole frames,
/// ends at the end and fails to open a path it was told to refuse. Every call is recorded in <see cref="Calls"/> so a test can say
/// exactly what a view model asked of the player. Playing does not advance on its own: a test calls <see cref="Advance"/>.
/// </summary>
public sealed class FakeVideoEngine : IVideoEngine
{
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Closed;

    /// <summary>Length given to every file opened.</summary>
    public TimeSpan FileDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Frame rate given to every file opened.</summary>
    public double FramesPerSecond { get; set; } = 25;

    /// <summary>Audio streams given to every file opened.</summary>
    public IReadOnlyList<AudioTrack> Tracks { get; set; } = [new AudioTrack(1, "English", "eng"), new AudioTrack(2, "Commentary", "eng")];

    /// <summary>Paths that fail to open, with the reason.</summary>
    public Dictionary<string, string> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every call, as text such as "Seek 00:01:00" or "Speed 2".</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The path last opened.</summary>
    public string? OpenPath { get; private set; }

    /// <summary>True once disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public string Name => "Fake";

    /// <inheritdoc />
    public PlaybackSnapshot Snapshot => _snapshot;

    /// <inheritdoc />
    public event EventHandler<PlaybackSnapshot>? Changed;

    /// <summary>Plays for <paramref name="time"/> (scaled by the speed); reaching the end ends the playback.</summary>
    public void Advance(TimeSpan time)
    {
        if (_snapshot.State != PlaybackState.Playing)
        {
            return;
        }

        var next = _snapshot.Position + (time * _snapshot.Speed);
        Update(next >= _snapshot.Duration ? _snapshot with { Position = _snapshot.Duration, State = PlaybackState.Ended } : _snapshot with { Position = next });
    }

    /// <inheritdoc />
    public Task OpenAsync(string path, TimeSpan start, bool autoplay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"Open {path} @ {start:c} {(autoplay ? "play" : "paused")}");
        OpenPath = path;
        if (Refuse.TryGetValue(path, out var reason))
        {
            Update(PlaybackSnapshot.Closed with { State = PlaybackState.Failed, Error = reason });
            return Task.CompletedTask;
        }

        Update(new PlaybackSnapshot(
            autoplay ? PlaybackState.Playing : PlaybackState.Paused, Clamp(start), FileDuration, 1, _snapshot.Volume, _snapshot.Muted, Tracks, Tracks.Count > 0 ? Tracks[0].Id : null, FramesPerSecond, null));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken) => Do("Play", s => s.IsOpen ? s with { State = PlaybackState.Playing } : s);

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken) => Do("Pause", s => s.IsOpen ? s with { State = PlaybackState.Paused } : s);

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) =>
        Do($"Seek {position:c}", s => s.IsOpen ? s with { Position = Clamp(position), State = s.State == PlaybackState.Ended ? PlaybackState.Paused : s.State } : s);

    /// <inheritdoc />
    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken) => Do($"Speed {speed}", s => s with { Speed = Math.Clamp(speed, 0.25, 8) });

    /// <inheritdoc />
    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) =>
        Do(forward ? "Step +" : "Step -", s => s.IsOpen ? s with { Position = Clamp(s.Position + TimeSpan.FromTicks((long)Math.Round((forward ? 1 : -1) * TimeSpan.TicksPerSecond / (s.FramesPerSecond ?? 25)))), State = PlaybackState.Paused } : s);

    /// <inheritdoc />
    public Task SetAudioTrackAsync(int trackId, CancellationToken cancellationToken) =>
        Do($"Audio {trackId}", s => s.AudioTracks.Any(t => t.Id == trackId) ? s with { AudioTrackId = trackId } : s);

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => Do($"Volume {volume}", s => s with { Volume = Math.Clamp(volume, 0, 1) });

    /// <inheritdoc />
    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => Do($"Muted {muted}", s => s with { Muted = muted });

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Calls.Add("Close");
        OpenPath = null;
        Update(PlaybackSnapshot.Closed with { Volume = _snapshot.Volume, Muted = _snapshot.Muted });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private TimeSpan Clamp(TimeSpan position) => position < TimeSpan.Zero ? TimeSpan.Zero : position > FileDuration ? FileDuration : position;

    private Task Do(string call, Func<PlaybackSnapshot, PlaybackSnapshot> change)
    {
        Calls.Add(call);
        Update(change(_snapshot));
        return Task.CompletedTask;
    }

    private void Update(PlaybackSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }
}
