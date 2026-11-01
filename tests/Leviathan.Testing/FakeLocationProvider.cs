using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Testing;

/// <summary>
/// An in-memory folder tree behind <see cref="ILocationProvider"/>. Build it with <see cref="AddFile"/> and
/// <see cref="AddFolder"/>; parent folders are created automatically.
/// </summary>
public sealed class FakeLocationProvider : ILocationProvider
{
    private readonly Dictionary<LocationPath, List<FileEntry>> _children = [];
    private readonly HashSet<LocationPath> _folders = [];
    private readonly DateTimeOffset _time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Number of <see cref="ListAsync"/> calls, per location.</summary>
    public Dictionary<LocationPath, int> ListCalls { get; } = [];

    /// <inheritdoc />
    public string Name => "fake";

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => true;

    /// <summary>Adds a folder (and its parents).</summary>
    public FakeLocationProvider AddFolder(string path)
    {
        EnsureFolder(LocationPath.Parse(path));
        return this;
    }

    /// <summary>Adds a file of <paramref name="size"/> bytes (and its parent folders).</summary>
    public FakeLocationProvider AddFile(string path, long size)
    {
        var location = LocationPath.Parse(path);
        var parent = location.Parent ?? throw new ArgumentException("A file needs a parent folder.", nameof(path));
        EnsureFolder(parent);
        _children[parent].Add(new FileEntry(location.Name, location, FileEntryKind.File, size, _time, _time, FileAttributes.Normal));
        return this;
    }

    /// <summary>Adds a junction/symlink entry that must not be followed.</summary>
    public FakeLocationProvider AddLink(string path)
    {
        var location = LocationPath.Parse(path);
        var parent = location.Parent!;
        EnsureFolder(parent);
        _children[parent].Add(new FileEntry(location.Name, location, FileEntryKind.Link, 0, _time, _time, FileAttributes.Directory | FileAttributes.ReparsePoint));
        return this;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ListCalls[location] = ListCalls.GetValueOrDefault(location) + 1;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_folders.Contains(location))
        {
            throw new LocationException(LocationError.NotFound, location, $"{location} was not found.");
        }

        var items = _children[location].Where(options.Includes).ToList();
        for (var i = 0; i < items.Count; i += options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = items.Skip(i).Take(options.BatchSize).ToList();
            yield return new ListingBatch(batch, i + options.BatchSize >= items.Count);
        }

        if (items.Count == 0)
        {
            yield return new ListingBatch([], true);
        }
    }

    private void EnsureFolder(LocationPath folder)
    {
        if (!_folders.Add(folder))
        {
            return;
        }

        _children[folder] = [];
        if (folder.Parent is { } parent && !folder.IsRoot)
        {
            EnsureFolder(parent);
            _children[parent].Add(new FileEntry(folder.Name, folder, FileEntryKind.Folder, 0, _time, _time, FileAttributes.Directory));
        }
    }
}
