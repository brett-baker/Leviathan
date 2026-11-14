using System.IO.Abstractions;
using System.Text;

namespace Leviathan.Core.Git;

/// <summary>What Git says about one item (spec §15.6). The order is the order of importance: a folder shows the highest state of anything inside it.</summary>
public enum GitState
{
    /// <summary>Nothing to report.</summary>
    Clean = 0,

    /// <summary>Ignored by .gitignore.</summary>
    Ignored,

    /// <summary>Not tracked yet.</summary>
    Untracked,

    /// <summary>New in the index.</summary>
    Added,

    /// <summary>Renamed or copied.</summary>
    Renamed,

    /// <summary>Changed since the last commit (content, mode or type).</summary>
    Modified,

    /// <summary>Deleted.</summary>
    Deleted,

    /// <summary>In the middle of a merge with a conflict.</summary>
    Conflicted,
}

/// <summary>The status of one repository: its branch and every item that is not clean (spec §15.6).</summary>
public sealed class GitRepoStatus
{
    private readonly Dictionary<string, GitState> _items;
    private readonly Dictionary<string, GitState> _folders;

    private GitRepoStatus(string root, string? branch, int ahead, int behind, Dictionary<string, GitState> items, Dictionary<string, GitState> folders)
    {
        Root = root;
        Branch = branch;
        Ahead = ahead;
        Behind = behind;
        _items = items;
        _folders = folders;
    }

    /// <summary>The repository's top folder, with no trailing slash.</summary>
    public string Root { get; }

    /// <summary>The branch, "(detached)" for a detached head, or null when Git did not say.</summary>
    public string? Branch { get; }

    /// <summary>Commits ahead of the upstream branch.</summary>
    public int Ahead { get; }

    /// <summary>Commits behind the upstream branch.</summary>
    public int Behind { get; }

    /// <summary>How many items are not clean (ignored ones do not count).</summary>
    public int ChangedCount => _items.Values.Count(s => s != GitState.Ignored);

    /// <summary>
    /// The state of the item at <paramref name="fullPath"/>: its own, else for a folder the highest state of anything inside it, else the state of an
    /// untracked or ignored folder it sits in. <see cref="GitState.Clean"/> for everything else, including paths outside the repository.
    /// </summary>
    public GitState StateOf(string fullPath, bool isDirectory)
    {
        var relative = Relative(fullPath);
        if (relative is null)
        {
            return GitState.Clean;
        }

        if (relative.Length == 0)
        {
            return _folders.GetValueOrDefault(string.Empty);
        }

        if (_items.TryGetValue(relative, out var own))
        {
            return own;
        }

        if (isDirectory && _folders.TryGetValue(relative, out var inside))
        {
            return inside;
        }

        // Below an untracked or ignored folder (Git lists only the folder).
        for (var cut = relative.LastIndexOf('/'); cut > 0; cut = relative.LastIndexOf('/', cut - 1))
        {
            if (_items.TryGetValue(relative[..cut], out var above) && above is GitState.Untracked or GitState.Ignored)
            {
                return above;
            }
        }

        return GitState.Clean;
    }

    /// <summary>The status that has no changes, for a repository that was found but whose status could not be read.</summary>
    public static GitRepoStatus Empty(string root) => new(root, null, 0, 0, [], []);

    /// <summary>
    /// Reads the output of <c>git status --porcelain=v2 --branch -z</c>. Paths are separated by NUL, so names with spaces, quotes and
    /// non-ASCII letters arrive as they are. Unknown lines are skipped, so a newer Git cannot break the list.
    /// </summary>
    public static GitRepoStatus Parse(string root, string porcelainV2)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(porcelainV2);
        var items = new Dictionary<string, GitState>(StringComparer.OrdinalIgnoreCase);
        string? branch = null;
        int ahead = 0, behind = 0;
        var parts = porcelainV2.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var line = parts[i];
            if (line.Length < 2)
            {
                continue;
            }

            switch (line[0])
            {
                case '#':
                    if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                    {
                        branch = line["# branch.head ".Length..];
                    }
                    else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                    {
                        var ab = line["# branch.ab ".Length..].Split(' ');
                        if (ab.Length == 2 && int.TryParse(ab[0], out var a) && int.TryParse(ab[1], out var b))
                        {
                            ahead = Math.Abs(a);
                            behind = Math.Abs(b);
                        }
                    }

                    break;
                case '1' when line.Length > 2 && PathAfter(line, 8) is { } ordinary:
                    items[Clean(ordinary)] = FromXy(line.Substring(2, 2));
                    break;
                case '2' when PathAfter(line, 9) is { } renamed:
                    items[Clean(renamed)] = GitState.Renamed;
                    i++; // the original name follows as its own part
                    break;
                case 'u' when PathAfter(line, 10) is { } unmerged:
                    items[Clean(unmerged)] = GitState.Conflicted;
                    break;
                case '?':
                    items[Clean(line[2..])] = GitState.Untracked;
                    break;
                case '!':
                    items[Clean(line[2..])] = GitState.Ignored;
                    break;
            }
        }

        var folders = new Dictionary<string, GitState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, state) in items)
        {
            if (state == GitState.Ignored)
            {
                continue;
            }

            for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
            {
                Raise(folders, path[..cut], state);
            }

            Raise(folders, string.Empty, state);
        }

        return new GitRepoStatus(root.TrimEnd('\\', '/'), branch, ahead, behind, items, folders);
    }

    private static void Raise(Dictionary<string, GitState> folders, string key, GitState state)
    {
        if (!folders.TryGetValue(key, out var current) || state > current)
        {
            folders[key] = state;
        }
    }

    // "1 XY sub mH mI mW hH hI path": the path is the 9th field, and may hold spaces, so cut after the 8th space.
    private static string? PathAfter(string line, int fields)
    {
        var at = -1;
        for (var n = 0; n < fields; n++)
        {
            at = line.IndexOf(' ', at + 1);
            if (at < 0)
            {
                return null;
            }
        }

        return at + 1 < line.Length ? line[(at + 1)..] : null;
    }

    private static string Clean(string path) => path.TrimEnd('/');

    private static GitState FromXy(string xy) =>
        xy.Contains('D') ? GitState.Deleted
        : xy[0] == 'A' ? GitState.Added
        : GitState.Modified;

    private string? Relative(string fullPath)
    {
        var path = fullPath.Replace('\\', '/').TrimEnd('/');
        var root = Root.Replace('\\', '/');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : null;
    }
}

/// <summary>The letter and the name that stand for a <see cref="GitState"/> in the Git column.</summary>
public static class GitStates
{
    /// <summary>One letter, or empty for clean.</summary>
    public static string Letter(GitState state) => state switch
    {
        GitState.Modified => "M",
        GitState.Added => "A",
        GitState.Deleted => "D",
        GitState.Renamed => "R",
        GitState.Untracked => "?",
        GitState.Conflicted => "!",
        GitState.Ignored => "·",
        _ => string.Empty,
    };

    /// <summary>The state in words, for a tooltip.</summary>
    public static string Title(GitState state) => state switch
    {
        GitState.Modified => "Modified",
        GitState.Added => "Added",
        GitState.Deleted => "Deleted",
        GitState.Renamed => "Renamed",
        GitState.Untracked => "Not tracked",
        GitState.Conflicted => "Conflict",
        GitState.Ignored => "Ignored",
        _ => "Unchanged",
    };
}

/// <summary>Finds the repository a folder belongs to (spec §15.6). Reads the disk only through <see cref="IFileSystem"/>.</summary>
public static class GitRepoLocator
{
    /// <summary>The top folder of the repository holding <paramref name="folder"/> (the nearest parent with a <c>.git</c> folder or file), or null when it is in none.</summary>
    public static string? FindRoot(IFileSystem fs, string folder)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var current = folder.TrimEnd('\\', '/');
        for (var depth = 0; depth < 64 && current.Length > 0; depth++)
        {
            var marker = fs.Path.Combine(current, ".git");
            if (fs.Directory.Exists(marker) || fs.File.Exists(marker))
            {
                return current;
            }

            var parent = fs.Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                return null;
            }

            current = parent.TrimEnd('\\', '/');
        }

        return null;
    }
}

/// <summary>How a Git command ended.</summary>
/// <param name="ExitCode">The exit code, or -1 when it did not run to the end.</param>
/// <param name="Output">What it wrote to standard output.</param>
/// <param name="NotInstalled">True when there is no git.exe to run.</param>
/// <param name="TimedOut">True when it was stopped after the time limit.</param>
public sealed record GitRunResult(int ExitCode, string Output, bool NotInstalled = false, bool TimedOut = false)
{
    /// <summary>True when Git ran and said it was fine.</summary>
    public bool Ok => ExitCode == 0 && !NotInstalled && !TimedOut;
}

/// <summary>Runs <c>git</c> in a folder. Behind an interface so the logic is tested without Git and so a missing Git is an answer, not an exception.</summary>
public interface IGitRunner
{
    /// <summary>Runs git with <paramref name="arguments"/> in <paramref name="workingDirectory"/>, stopping after <paramref name="timeout"/>.</summary>
    Task<GitRunResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The arguments of the one command the status feature needs.</summary>
public static class GitCommands
{
    /// <summary>Status with branch, NUL-separated, untracked and ignored items listed (folders as one entry). Optional locks are off so a running Git is never blocked.</summary>
    public static IReadOnlyList<string> Status { get; } = ["--no-optional-locks", "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal", "--ignored=traditional"];

    /// <summary>Output encoding of git on Windows when quoting is off: UTF-8.</summary>
    public static Encoding OutputEncoding { get; } = new UTF8Encoding(false);
}
