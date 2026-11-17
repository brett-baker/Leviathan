using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Hub;
using Microsoft.UI.Dispatching;

namespace Leviathan.App;

/// <summary>
/// The work the app does on a timer or in the background for as long as it runs: closing idle server connections, rechecking cloud
/// states, the search index crawl, the sync pair check, the Soft-delete bin purge and the folder size recount. They used to start from inside
/// <c>AppHost.Create</c>, so building the host had side effects; now <c>App</c> starts them once the host exists (spec §3.4).
/// Must be called on the UI thread (the sync timer belongs to its dispatcher queue).
/// </summary>
internal static class BackgroundSchedules
{
    /// <summary>Starts every schedule. Call once.</summary>
    public static void Start(AppHost host, DispatcherQueue queue)
    {
        CleanArchiveScratch(host);
        CloseIdleConnectionsEachMinute(host);
        RunSearchIndex(host);
        CheckSyncPairsEachMinute(host, queue);

        // The automatic purge of the Soft-delete bin: a while after start-up, then daily, by the limits in Settings (spec 12.5).
        _ = new BinPurgeSchedule().RunAsync(() => host.Settings.Current.Files.BinPolicy(), host.BinService.SubmitPolicyAsync, CancellationToken.None);

        // Network drives the user opted in are counted again once a day (spec 12.7).
        _ = new SizeRecheckSchedule().RunAsync(
            () => SizeRecheckSchedule.Parse(host.Settings.Current.Files.RecheckSizePaths), host.Sizes.Recount, CancellationToken.None);
    }

    // Unpacked copies for opening and previewing go to a scratch folder, swept of old files at start.
    private static void CleanArchiveScratch(AppHost host) => _ = Task.Run(() =>
    {
        try
        {
            host.ArchiveStager.Clean(TimeSpan.FromDays(2));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old scratch files stay until the next start.
        }
    });

    // Connections to SFTP and FTP servers that nobody has used for a couple of minutes are closed; the cloud clients are looked at again.
    private static void CloseIdleConnectionsEachMinute(AppHost host) => _ = Task.Run(async () =>
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await tick.WaitForNextTickAsync())
        {
            await host.RemoteProvider.CloseIdleAsync();
            await host.RefreshCloudStatesAsync();
        }
    });

    private static void RunSearchIndex(AppHost host) => _ = Task.Run(() =>
    {
        host.SearchIndex.Initialize();
        return host.SearchIndex.RunAsync(CancellationToken.None);
    });

    // Saved sync pairs (spec 15.5) are checked once a minute for those due; the worker starts only when a pair is actually due.
    private static void CheckSyncPairsEachMinute(AppHost host, DispatcherQueue queue)
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(1);
        timer.Tick += (_, _) => _ = Task.Run(async () =>
        {
            try
            {
                await host.SyncChecker.CheckDueAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // The next tick tries again; what went wrong is in the pair's last result.
            }
        });
        timer.Start();
    }
}
