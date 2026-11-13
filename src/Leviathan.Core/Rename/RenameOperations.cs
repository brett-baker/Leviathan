namespace Leviathan.Core.Rename;

/// <summary>Which part of a name an operation works on (spec §15.3).</summary>
public enum RenameTarget
{
    /// <summary>The name without its extension (a folder's whole name).</summary>
    Stem,

    /// <summary>The extension, without its dot.</summary>
    Extension,

    /// <summary>The whole name, extension included.</summary>
    Name,
}

/// <summary>How <see cref="ChangeCase"/> rewrites letters.</summary>
public enum CaseStyle
{
    /// <summary>all lower case</summary>
    Lower,

    /// <summary>ALL UPPER CASE</summary>
    Upper,

    /// <summary>Each Word Capitalised (words end at space, underscore, hyphen and dot).</summary>
    Title,

    /// <summary>First letter capital, the rest lower case.</summary>
    Sentence,
}

/// <summary>Facts about a picture or video that the media tokens use; any may be unknown.</summary>
/// <param name="Taken">When it was taken or recorded.</param>
/// <param name="Duration">Length of a video.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Codec">Video codec name.</param>
public sealed record MediaFacts(DateTime? Taken = null, TimeSpan? Duration = null, int? Width = null, int? Height = null, string? Codec = null);

/// <summary>One file or folder to rename.</summary>
/// <param name="Path">Full path.</param>
/// <param name="IsFolder">True for a folder (its name has no extension).</param>
/// <param name="Modified">Last write time, for <c>{date}</c>.</param>
/// <param name="Media">Media facts for <c>{taken}</c>, <c>{duration}</c>, <c>{resolution}</c> and <c>{codec}</c>, when known.</param>
public sealed record RenameItem(string Path, bool IsFolder, DateTime Modified, MediaFacts? Media = null);

/// <summary>One step of a bulk rename; steps run in the order given, each on the result of the one before (spec §15.3).</summary>
public abstract record RenameOperation;

/// <summary>Replaces text. With <paramref name="UseRegex"/> the pattern is a .NET regular expression and the replacement may use $1; otherwise both are plain and the replacement may use tokens.</summary>
public sealed record FindReplace(string Find, string Replace, bool UseRegex = false, bool MatchCase = false, RenameTarget Target = RenameTarget.Stem) : RenameOperation;

/// <summary>Inserts text (tokens allowed) into the stem at <paramref name="Position"/> characters from the start, or from the end when <paramref name="FromEnd"/>.</summary>
public sealed record Insert(string Text, int Position = 0, bool FromEnd = false) : RenameOperation;

/// <summary>Removes <paramref name="Count"/> characters of the stem starting <paramref name="Start"/> characters from the start (or from the end).</summary>
public sealed record RemoveCharacters(int Start, int Count, bool FromEnd = false) : RenameOperation;

/// <summary>Changes the case of letters.</summary>
public sealed record ChangeCase(CaseStyle Style, RenameTarget Target = RenameTarget.Stem) : RenameOperation;

/// <summary>Trims white space (and <paramref name="Extra"/> characters) from both ends and optionally collapses runs of spaces to one.</summary>
public sealed record TrimName(string Extra = "", bool CollapseSpaces = false, RenameTarget Target = RenameTarget.Stem) : RenameOperation;

/// <summary>Adds a running number: item 1 gets <paramref name="Start"/>, item 2 gets Start + Step, and so on, zero-padded to <paramref name="Padding"/> digits, joined with <paramref name="Separator"/>.</summary>
public sealed record NumberSequence(int Start = 1, int Step = 1, int Padding = 1, bool AtStart = false, string Separator = " ") : RenameOperation;

/// <summary>Replaces the stem with a pattern made of text and tokens: <c>{name}</c> <c>{ext}</c> <c>{date:fmt}</c> <c>{taken:fmt}</c> <c>{duration}</c> <c>{resolution}</c> <c>{codec}</c> <c>{parent}</c> <c>{counter:000}</c>.</summary>
public sealed record Template(string Pattern) : RenameOperation;

/// <summary>Replaces each whole name with the matching line of a pasted list: item 1 gets the first, item 2 the second. An item with no line is invalid.</summary>
public sealed record NameList(IReadOnlyList<string> Names) : RenameOperation;
