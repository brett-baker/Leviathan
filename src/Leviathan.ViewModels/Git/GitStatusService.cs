using System.IO.Abstractions;
using Leviathan.Core.Git;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Git;

/// <summary>
/// Git status for the folders on show (spec §15.6): finds the repository a folder is in, asks Git for its status once (off the UI thread, with
/// a time limit), keeps the answer per repository, and tells the view when it changed. Local drives only: status over SMB walks the whole tree
/// across the network. A missing Git turns the feature off quietly; a slow or failing Git keeps the last answer and never blocks a listing.
/// Thread-safe; <see cref="Changed"/> is raised on the thread that finished the run, so the view posts it to the UI thread.
/// </summary>
public sealed class GitStatusService(IGitRunner runner, IFileSystem fileSystem, Func<bool> enabled, TimeProvider? time = null)
{
    /// <summary>How long one status run may take.</summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(5);

    /// <summary>How soon the same repository is asked again at the earliest.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, GitRepoStatus> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private bool _gitMissing;

    /// <summary>Raised with the repository's top folder when its status was read.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>True when the setting is on and Git was not found missing.</summary>
    public bool Active => !_gitMissing && enabled();

    /// <summary>
    /// Reads the status of the repository <paramref name="folder"/> is in, unless it was read a moment ago. Does nothing when the feature is off, the
    /// folder is not on a local drive or not in a repository. Returns when the answer is stored.
    /// </summary>
    public async Task RefreshAsync(LocationPath folder, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Active || folder.Kind != LocationKind.Drive)
        {
            return;
        }

        var win32 = folder.ToWin32Path();
        var root = await Task.Run(() => GitRepoLocator.FindRoot(fileSystem, win32), cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_running.Contains(root) || (!force && _lastRun.TryGetValue(root, out var last) && _time.GetUtcNow() - last < MinInterval))
            {
                return;
            }

            _running.Add(root);
        }

        try
        {
            var result = await runner.RunAsync(root, GitCommands.Status, RunLimit, cancellationToken).ConfigureAwait(false);
            if (result.NotInstalled)
            {
                _gitMissing = true;
                return;
            }

            if (!result.Ok)
            {
                return; // keep what we had: a timeout or a repository mid-operation must not wipe the icons
            }

            var status = GitRepoStatus.Parse(root, result.Output);
            lock (_lock)
            {
                _repos[root] = status;
            }
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(root);
                _lastRun[root] = _time.GetUtcNow();
            }
        }

        Changed?.Invoke(this, root);
    }

    /// <summary>The Git state of <paramref name="entry"/>; <see cref="GitState.Clean"/> when its repository has not been read or it is in none.</summary>
    public GitState StateOf(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var status = StatusFor(entry.Location);
        return status?.StateOf(entry.Location.ToWin32Path(), entry.Kind is FileEntryKind.Folder) ?? GitState.Clean;
    }

    /// <summary>The status of the repository holding <paramref name="location"/>, or null when it has not been read.</summary>
    public GitRepoStatus? StatusFor(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.Kind != LocationKind.Drive)
        {
            return null;
        }

        var path = location.ToWin32Path();
        lock (_lock)
        {
            GitRepoStatus? best = null;
            foreach (var repo in _repos.Values)
            {
                var inside = path.Equals(repo.Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(repo.Root + "\\", StringComparison.OrdinalIgnoreCase);
                if (inside && (best is null || repo.Root.Length > best.Root.Length))
                {
                    best = repo; // the nearest one, for a repository inside another
                }
            }

            return best;
        }
    }

    /// <summary>The line the status bar adds in a repository: "main ↑1 ↓2 · 3 changed", or null outside one.</summary>
    public string? SummaryFor(LocationPath folder)
    {
        if (StatusFor(folder) is not { } status || status.Branch is null)
        {
            return null;
        }

        var sync = (status.Ahead > 0 ? $" ↑{status.Ahead}" : string.Empty) + (status.Behind > 0 ? $" ↓{status.Behind}" : string.Empty);
        return $"{status.Branch}{sync} · {(status.ChangedCount == 0 ? "clean" : $"{status.ChangedCount} changed")}";
    }
}
