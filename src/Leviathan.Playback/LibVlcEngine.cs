using Leviathan.Core.Platform;
using LibVLCSharp.Shared;

namespace Leviathan.Playback;

/// <summary>
/// The LibVLC player behind <see cref="IVideoEngine"/> (spec §2.4, D10). LibVLC does the decoding, with hardware decoding switched on
/// (<c>--avcodec-hw=any</c>: D3D11VA where the card has it); this class turns its calls and events into the engine's one
/// <see cref="PlaybackSnapshot"/>. The calls that can block (opening a file, a seek on a slow share) run on the thread pool, never on
/// the caller's thread, and one at a time, because LibVLC does not like commands that overlap. A file that will not play ends in
/// <see cref="PlaybackState.Failed"/>; nothing here throws for a bad file (spec §3).
/// </summary>
public sealed class LibVlcEngine : IVideoEngine
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    private readonly LibVLC _libvlc;
    private readonly bool _ownsLibVlc;
    private readonly MediaPlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private Media? _media;
    private PlaybackState _state = PlaybackState.Idle;
    private string? _error;
    private double? _fps;
    private IReadOnlyList<Leviathan.Core.Platform.AudioTrack> _tracks = [];
    private TaskCompletionSource<bool>? _opened;
    private double _speed = 1;
    private double _volume = 1;
    private bool _muted;
    private bool _disposed;

    /// <summary>Starts a player on <paramref name="libvlc"/>; <paramref name="ownsLibVlc"/> disposes it with the engine.</summary>
    public LibVlcEngine(LibVLC libvlc, bool ownsLibVlc = false)
    {
        _libvlc = libvlc;
        _ownsLibVlc = ownsLibVlc;
        _player = new MediaPlayer(libvlc);
        _player.Playing += (_, _) => Moved(PlaybackState.Playing, opened: true);
        _player.Paused += (_, _) => Moved(PlaybackState.Paused);
        _player.EndReached += (_, _) => Moved(PlaybackState.Ended);
        _player.EncounteredError += (_, _) => Moved(PlaybackState.Failed, error: "LibVLC could not play this file.", opened: false);
        _player.TimeChanged += (_, _) => Raise();
        _player.LengthChanged += (_, _) => Raise();
    }

    /// <summary>
    /// The LibVLC options every Leviathan player uses: hardware decoding, no on-screen display of LibVLC's own, no mouse or key
    /// handling by LibVLC (the app owns the keys, spec §10.2), no per-file metadata fetching, and a longer read-ahead for network files.
    /// </summary>
    public static string[] DefaultOptions { get; } =
    [
        "--avcodec-hw=any", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--no-spu",
        "--no-mouse-events", "--no-keyboard-events", "--no-metadata-network-access", "--network-caching=1500",
    ];

    /// <summary>The LibVLC player, for the video surface to attach to (<c>VideoView.MediaPlayer</c>).</summary>
    public MediaPlayer Player => _player;

    /// <inheritdoc />
    public string Name => "LibVLC";

    /// <inheritdoc />
    public event EventHandler<PlaybackSnapshot>? Changed;

    /// <inheritdoc />
    public PlaybackSnapshot Snapshot
    {
        get
        {
            PlaybackState state;
            string? error;
            double? fps;
            IReadOnlyList<Leviathan.Core.Platform.AudioTrack> tracks;
            double speed;
            double volume;
            bool muted;
            lock (_lock)
            {
                (state, error, fps, tracks, speed, volume, muted) = (_state, _error, _fps, _tracks, _speed, _volume, _muted);
                if (_disposed || state is PlaybackState.Idle or PlaybackState.Failed)
                {
                    return PlaybackSnapshot.Closed with { State = state, Error = error, Volume = volume, Muted = muted };
                }
            }

            // LibVLC is asked outside the lock: a call into it can wait for its event thread, and that thread's handlers take the lock.
            var selected = _player.AudioTrack;
            return new PlaybackSnapshot(
                state,
                TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)),
                TimeSpan.FromMilliseconds(Math.Max(0, _player.Length)),
                speed,
                volume,
                muted,
                tracks,
                tracks.Any(t => t.Id == selected) ? selected : null,
                fps,
                null);
        }
    }

    /// <summary>
    /// A LibVLC instance with <see cref="DefaultOptions"/> (plus <paramref name="extra"/>), found in the <c>libvlc</c> folder the
    /// package puts beside the program. Throws <see cref="VLCException"/> when the native libraries are missing.
    /// </summary>
    public static LibVLC CreateLibVlc(params string[] extra)
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC([.. DefaultOptions, .. extra]);
    }

    /// <inheritdoc />
    public async Task OpenAsync(string path, TimeSpan start, bool autoplay, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _opened = opened;
                _state = PlaybackState.Opening;
                _error = null;
                _fps = null;
            }

            Changed?.Invoke(this, Snapshot);
            try
            {
                await Task.Run(() =>
                {
                    _player.Stop();
                    var old = _media;
                    _media = new Media(_libvlc, path, FromType.FromPath);
                    old?.Dispose();
                    _player.Play(_media);
                }, cancellationToken).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(OpenTimeout);
                try
                {
                    await opened.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Fail("The file did not start playing in time.");
                    return;
                }

                if (Snapshot.State == PlaybackState.Failed)
                {
                    return;
                }

                _fps = await Task.Run(ReadFrameRate, cancellationToken).ConfigureAwait(false);
                var found = await Task.Run(() => _player.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => new Leviathan.Core.Platform.AudioTrack(t.Id, t.Name ?? $"Track {t.Id}", null)).ToList(), cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _tracks = found;
                }
                if (start > TimeSpan.Zero)
                {
                    // Seeking once it plays is exact where the start-time option is only as good as the next keyframe.
                    await Task.Run(() => _player.Time = (long)start.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                }

                if (!autoplay)
                {
                    await Task.Run(() => _player.SetPause(true), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is VLCException or InvalidOperationException or ArgumentException or NullReferenceException)
            {
                Fail(ex.Message);
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, Snapshot);
        }
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        if (_state == PlaybackState.Ended && _media is not null)
        {
            _player.Play(_media);
        }
        else
        {
            _player.SetPause(false);
        }
    }, cancellationToken);

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken) => RunAsync(() => _player.SetPause(true), cancellationToken);

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) => RunAsync(() =>
    {
        var length = _player.Length;
        var milliseconds = (long)Math.Max(0, position.TotalMilliseconds);
        _player.Time = length > 0 ? Math.Min(milliseconds, length) : milliseconds;
        if (_state == PlaybackState.Ended)
        {
            Moved(PlaybackState.Paused);
        }
    }, cancellationToken);

    /// <inheritdoc />
    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken) => RunAsync(() =>
    {
        _speed = Math.Clamp(speed, 0.25, 8);
        _player.SetRate((float)_speed);
    }, cancellationToken);

    /// <inheritdoc />
    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken) => RunAsync(() =>
    {
        if (_state == PlaybackState.Playing)
        {
            _player.SetPause(true);
        }

        if (forward)
        {
            _player.NextFrame();
        }
        else
        {
            // LibVLC 3 only steps forward; a step back is a short seek, which lands on the frame before when the file allows it.
            _player.Time = Math.Max(0, _player.Time - (long)Math.Round(1000 / (_fps is > 0 ? _fps.Value : 25)));
        }
    }, cancellationToken);

    /// <inheritdoc />
    public Task SetAudioTrackAsync(int trackId, CancellationToken cancellationToken) => RunAsync(() => _player.SetAudioTrack(trackId), cancellationToken);

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken) => RunAsync(() =>
    {
        _volume = Math.Clamp(volume, 0, 1);
        _player.Volume = (int)Math.Round(_volume * 100);
    }, cancellationToken);

    /// <inheritdoc />
    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken) => RunAsync(() =>
    {
        _muted = muted;
        _player.Mute = muted;
    }, cancellationToken);

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        _player.Stop();
        lock (_lock)
        {
            _state = PlaybackState.Idle;
            _error = null;
        }
    }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await _gate.WaitAsync().ConfigureAwait(false);

        // Stopping and disposing from a LibVLC callback thread would deadlock, so this is done on the pool.
        await Task.Run(() =>
        {
            _player.Stop();
            _player.Dispose();
            _media?.Dispose();
            if (_ownsLibVlc)
            {
                _libvlc.Dispose();
            }
        }).ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _state is PlaybackState.Idle or PlaybackState.Failed or PlaybackState.Opening)
            {
                // Volume and mute are remembered for the next file even when nothing is open.
                if (!_disposed)
                {
                    await Task.Run(action, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, Snapshot);
    }

    private double? ReadFrameRate()
    {
        try
        {
            // The tracks are known once playback has started.
            foreach (var track in _media?.Tracks ?? [])
            {
                if (track.TrackType == TrackType.Video && track.Data.Video is { FrameRateDen: > 0, FrameRateNum: > 0 } video)
                {
                    return (double)video.FrameRateNum / video.FrameRateDen;
                }
            }
        }
        catch (Exception ex) when (ex is VLCException or InvalidOperationException)
        {
            // Not known: frame steps assume 25 frames a second.
        }

        return null;
    }

    private void Moved(PlaybackState state, string? error = null, bool? opened = null)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // Pause events from an open that is still starting must not replace Opening before Playing arrives.
            if (_state == PlaybackState.Opening && opened is null)
            {
                return;
            }

            _state = state;
            _error = error;
        }

        if (opened is { } result)
        {
            _opened?.TrySetResult(result);
        }

        Raise();
    }

    // Handlers of LibVLC's events run on its own thread, where calling back into LibVLC for the snapshot could deadlock: the
    // notification is made from the pool instead.
    private void Raise() => ThreadPool.QueueUserWorkItem(_ =>
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, Snapshot);
        }
    });

    private void Fail(string message)
    {
        lock (_lock)
        {
            _state = PlaybackState.Failed;
            _error = message;
        }

        _opened?.TrySetResult(false);
    }
}
