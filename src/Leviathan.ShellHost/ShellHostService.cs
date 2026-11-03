using System.Reflection;
using Leviathan.Contracts;

namespace Leviathan.ShellHost;

/// <summary>The shell host's IPC endpoint; a thin translation layer over <see cref="StaPool"/>.</summary>
internal sealed class ShellHostService : IShellHostService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    private static readonly string AppVersion =
        typeof(ShellHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private readonly StaPool _pool;

    public ShellHostService(StaPool pool) => _pool = pool ?? throw new ArgumentNullException(nameof(pool));

    /// <inheritdoc />
    public Task<ShellHostInfo> PingAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ShellHostInfo(ContractVersion.Major, ContractVersion.Minor, AppVersion, Environment.ProcessId, StartedAt));

    /// <inheritdoc />
    public async Task<ShellHostStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var apartment = await _pool.RunAsync(() => Thread.CurrentThread.GetApartmentState().ToString(), ProbeTimeout, cancellationToken).ConfigureAwait(false);
        var status = _pool.Status;
        return new ShellHostStatus(status.Size, status.Busy, status.Queued, status.Completed, status.Failed, status.TimedOut, status.Abandoned, apartment);
    }
}
