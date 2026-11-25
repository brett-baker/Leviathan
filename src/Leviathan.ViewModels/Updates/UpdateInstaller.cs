namespace Leviathan.ViewModels.Updates;

/// <summary>Downloads a release and hands the program folder over to the installer (release plan §4). The real one is Velopack.</summary>
public interface IUpdateApplier
{
    /// <summary>Downloads the release found by the last check, reporting 0 to 100. The files are checked against the feed's hashes by the installer.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);

    /// <summary>Starts the installer, which waits for this process to exit, swaps the program folder and starts the new version. The caller exits right after.</summary>
    void ApplyAndRestart();
}

/// <summary>Stops the helper processes (worker, shell host, media, administrator helper) that run from the program folder, so none holds a file the update must replace (release plan §6).</summary>
public interface IHelperStopper
{
    /// <summary>Stops every helper. Their jobs are journaled and resume when the new version starts (spec §3.4).</summary>
    Task StopAllAsync(CancellationToken cancellationToken);
}

/// <summary>What is running that an update would interrupt.</summary>
/// <param name="Running">Jobs copying, moving or deleting now.</param>
/// <param name="Queued">Jobs waiting for a slot.</param>
public readonly record struct JobLoad(int Running, int Queued)
{
    /// <summary>Everything an update would interrupt.</summary>
    public int Total => Running + Queued;

    /// <summary>The sentence the confirmation shows, e.g. "2 jobs are running and 1 is waiting", or empty when there are none.</summary>
    public string Describe() => Total == 0
        ? string.Empty
        : Running > 0 && Queued > 0 ? $"{Count(Running)} running and {Queued:N0} waiting"
        : Running > 0 ? $"{Count(Running)} running"
        : $"{Count(Queued)} waiting";

    private static string Count(int n) => n == 1 ? "1 job is" : $"{n:N0} jobs are";
}

/// <summary>Where installing the update stands.</summary>
public enum InstallState
{
    /// <summary>Nothing started.</summary>
    Idle,

    /// <summary>Downloading the release.</summary>
    Downloading,

    /// <summary>Downloaded and checked; waiting for the user to restart.</summary>
    Ready,

    /// <summary>The user chose to wait; the restart happens when the jobs have finished.</summary>
    WaitingForJobs,

    /// <summary>Stopping the helper processes.</summary>
    Stopping,

    /// <summary>The installer has been started and this process is about to exit.</summary>
    Applying,

    /// <summary>The download or the hand-over did not work; the app is still running and the user can try again.</summary>
    Failed,
}

/// <summary>
/// Installs a newer version the checker found (release plan §4, slice 4): download, then, when the user says so, stop the helpers and let
/// the installer swap the program folder and restart. Nothing is applied without the user choosing it; jobs that are running are named
/// in the confirmation and can be waited for; a failure leaves the running copy as it was. Used from the UI thread.
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>How often "wait for jobs" looks again.</summary>
    public static readonly TimeSpan JobPoll = TimeSpan.FromSeconds(2);

    private readonly IUpdateApplier _applier;
    private readonly IHelperStopper _helpers;
    private readonly Func<JobLoad> _jobs;
    private readonly TimeProvider _time;
    private int _busy;

    /// <summary>Creates an installer; <paramref name="jobs"/> reports what is running now.</summary>
    public UpdateInstaller(IUpdateApplier applier, IHelperStopper helpers, Func<JobLoad> jobs, TimeProvider? time = null)
    {
        _applier = applier;
        _helpers = helpers;
        _jobs = jobs;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when the state, the percentage or the message changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Where it stands.</summary>
    public InstallState State { get; private set; }

    /// <summary>Download progress, 0 to 100.</summary>
    public int Percent { get; private set; }

    /// <summary>A sentence about the state, for the status hub.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>What is running now, for the confirmation.</summary>
    public JobLoad Jobs => _jobs();

    /// <summary>Downloads the release. A download already running is not repeated; a finished one is not fetched again.</summary>
    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (State is InstallState.Ready or InstallState.Applying || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            Percent = 0;
            Set(InstallState.Downloading, "Downloading the update…");
            await _applier.DownloadAsync(Progress, cancellationToken).ConfigureAwait(true);
            Percent = 100;
            Set(InstallState.Ready, "The update is downloaded. Restart to install it.");
        }
        catch (OperationCanceledException)
        {
            Set(InstallState.Idle, string.Empty);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Set(InstallState.Failed, $"The update could not be downloaded: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Restarts into the new version. With <paramref name="waitForJobs"/> and jobs running it first waits until there are none; otherwise
    /// the helpers are stopped at once and their jobs resume after the restart. Cancel to stay on the current version while waiting.
    /// </summary>
    public async Task ApplyAsync(bool waitForJobs, CancellationToken cancellationToken)
    {
        if (State != InstallState.Ready && State != InstallState.WaitingForJobs)
        {
            await DownloadAsync(cancellationToken).ConfigureAwait(true);
            if (State != InstallState.Ready)
            {
                return;
            }
        }

        try
        {
            if (waitForJobs)
            {
                while (_jobs().Total > 0)
                {
                    Set(InstallState.WaitingForJobs, $"Waiting for jobs to finish ({_jobs().Describe()}). The update is installed after that.");
                    await Task.Delay(JobPoll, _time, cancellationToken).ConfigureAwait(true);
                }
            }

            Set(InstallState.Stopping, "Closing the helper processes…");
            await _helpers.StopAllAsync(cancellationToken).ConfigureAwait(true);
            Set(InstallState.Applying, "Restarting to install the update…");
            _applier.ApplyAndRestart();
        }
        catch (OperationCanceledException)
        {
            Set(InstallState.Ready, "The update is downloaded. Restart to install it.");
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Set(InstallState.Failed, $"The update could not be started: {ex.Message} Restart Leviathan and try again.");
        }
    }

    private void Progress(int percent)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Set(InstallState.Downloading, $"Downloading the update… {Percent}%");
    }

    private void Set(InstallState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
