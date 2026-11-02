using System.Text.Json;
using Leviathan.ViewModels.Drives;

namespace Leviathan.App;

/// <summary>Keeps the drives whose summary card is hidden in <c>%LOCALAPPDATA%\Leviathan\hidden-drive-cards.json</c>, written to a temporary file and renamed (spec §17).</summary>
internal sealed class JsonDriveCardStorage : IDriveCardStorage
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leviathan", "hidden-drive-cards.json");

    public IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file shows every card again; nothing is lost.
            return [];
        }
    }

    public void Save(IReadOnlyList<string> roots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(roots));
        File.Move(temp, _path, overwrite: true);
    }
}
