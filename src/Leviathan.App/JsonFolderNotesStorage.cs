using System.Text.Json;
using Leviathan.ViewModels.Notes;

namespace Leviathan.App;

/// <summary>Keeps the folder notes in <c>folder-notes.json</c> in the data folder, written to a temporary file and renamed (spec §15.6, §17).</summary>
internal sealed class JsonFolderNotesStorage : IFolderNotesStorage
{
    private readonly string _path = Leviathan.Core.Platform.DataLocation.Combine("folder-notes.json");

    public IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file only costs the notes; nothing else depends on them.
            return new Dictionary<string, string>();
        }
    }

    public void Save(IReadOnlyDictionary<string, string> notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(notes));
        File.Move(temp, _path, overwrite: true);
    }
}
