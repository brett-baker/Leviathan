using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Folders;

/// <summary>What a valid new name looks like and which part of a name the rename box selects (spec §8.4).</summary>
public static partial class RenameRules
{
    /// <summary>
    /// Returns why <paramref name="name"/> cannot be a file or folder name, or null when it can. The messages are shown in
    /// the status bar next to the rename box, so they say what to change.
    /// </summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A name cannot be empty.";
        }

        if (name.Length > 255)
        {
            return "That name is too long (255 characters at most).";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "A name cannot contain any of these characters: \\ / : * ? \" < > |";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "A name cannot end with a dot or a space.";
        }

        return ReservedName().IsMatch(name) ? $"'{name}' is a reserved name in Windows." : null;
    }

    /// <summary>
    /// Length of the part of the name the box selects first: the name without its extension for a file (so typing replaces the
    /// name and keeps ".mkv"), the whole name for a folder or a dotfile such as ".gitignore".
    /// </summary>
    public static int StemLength(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != FileEntryKind.File)
        {
            return entry.Name.Length;
        }

        var dot = entry.Name.LastIndexOf('.');
        return dot > 0 ? dot : entry.Name.Length;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}

/// <summary>
/// Renames a file or folder in place (spec §8.4, §12.5). The disk work runs on the thread pool; the slice that adds undo (E4)
/// routes this through the journaled worker, so callers only see <see cref="RenameAsync"/>.
/// </summary>
public sealed class RenameService
{
    private readonly IFileSystem _fileSystem;
    private readonly IRemoteItems? _remote;

    /// <summary>Creates the service over <paramref name="fileSystem"/>, and <paramref name="remote"/> for items on a server.</summary>
    public RenameService(IFileSystem fileSystem, IRemoteItems? remote = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _remote = remote;
    }

    /// <summary>
    /// Gives <paramref name="from"/> the name <paramref name="newName"/> and returns its new location. Changing only the case
    /// of a name works. Nothing is overwritten.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not valid.</exception>
    /// <exception cref="IOException">The item is gone, or something else already has that name.</exception>
    public Task<LocationPath> RenameAsync(LocationPath from, string newName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (RenameRules.Validate(newName) is { } problem)
        {
            throw new ArgumentException(problem, nameof(newName));
        }

        if (_remote is { } remote && remote.Handles(from))
        {
            return RenameOnServerAsync(remote, from, newName.Trim(), cancellationToken);
        }

        return Task.Run(() => Rename(from, newName.Trim()), cancellationToken);
    }

    private static async Task<LocationPath> RenameOnServerAsync(IRemoteItems remote, LocationPath from, string newName, CancellationToken cancellationToken)
    {
        var parent = from.Parent ?? throw new IOException("A server cannot be renamed here.");
        var target = parent.Combine(newName);
        var onlyCase = string.Equals(from.Value, target.Value, StringComparison.OrdinalIgnoreCase);
        if (!onlyCase && await remote.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"Something named '{newName}' already exists in this folder.");
        }

        await remote.RenameAsync(from, target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    private LocationPath Rename(LocationPath from, string newName)
    {
        var parent = from.Parent ?? throw new IOException("A drive cannot be renamed here.");
        var source = from.Value;
        var target = _fileSystem.Path.Combine(parent.Value, newName);
        var isFolder = _fileSystem.Directory.Exists(source);
        if (!isFolder && !_fileSystem.File.Exists(source))
        {
            throw new FileNotFoundException($"{from.Name} no longer exists.");
        }

        var onlyCase = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (!onlyCase && (_fileSystem.File.Exists(target) || _fileSystem.Directory.Exists(target)))
        {
            throw new IOException($"Something named '{newName}' already exists in this folder.");
        }

        if (isFolder)
        {
            _fileSystem.Directory.Move(source, target);
        }
        else
        {
            _fileSystem.File.Move(source, target);
        }

        return LocationPath.Parse(target);
    }
}
