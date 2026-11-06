using System.Text.Json;
using Leviathan.ViewModels.Undo;

namespace Leviathan.App;

/// <summary>Keeps the undo and redo stacks in <c>%LOCALAPPDATA%\Leviathan\undo.json</c>, written to a temporary file and renamed (spec §12.5, §17).</summary>
internal sealed class JsonUndoStorage : IUndoStorage
{
    private readonly string _path = Leviathan.Core.Platform.DataLocation.Combine("undo.json");

    public UndoState Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<UndoState>(File.ReadAllText(_path)) ?? UndoState.Empty : UndoState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file only costs the history; nothing on disk depends on it.
            return UndoState.Empty;
        }
    }

    public void Save(UndoState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, _path, overwrite: true);
    }
}
