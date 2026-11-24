namespace Leviathan.ViewModels.Undo;

/// <summary>
/// The names of the jobs that move items, as the Operations tab shows them: a move forward is "Move", taking an action back is "Undo" (and
/// "Redo" for putting it again), putting bin items back is "Restore" (that title comes from the worker). The same worker job does all of
/// them; the name says what the user did, not how it ran. Every title names the folder the item went to, so the list says where a file
/// ended up and not only what it was called.
/// </summary>
public static class MoveTitles
{
    /// <summary>"Move a.mkv to D:\keep" or "Move 12 items to D:\keep".</summary>
    public static string Move(IReadOnlyList<ItemMove> moves) => $"Move {What(moves)}{To(moves)}";

    /// <summary>"Keep: move a.mkv to D:\keep": what triage's Keep did.</summary>
    public static string Keep(IReadOnlyList<ItemMove> moves) => $"Keep: move {What(moves)}{To(moves)}";

    /// <summary>"Undo: move a.mkv back to D:\in" or "Undo: move 12 items back to D:\in".</summary>
    public static string Undo(IReadOnlyList<ItemMove> moves) => $"Undo: move {What(moves)} back{To(moves)}";

    /// <summary>"Redo: move a.mkv to D:\keep" or "Redo: move 12 items to D:\keep".</summary>
    public static string Redo(IReadOnlyList<ItemMove> moves) => $"Redo: move {What(moves)}{To(moves)}";

    /// <summary>"Restore a.mkv": an item brought back out of the bin.</summary>
    public static string Restore(IReadOnlyList<ItemMove> moves) => $"Restore {What(moves)}";

    /// <summary>"Rename a.mkv" or "Rename 12 items".</summary>
    public static string Rename(IReadOnlyList<ItemMove> moves) => $"Rename {What(moves)}";

    /// <summary>" to the folder" the first item went to; empty when the item has no folder to name.</summary>
    private static string To(IReadOnlyList<ItemMove> moves) =>
        moves.Count > 0 && System.IO.Path.GetDirectoryName(moves[0].To.TrimEnd('\\', '/')) is { Length: > 0 } folder ? $" to {folder}" : string.Empty;

    private static string What(IReadOnlyList<ItemMove> moves) =>
        moves.Count == 1 ? System.IO.Path.GetFileName(moves[0].To.TrimEnd('\\', '/')) : $"{moves.Count} items";
}
