using System.Text.Json;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.App;

/// <summary>Keeps the saved servers in <c>servers.json</c> next to the other settings, written to a temporary file and renamed so a crash never leaves half a file (spec §17). No passwords: Windows keeps those (spec §3.3).</summary>
internal sealed class JsonSavedConnectionsStorage : ISavedConnectionsStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonSavedConnectionsStorage(string path) => _path = path;

    public IReadOnlyList<SavedConnection> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_path)) ?? [];
            return [.. stored.Where(s => !string.IsNullOrWhiteSpace(s.Host)).Select(s => new SavedConnection(s.Host, s.Name))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts the list afresh; servers are re-saved the next time they are opened.
            return [];
        }
    }

    public void Save(IReadOnlyList<SavedConnection> connections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(connections.Select(c => new Stored(c.Host, c.Name)), Options));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record Stored(string Host, string? Name = null);
}
