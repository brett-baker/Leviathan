using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Updates;

/// <summary>A newer release the feed offers.</summary>
/// <param name="Version">Its version, e.g. <c>0.6.0</c> or <c>0.6.0-beta.2</c>.</param>
/// <param name="Notes">What changed (Markdown from the release page), or null.</param>
public sealed record UpdateOffer(string Version, string? Notes);

/// <summary>What the feed is asked.</summary>
/// <param name="CurrentVersion">The version running now.</param>
/// <param name="Channel">The channel to look in: Stable sees only finished releases, Beta sees both.</param>
public sealed record UpdateRequest(string CurrentVersion, ReleaseChannel Channel);

/// <summary>
/// Where releases are published (release plan §4). The real one reads GitHub Releases through Velopack; a fake stands in for tests, so
/// the rules around it (channels, throttling, offline) are tested without a network.
/// </summary>
public interface IUpdateSource
{
    /// <summary>The newest release for the request, or null when this version is the newest. Throws <see cref="IOException"/> or <see cref="HttpRequestException"/> when the feed cannot be read.</summary>
    Task<UpdateOffer?> CheckAsync(UpdateRequest request, CancellationToken cancellationToken);
}

/// <summary>Where the update check stands.</summary>
public enum UpdateState
{
    /// <summary>Not checked yet.</summary>
    Idle,

    /// <summary>Asking the feed.</summary>
    Checking,

    /// <summary>This is the newest version.</summary>
    UpToDate,

    /// <summary>A newer version exists.</summary>
    Available,

    /// <summary>The feed could not be read (offline, no access).</summary>
    Failed,

    /// <summary>This build never checks: a development build is not installed and has no feed.</summary>
    Disabled,
}

/// <summary>
/// Asks the release feed whether a newer version exists, in the background and on request, and keeps the answer for the status hub and
/// Settings (release plan §4). It never interrupts: a check that fails (offline) is only recorded, never shown as an error dialog, and
/// nothing is applied here (that is the next slice). One check at a time; each is cancellable and has a timeout (spec §3.2). Used from
/// the UI thread, so <see cref="Changed"/> is raised there.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>How long to wait after start-up before the first check, so it never competes with the first folder (spec §3.1).</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);

    /// <summary>How often the background check repeats.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>The longest one check may take.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly IUpdateSource _source;
    private readonly AppVersion _current;
    private readonly Func<UpdateSettings> _settings;
    private readonly TimeProvider _time;
    private int _running;

    /// <summary>Creates a checker for the running version <paramref name="current"/>.</summary>
    public UpdateChecker(IUpdateSource source, AppVersion current, Func<UpdateSettings> settings, TimeProvider? time = null)
    {
        _source = source;
        _current = current;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        if (current.Channel == ReleaseChannel.Dev)
        {
            State = UpdateState.Disabled;
            Message = "A development build does not check for updates. Install a release to get them.";
        }
    }

    /// <summary>Raised when the state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the check stands.</summary>
    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>The newer release, while <see cref="State"/> is <see cref="UpdateState.Available"/>.</summary>
    public UpdateOffer? Offer { get; private set; }

    /// <summary>When the feed was last asked and answered, or null.</summary>
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>A sentence about the state, for Settings: "You have the newest version", why a check failed, and so on.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>The technical reason the last check failed, for the log; null when it did not.</summary>
    public string? LastError { get; private set; }

    /// <summary>The version running now.</summary>
    public AppVersion Current => _current;

    /// <summary>The channel a check looks in: the one chosen in Settings, else the one this build belongs to.</summary>
    public ReleaseChannel EffectiveChannel => _settings().Channel switch
    {
        UpdateChannelChoice.Stable => ReleaseChannel.Stable,
        UpdateChannelChoice.Beta => ReleaseChannel.Beta,
        _ => _current.Channel == ReleaseChannel.Beta ? ReleaseChannel.Beta : ReleaseChannel.Stable,
    };

    /// <summary>
    /// The background loop: the first check after <see cref="FirstCheckDelay"/>, then one every <see cref="Interval"/>, each only when
    /// "Check for updates" is on. Ends when <paramref name="cancellationToken"/> does.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (State == UpdateState.Disabled)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstCheckDelay, _time, cancellationToken).ConfigureAwait(true);
            while (true)
            {
                if (_settings().CheckForUpdates)
                {
                    await CheckAsync(cancellationToken).ConfigureAwait(true);
                }

                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing.
        }
    }

    /// <summary>Asks the feed now ("Check now"). A check already running is not repeated.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (State == UpdateState.Disabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            Set(UpdateState.Checking, "Checking for updates…");
            using var clock = new CancellationTokenSource(CheckTimeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clock.Token);
            var channel = EffectiveChannel;
            try
            {
                var offer = await _source.CheckAsync(new UpdateRequest(_current.Display, channel), timeout.Token).ConfigureAwait(true);
                LastChecked = _time.GetUtcNow();
                LastError = null;
                if (Newer(offer, channel) is { } newer)
                {
                    Offer = newer;
                    Set(UpdateState.Available, $"Leviathan {newer.Version} is ready.");
                }
                else
                {
                    Offer = null;
                    Set(UpdateState.UpToDate, "You have the newest version.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Fail("The update feed did not answer in time.", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidOperationException or FormatException)
            {
                Fail("Could not reach the update feed. Check your connection and try again.", ex);
            }
            catch (OperationCanceledException)
            {
                Set(Offer is null ? UpdateState.Idle : UpdateState.Available, Message);
                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // A source is trusted to look in the right place, not to be right: an offer that is not newer than this build, or a beta offered to
    // someone on the stable channel, is ignored here, so a mislabelled release can never push a downgrade or an early build.
    private UpdateOffer? Newer(UpdateOffer? offer, ReleaseChannel channel)
    {
        if (offer is null || !IsVersion(offer.Version))
        {
            return null;
        }

        if (channel == ReleaseChannel.Stable && offer.Version.Contains('-', StringComparison.Ordinal))
        {
            return null;
        }

        return AppVersion.Compare(offer.Version, _current.Prerelease is null ? _current.Number : $"{_current.Number}-{_current.Prerelease}") > 0 ? offer : null;
    }

    private static bool IsVersion(string text) => AppVersion.TryFromTag("v" + text, out _);

    private void Fail(string message, Exception? ex)
    {
        LastError = ex?.Message;
        Set(UpdateState.Failed, message);
    }

    private void Set(UpdateState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
