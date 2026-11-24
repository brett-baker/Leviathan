using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Transfers;

/// <summary>
/// The items that were cut and not yet pasted, as a set the lists can ask about for every row they draw (spec §6.1): a cut file is shown
/// faded, as in Explorer, until it is pasted, replaced on the clipboard or the clipboard is cleared. A copy marks nothing. Paths compare
/// without regard to case, as Windows does.
/// </summary>
public sealed class CutMarks
{
    /// <summary>Nothing is cut.</summary>
    public static CutMarks None { get; } = new([]);

    private readonly HashSet<string> _paths;

    private CutMarks(HashSet<string> paths) => _paths = paths;

    /// <summary>The marks for what is on the clipboard: its items when they were cut, nothing for a copy or an empty clipboard.</summary>
    public static CutMarks Of(ClipboardItems? contents) =>
        contents is { Operation: ClipboardOperation.Cut, Items.Count: > 0 }
            ? new CutMarks(new HashSet<string>(contents.Items.Select(i => Normalize(i.Value)), StringComparer.OrdinalIgnoreCase))
            : None;

    /// <summary>True when nothing is marked, so a list can skip asking per row.</summary>
    public bool IsEmpty => _paths.Count == 0;

    /// <summary>True when <paramref name="location"/> was cut.</summary>
    public bool IsCut(LocationPath location) => _paths.Count > 0 && _paths.Contains(Normalize(location.Value));

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\', '/') : path;
}
