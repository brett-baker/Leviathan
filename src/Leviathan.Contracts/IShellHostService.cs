namespace Leviathan.Contracts;

/// <summary>Information the shell host reports about itself.</summary>
/// <param name="ContractMajor">Contract major version the host speaks (the same number as the worker's).</param>
/// <param name="ContractMinor">Contract minor version.</param>
/// <param name="AppVersion">Informational version including the commit SHA.</param>
/// <param name="ProcessId">Host process ID.</param>
/// <param name="StartedAt">When the host started.</param>
public sealed record ShellHostInfo(int ContractMajor, int ContractMinor, string AppVersion, int ProcessId, DateTimeOffset StartedAt);

/// <summary>How the shell host's STA thread pool is doing (shown on the Background tab, spec §16.1).</summary>
/// <param name="PoolSize">Threads the pool keeps available.</param>
/// <param name="Busy">Threads running a request right now.</param>
/// <param name="Queued">Requests waiting for a thread.</param>
/// <param name="Completed">Requests that finished.</param>
/// <param name="Failed">Requests that threw.</param>
/// <param name="TimedOut">Requests given up on because they ran too long (a shell extension hung).</param>
/// <param name="AbandonedThreads">Threads still stuck in a request that timed out; they are replaced, not killed.</param>
/// <param name="Apartment">The apartment state a request runs in; always STA, reported so a test can prove it.</param>
public sealed record ShellHostStatus(int PoolSize, int Busy, int Queued, long Completed, long Failed, long TimedOut, int AbandonedThreads, string Apartment);

/// <summary>
/// The shell host (Leviathan.ShellHost), called by the UI over a named pipe (spec §2.2). It runs the things that load third-party
/// code into the process, shell context-menu handlers and preview handlers, so a crash or a hang in one of them costs a restart of
/// this process and never the window. Context-menu and preview calls arrive with slices F2 and F3.
/// </summary>
public interface IShellHostService
{
    /// <summary>Returns host and contract versions; also used as a heartbeat.</summary>
    Task<ShellHostInfo> PingAsync(CancellationToken cancellationToken);

    /// <summary>Runs a trivial request on the STA pool, so a healthy answer proves the pool still takes work, and reports its counters.</summary>
    Task<ShellHostStatus> GetStatusAsync(CancellationToken cancellationToken);
}
