using System.Text.Json;
using Leviathan.ViewModels.Rename;

namespace Leviathan.App;

/// <summary>Keeps the bulk-rename presets in <c>rename-presets.json</c> in the data folder, written to a temporary file and renamed (spec §15.3, §17).</summary>
internal sealed class JsonRenamePresetStore : IRenamePresetStore
{
    private readonly string _path = Leviathan.Core.Platform.DataLocation.Combine("rename-presets.json");

    public IReadOnlyList<RenamePreset> Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<RenamePreset>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file only costs the presets; nothing on disk depends on them.
            return [];
        }
    }

    public void Save(IReadOnlyList<RenamePreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(presets));
        File.Move(temp, _path, overwrite: true);
    }
}
