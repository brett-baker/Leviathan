using System.IO.Abstractions;
using System.Text.Json;
using Leviathan.Core.Paths;

namespace Leviathan.Engine.Transfers;

/// <summary>
/// The trash folders Leviathan has made (spec §12.5): <c>\\nas\media\.leviathan-trash</c>, or one deeper when the share root could
/// not be written to. Remembering them is what lets the Soft-delete bin list "everything across shares" without crawling any share.
/// Kept in a small JSON file written beside itself and swapped in. Thread-safe.
/// </summary>
public sealed class BinRegistry
{
    private readonly IFileSystem _fs;
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<string>? _roots;

    /// <summary>Creates the registry kept in <paramref name="path"/>.</summary>
    public BinRegistry(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fs = fileSystem;
        _path = path;
    }

    /// <summary>The file system the bin works on.</summary>
    public IFileSystem FileSystem => _fs;

    /// <summary>Every trash folder known, in the order they were first used.</summary>
    public IReadOnlyList<LocationPath> Roots
    {
        get
        {
            lock (_lock)
            {
                return [.. Load().Select(LocationPath.Parse)];
            }
        }
    }

    /// <summary>Remembers a trash folder; does nothing when it is already known.</summary>
    public void Add(LocationPath trashRoot)
    {
        ArgumentNullException.ThrowIfNull(trashRoot);
        lock (_lock)
        {
            var roots = Load();
            if (roots.Any(r => r.Equals(trashRoot.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            roots.Add(trashRoot.Value);
            Save(roots);
        }
    }

    /// <summary>Forgets a trash folder (the share is gone for good); its items are not touched.</summary>
    public void Remove(LocationPath trashRoot)
    {
        ArgumentNullException.ThrowIfNull(trashRoot);
        lock (_lock)
        {
            var roots = Load();
            if (roots.RemoveAll(r => r.Equals(trashRoot.Value, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save(roots);
            }
        }
    }

    /// <summary>
    /// Drops the manifest lines for items that were restored or purged. An item inside a <c>.leviathan-trash</c> names its own
    /// manifest; one inside a NAS bin is looked for in the trash folders of the same share.
    /// </summary>
    public void Forget(IReadOnlyCollection<LocationPath> trashedItems)
    {
        ArgumentNullException.ThrowIfNull(trashedItems);
        var all = Roots;
        foreach (var group in trashedItems.GroupBy(i => BinManifest.RootOf(i)?.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(i => i.Value).ToList();
            IEnumerable<LocationPath> roots = group.Key.Length > 0
                ? [LocationPath.Parse(group.Key)]
                : all.Where(r => group.Any(i => string.Equals(i.Root, r.Root, StringComparison.OrdinalIgnoreCase)));
            foreach (var root in roots)
            {
                try
                {
                    BinManifest.Remove(_fs, root, names);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The item is already restored or purged; a line left behind is dropped from the view because its item is gone.
                }
            }
        }
    }

    private List<string> Load()
    {
        if (_roots is not null)
        {
            return _roots;
        }

        try
        {
            _roots = _fs.File.Exists(_path) ? JsonSerializer.Deserialize<List<string>>(_fs.File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged registry only forgets which trash folders to look in; the next delete adds its folder again.
            _roots = [];
        }

        return _roots;
    }

    private void Save(List<string> roots)
    {
        var folder = _fs.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
        {
            _fs.Directory.CreateDirectory(folder);
        }

        var temp = _path + ".tmp";
        _fs.File.WriteAllText(temp, JsonSerializer.Serialize(roots));
        _fs.File.Move(temp, _path, overwrite: true);
    }
}
