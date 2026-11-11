using System.Diagnostics;
using Leviathan.Core.Platform;

namespace Leviathan.Providers.Cloud;

/// <summary>
/// Tells whether each cloud sync client is running by looking at the programs that run (spec §7 "shows sync status"), and whether its folder is still there. It does not
/// read a client's own progress, which only the client knows; "running" means changes are being kept in step, "not running" means they are not until it is started.
/// </summary>
public sealed class ProcessCloudSyncProbe : ICloudSyncProbe
{
    private readonly Func<IReadOnlySet<string>> _running;
    private readonly Func<string, bool> _folderExists;

    /// <summary>Uses the real list of running programs and the real file system.</summary>
    public ProcessCloudSyncProbe()
        : this(RunningPrograms, Directory.Exists)
    {
    }

    /// <summary>Uses the given sources (tests pass fakes).</summary>
    public ProcessCloudSyncProbe(Func<IReadOnlySet<string>> running, Func<string, bool> folderExists)
    {
        _running = running ?? throw new ArgumentNullException(nameof(running));
        _folderExists = folderExists ?? throw new ArgumentNullException(nameof(folderExists));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<CloudProviderInfo, CloudSyncState> StatesOf(IReadOnlyList<CloudProviderInfo> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var running = providers.Count == 0 ? new HashSet<string>() : _running();
        var states = new Dictionary<CloudProviderInfo, CloudSyncState>();
        foreach (var provider in providers)
        {
            bool exists;
            try
            {
                exists = _folderExists(provider.SyncFolder.ToWin32Path());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exists = true;
            }

            states[provider] = CloudClients.Decide(provider.Kind, exists, running);
        }

        return states;
    }

    private static HashSet<string> RunningPrograms()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A program that ended while the list was read.
                }
            }
        }

        return names;
    }
}
