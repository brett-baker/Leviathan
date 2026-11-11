using System.Text.Json;
using Leviathan.Core.Paths;
using Leviathan.Search;

namespace Leviathan.App;

/// <summary>The folders chosen for the search index, in <c>indexed-folders.json</c> (spec §13, §17: written atomically).</summary>
internal sealed class JsonIndexedFolders : IIndexedFolders
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonIndexedFolders(string? path = null) =>
        _path = path ?? Leviathan.Core.Platform.DataLocation.Combine("indexed-folders.json");

    public IReadOnlyList<LocationPath> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [];
            return [.. stored.Where(s => LocationPath.TryParse(s, out _)).Select(LocationPath.Parse)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts the list afresh; the folders are a choice that is quick to make again.
            return [];
        }
    }

    public void Save(IReadOnlyList<LocationPath> folders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(folders.Select(f => f.Value), Options));
        File.Move(temp, _path, overwrite: true);
    }
}
