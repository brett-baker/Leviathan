using Leviathan.Core.Rename;

namespace Leviathan.ViewModels.Rename;

/// <summary>The kinds of step the rename dialog offers (spec §15.3).</summary>
public enum DraftKind
{
    /// <summary>Find and replace text.</summary>
    FindReplace,

    /// <summary>Insert text.</summary>
    Insert,

    /// <summary>Remove characters.</summary>
    Remove,

    /// <summary>Change case.</summary>
    Case,

    /// <summary>Trim.</summary>
    Trim,

    /// <summary>Add a number.</summary>
    Number,

    /// <summary>Rebuild the name from a pattern with tokens.</summary>
    Template,

    /// <summary>Take the new names from a pasted list.</summary>
    NameList,
}

/// <summary>
/// One step of a bulk rename as the dialog edits it: plain, settable fields (so the controls bind to them and a preset is a JSON list of
/// them), turned into a <see cref="RenameOperation"/> by <see cref="ToOperation"/>. Only the fields that belong to <see cref="Kind"/> are used.
/// </summary>
public sealed class RenameDraft
{
    /// <summary>Which step this is.</summary>
    public DraftKind Kind { get; set; }

    /// <summary>Find: the text to look for. Insert / Template: the text or pattern. Trim: extra characters to trim. NameList: one name per line.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Find and replace: what replaces it. Number: the separator.</summary>
    public string Replace { get; set; } = string.Empty;

    /// <summary>Find and replace: the text is a regular expression.</summary>
    public bool UseRegex { get; set; }

    /// <summary>Find and replace: upper and lower case are different.</summary>
    public bool MatchCase { get; set; }

    /// <summary>The part of the name the step works on.</summary>
    public RenameTarget Target { get; set; }

    /// <summary>Insert: characters from the start (or end). Remove: where removing starts. Number: the first number.</summary>
    public int Position { get; set; }

    /// <summary>Remove: how many characters. Number: the step between numbers.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Number: digits, zero-padded.</summary>
    public int Padding { get; set; } = 1;

    /// <summary>Insert and Remove count from the end. Number puts the number at the start. Trim collapses runs of spaces.</summary>
    public bool Flag { get; set; }

    /// <summary>How Case rewrites letters.</summary>
    public CaseStyle Style { get; set; } = CaseStyle.Lower;

    /// <summary>A new step of <paramref name="kind"/> with sensible defaults.</summary>
    public static RenameDraft Create(DraftKind kind) => kind switch
    {
        DraftKind.Number => new RenameDraft { Kind = kind, Position = 1, Count = 1, Padding = 2, Replace = " " },
        DraftKind.Template => new RenameDraft { Kind = kind, Text = "{name}" },
        DraftKind.Remove => new RenameDraft { Kind = kind, Position = 0, Count = 1 },
        DraftKind.Trim => new RenameDraft { Kind = kind, Flag = true },
        _ => new RenameDraft { Kind = kind },
    };

    /// <summary>A copy that can be edited without changing this one.</summary>
    public RenameDraft Clone() => (RenameDraft)MemberwiseClone();

    /// <summary>True when the step would do something (a find with nothing to find, or an empty insert, does not).</summary>
    public bool IsActive => Kind switch
    {
        DraftKind.FindReplace => Text.Length > 0,
        DraftKind.Insert => Text.Length > 0,
        DraftKind.Remove => Count > 0,
        DraftKind.Template => Text.Length > 0,
        DraftKind.NameList => Text.Length > 0,
        _ => true,
    };

    /// <summary>The pipeline step this draft stands for.</summary>
    public RenameOperation ToOperation() => Kind switch
    {
        DraftKind.FindReplace => new FindReplace(Text, Replace, UseRegex, MatchCase, Target),
        DraftKind.Insert => new Insert(Text, Math.Max(0, Position), Flag),
        DraftKind.Remove => new RemoveCharacters(Math.Max(0, Position), Math.Max(0, Count), Flag),
        DraftKind.Case => new ChangeCase(Style, Target),
        DraftKind.Trim => new TrimName(Text, Flag, Target),
        DraftKind.Number => new NumberSequence(Position, Count, Math.Clamp(Padding, 1, 18), Flag, Replace),
        DraftKind.Template => new Template(Text),
        _ => new NameList([.. Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)]),
    };

    /// <summary>One line saying what the step does, for the step's heading and the presets list.</summary>
    public string Describe() => Kind switch
    {
        DraftKind.FindReplace => $"Replace '{Text}' with '{Replace}'" + (UseRegex ? " (regex)" : string.Empty),
        DraftKind.Insert => $"Insert '{Text}' at {(Flag ? "end" : "start")} +{Position}",
        DraftKind.Remove => $"Remove {Count} character{(Count == 1 ? string.Empty : "s")} from {Position}",
        DraftKind.Case => $"Change case to {Style}",
        DraftKind.Trim => "Trim spaces" + (Text.Length > 0 ? $" and '{Text}'" : string.Empty),
        DraftKind.Number => $"Number from {Position} by {Count}",
        DraftKind.Template => $"Name as {Text}",
        _ => "Names from a list",
    };
}
