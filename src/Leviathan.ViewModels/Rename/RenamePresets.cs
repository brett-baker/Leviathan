namespace Leviathan.ViewModels.Rename;

/// <summary>A saved set of rename steps (spec §15.3: saved presets).</summary>
/// <param name="Name">What the person called it.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record RenamePreset(string Name, IReadOnlyList<RenameDraft> Steps);

/// <summary>Keeps the presets between runs; the real one is a JSON file in the user's data folder.</summary>
public interface IRenamePresetStore
{
    /// <summary>The saved presets; damaged or missing storage gives an empty list.</summary>
    IReadOnlyList<RenamePreset> Load();

    /// <summary>Writes the presets atomically. Does I/O, so callers run it off the UI thread.</summary>
    void Save(IReadOnlyList<RenamePreset> presets);
}

/// <summary>
/// The saved rename presets (spec §15.3). A preset stores copies of the steps, so editing the dialog afterwards does not change it;
/// saving under an existing name replaces that preset.
/// </summary>
public sealed class RenamePresets
{
    private readonly IRenamePresetStore _store;
    private readonly List<RenamePreset> _presets;

    /// <summary>Loads what <paramref name="store"/> holds (reads the file: construct off the UI thread).</summary>
    public RenamePresets(IRenamePresetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _presets = [.. store.Load().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The presets, by name.</summary>
    public IReadOnlyList<RenamePreset> All => _presets;

    /// <summary>Saves a copy of <paramref name="steps"/> as <paramref name="name"/>, replacing a preset of that name. Returns false for a blank name.</summary>
    public bool Save(string name, IEnumerable<RenameDraft> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return false;
        }

        _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _presets.Add(new RenamePreset(name, [.. steps.Select(s => s.Clone())]));
        _presets.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return true;
    }

    /// <summary>Removes the preset called <paramref name="name"/>; true when there was one.</summary>
    public bool Delete(string name) => _presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Copies of the steps of the preset called <paramref name="name"/>, ready to load into the dialog; null when there is none.</summary>
    public IReadOnlyList<RenameDraft>? Steps(string name) =>
        _presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } preset ? [.. preset.Steps.Select(s => s.Clone())] : null;

    /// <summary>Writes the presets to the store, on the thread pool.</summary>
    public Task SaveAsync()
    {
        var snapshot = _presets.ToList();
        return Task.Run(() => _store.Save(snapshot));
    }
}
