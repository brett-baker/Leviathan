using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Triage;

/// <summary>One triage command for the Keyboard page and the overlay: its id, its name and what group it belongs to.</summary>
/// <param name="Id">The id the settings file uses (<c>triageMode.keep</c>).</param>
/// <param name="Title">The name shown to the user.</param>
/// <param name="Category">The group on the Keyboard page.</param>
public sealed record TriageCommandInfo(CommandId Id, string Title, string Category);

/// <summary>
/// The triage commands and their default keys (spec §10.2: "all remappable"). They live in a key map of their own, because Space, the
/// arrows, Delete and the digits mean something else in the file view, and only one of the two is ever active. The ids start with
/// <c>triageMode.</c> and are remapped through the same <c>KeyBindings</c> of the settings file as every other command (spec §17.3), with the
/// same rule: an entry replaces every default key of its command, an empty text removes them, and a key another command held goes to the
/// one the file names.
/// </summary>
public static class TriageCommands
{
    /// <summary>The prefix of every triage command id.</summary>
    public const string Prefix = "triageMode.";

    private static readonly CommandId[] TagIds = [.. Enumerable.Range(1, 9).Select(i => new CommandId($"triageMode.tag{i}"))];
    private static readonly CommandId[] JumpIds = [.. Enumerable.Range(1, 9).Select(i => new CommandId($"triageMode.jump{i * 10}"))];

    /// <summary>Play or pause.</summary>
    public static readonly CommandId PlayPause = new("triageMode.playPause");

    /// <summary>Jump forward 30 s.</summary>
    public static readonly CommandId Forward30 = new("triageMode.forward30");

    /// <summary>Jump back 30 s.</summary>
    public static readonly CommandId Back30 = new("triageMode.back30");

    /// <summary>Jump forward 1 min.</summary>
    public static readonly CommandId Forward1m = new("triageMode.forward1m");

    /// <summary>Jump back 1 min.</summary>
    public static readonly CommandId Back1m = new("triageMode.back1m");

    /// <summary>Jump forward 5 min.</summary>
    public static readonly CommandId Forward5m = new("triageMode.forward5m");

    /// <summary>Jump back 5 min.</summary>
    public static readonly CommandId Back5m = new("triageMode.back5m");

    /// <summary>Fine seek forward 5 s.</summary>
    public static readonly CommandId Forward5s = new("triageMode.forward5s");

    /// <summary>Fine seek back 5 s.</summary>
    public static readonly CommandId Back5s = new("triageMode.back5s");

    /// <summary>Next frame.</summary>
    public static readonly CommandId FrameNext = new("triageMode.frameNext");

    /// <summary>Previous frame.</summary>
    public static readonly CommandId FramePrevious = new("triageMode.framePrevious");

    /// <summary>Back to the start.</summary>
    public static readonly CommandId Start = new("triageMode.start");

    /// <summary>To the last 10 seconds.</summary>
    public static readonly CommandId End = new("triageMode.end");

    /// <summary>Reverse skim (J).</summary>
    public static readonly CommandId Rewind = new("triageMode.rewind");

    /// <summary>Pause (K).</summary>
    public static readonly CommandId Pause = new("triageMode.pause");

    /// <summary>Forward with increasing speed (L).</summary>
    public static readonly CommandId FastForward = new("triageMode.fastForward");

    /// <summary>Slower.</summary>
    public static readonly CommandId SpeedDown = new("triageMode.speedDown");

    /// <summary>Faster.</summary>
    public static readonly CommandId SpeedUp = new("triageMode.speedUp");

    /// <summary>Toggle skim playback (S, slice E6).</summary>
    public static readonly CommandId ToggleSkim = new("triageMode.skim");

    /// <summary>Keep the file and go on.</summary>
    public static readonly CommandId Keep = new("triageMode.keep");

    /// <summary>Discard the file and go on.</summary>
    public static readonly CommandId Discard = new("triageMode.discard");

    /// <summary>Clear every colour tag on the file.</summary>
    public static readonly CommandId ClearTags = new("triageMode.clearTags");

    /// <summary>Skip: no decision, next file.</summary>
    public static readonly CommandId Skip = new("triageMode.skip");

    /// <summary>Previous file.</summary>
    public static readonly CommandId Previous = new("triageMode.previous");

    /// <summary>Show the contact sheet.</summary>
    public static readonly CommandId ContactSheet = new("triageMode.contactSheet");

    /// <summary>Rename inline.</summary>
    public static readonly CommandId Rename = new("triageMode.rename");

    /// <summary>Move to a preset folder.</summary>
    public static readonly CommandId MoveToPreset = new("triageMode.move");

    /// <summary>Undo the last decision.</summary>
    public static readonly CommandId Undo = new("triageMode.undo");

    /// <summary>Show or hide the file overlay.</summary>
    public static readonly CommandId ToggleOverlay = new("triageMode.overlay");

    /// <summary>Leave triage mode.</summary>
    public static readonly CommandId Exit = new("triageMode.exit");

    /// <summary>The command that toggles colour tag <paramref name="number"/> (1 to 9).</summary>
    public static CommandId Tag(int number) => TagIds[number - 1];

    /// <summary>The command that jumps to <paramref name="tenths"/> tenths of the file (1 to 9).</summary>
    public static CommandId JumpToTenth(int tenths) => JumpIds[tenths - 1];

    /// <summary>True when <paramref name="id"/> names a triage command (whatever the file says, so the app's own builder can leave it alone).</summary>
    public static bool IsTriage(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True when <paramref name="id"/> is one of the commands below.</summary>
    public static bool IsKnown(CommandId id) => Known.Contains(id);

    /// <summary>Every command, in the order the Keyboard page lists them.</summary>
    public static IReadOnlyList<TriageCommandInfo> All { get; } =
    [
        new(Keep, "Keep, then next file", "Decide"),
        new(Discard, "Discard, then next file", "Decide"),
        new(Skip, "Skip: no decision, next file", "Decide"),
        new(Previous, "Previous file", "Decide"),
        new(Undo, "Undo the last decision", "Decide"),
        new(ClearTags, "Clear all tags", "Decide"),
        .. TagIds.Select((id, i) => new TriageCommandInfo(id, $"Toggle tag {i + 1}", "Decide")),
        new(PlayPause, "Play or pause", "Playback"),
        new(Forward30, "Forward 30 s", "Playback"),
        new(Back30, "Back 30 s", "Playback"),
        new(Forward1m, "Forward 1 min", "Playback"),
        new(Back1m, "Back 1 min", "Playback"),
        new(Forward5m, "Forward 5 min", "Playback"),
        new(Back5m, "Back 5 min", "Playback"),
        new(Forward5s, "Forward 5 s", "Playback"),
        new(Back5s, "Back 5 s", "Playback"),
        new(FrameNext, "Next frame", "Playback"),
        new(FramePrevious, "Previous frame", "Playback"),
        .. JumpIds.Select((id, i) => new TriageCommandInfo(id, $"Jump to {(i + 1) * 10}%", "Playback")),
        new(Start, "Go to the start", "Playback"),
        new(End, "Go to the last 10 s", "Playback"),
        new(Rewind, "Reverse skim", "Playback"),
        new(Pause, "Pause", "Playback"),
        new(FastForward, "Forward, faster each press", "Playback"),
        new(SpeedDown, "Slower", "Playback"),
        new(SpeedUp, "Faster", "Playback"),
        new(ToggleSkim, "Toggle skim playback", "Playback"),
        new(ContactSheet, "Contact sheet", "Window"),
        new(Rename, "Rename", "Window"),
        new(MoveToPreset, "Move to a preset folder", "Window"),
        new(ToggleOverlay, "Show or hide the file overlay", "Window"),
        new(Exit, "Leave triage mode", "Window"),
    ];

    private static readonly HashSet<CommandId> Known = [.. All.Select(c => c.Id)];

    /// <summary>The default keys of spec §10.2.</summary>
    public static KeyMap DefaultKeyMap()
    {
        var map = new KeyMap();
        void Bind(CommandId command, params string[] gestures)
        {
            foreach (var gesture in gestures)
            {
                map.TryBind(new KeyBinding(KeyGesture.Parse(gesture), command));
            }
        }

        Bind(PlayPause, "Space");
        Bind(Forward30, "Right");
        Bind(Back30, "Left");
        Bind(Forward1m, "Shift+Right");
        Bind(Back1m, "Shift+Left");
        Bind(Forward5m, "Ctrl+Right");
        Bind(Back5m, "Ctrl+Left");
        Bind(Forward5s, "Alt+Right");
        Bind(Back5s, "Alt+Left");
        Bind(FrameNext, ".");
        Bind(FramePrevious, ",");
        for (var i = 1; i <= 9; i++)
        {
            Bind(JumpToTenth(i), $"Ctrl+{i}");
            Bind(Tag(i), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Bind(Start, "Home");
        Bind(End, "End");
        Bind(Rewind, "J");
        Bind(Pause, "K");
        Bind(FastForward, "L");
        Bind(SpeedDown, "[");
        Bind(SpeedUp, "]");
        Bind(ToggleSkim, "S");
        Bind(Keep, "Enter", "G");
        Bind(Discard, "Delete", "X");
        Bind(ClearTags, "Ctrl+0");
        Bind(Skip, "Down", "Tab");
        Bind(Previous, "Up", "Shift+Tab");
        Bind(ContactSheet, "C");
        Bind(Rename, "R");
        Bind(MoveToPreset, "M");
        Bind(Undo, "Ctrl+Z");
        Bind(ToggleOverlay, "I");
        Bind(Exit, "Escape");
        return map;
    }

    /// <summary>
    /// The triage key map with the user's changes: the <c>triageMode.*</c> entries of the settings file's <c>KeyBindings</c> applied to the
    /// defaults. Other entries belong to the file view's commands and are left alone here.
    /// </summary>
    public static KeyMapResult BuildKeyMap(IReadOnlyDictionary<string, string>? overrides) =>
        KeyMapBuilder.Build(overrides?.Where(p => IsTriage(p.Key)).ToDictionary(p => p.Key, p => p.Value), IsKnown, DefaultKeyMap());

    /// <summary>The keys of triage commands as rows for the Keyboard page, grouped like <see cref="All"/>.</summary>
    public static IReadOnlyList<KeyboardRow> Rows(KeyMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var defaults = DefaultKeyMap();
        return
        [
            .. All.Select(c => new KeyboardRow(
                c.Id,
                c.Title,
                "Triage · " + c.Category,
                string.Join(", ", map.GesturesFor(c.Id)),
                !map.GesturesFor(c.Id).SequenceEqual(defaults.GesturesFor(c.Id)))),
        ];
    }
}
