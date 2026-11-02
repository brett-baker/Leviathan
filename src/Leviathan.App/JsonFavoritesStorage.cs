using System.Text.Json;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.App;

/// <summary>Keeps the favorites in <c>%LOCALAPPDATA%\Leviathan\favorites.json</c>, written to a temporary file and renamed so a crash never leaves half a file (spec §17).</summary>
internal sealed class JsonFavoritesStorage : IFavoritesStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonFavoritesStorage(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan", "favorites.json");

    public IReadOnlyList<FavoriteEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_path)) ?? [];
            return [.. stored.Where(s => LocationPath.TryParse(s.Path, out _)).Select(s => new FavoriteEntry(LocationPath.Parse(s.Path), s.Name))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts the list afresh; the pins are a convenience, not data.
            return [];
        }
    }

    public void Save(IReadOnlyList<FavoriteEntry> favorites)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(favorites.Select(f => new Stored(f.Location.Value, f.Name)), Options));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Stored(string Path, string? Name);
}
