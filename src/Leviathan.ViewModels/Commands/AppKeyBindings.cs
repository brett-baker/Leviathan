namespace Leviathan.ViewModels.Commands;

/// <summary>
/// The key maps the app runs with: the default keys with the user's changes from the settings file (spec §17.3), for the file view and
/// for triage mode, plus what could not be used. Pure, so the rules are tested without the app.
/// </summary>
public sealed record AppKeyBindings(KeyMap Keys, KeyMap TriageKeys, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    /// <summary>A text that is equal for equal bindings, so the maps are only rebuilt when the settings file's <c>KeyBindings</c> changed.</summary>
    public static string SignatureOf(IReadOnlyDictionary<string, string> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return string.Join('\n', bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => $"{b.Key}={b.Value}"));
    }

    /// <summary>Builds both maps. Triage has its own keys (<see cref="Triage.TriageCommands"/>); its entries share the file but are not the file view's commands.</summary>
    public static AppKeyBindings Build(IReadOnlyDictionary<string, string> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var fileView = KeyMapBuilder.Build(bindings.Where(b => !Triage.TriageCommands.IsTriage(b.Key)).ToDictionary(b => b.Key, b => b.Value), CommandIds.All.Contains);
        var triage = Triage.TriageCommands.BuildKeyMap(bindings);
        return new AppKeyBindings(fileView.Map, triage.Map, [.. fileView.Problems, .. triage.Problems], [.. fileView.Notes, .. triage.Notes]);
    }
}
