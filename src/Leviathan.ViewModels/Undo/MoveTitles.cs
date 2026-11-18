namespace Leviathan.ViewModels.Undo;

/// <summary>
/// The names of the jobs that move items, as the Operations tab shows them: a move forward is "Move", taking an action back is "Undo" (and
/// "Redo" for putting it again), putting bin items back is "Restore" (that title comes from the worker). The same worker job does all of
/// them; the name says what the user did, not how it ran.
/// </summary>
public static class MoveTitles
{
    /// <summary>"Move a.mkv" or "Move 12 items".</summary>
    public static string Move(IReadOnlyList<ItemMove> moves) => $"Move {What(moves)}";

    /// <summary>"Undo: move a.mkv back" or "Undo: move 12 items back".</summary>
    public static string Undo(IReadOnlyList<ItemMove> moves) => $"Undo: move {What(moves)} back";

    /// <summary>"Redo: move a.mkv" or "Redo: move 12 items".</summary>
    public static string Redo(IReadOnlyList<ItemMove> moves) => $"Redo: move {What(moves)}";

    /// <summary>"Restore a.mkv": an item brought back out of the bin.</summary>
    public static string Restore(IReadOnlyList<ItemMove> moves) => $"Restore {What(moves)}";

    /// <summary>"Rename a.mkv" or "Rename 12 items".</summary>
    public static string Rename(IReadOnlyList<ItemMove> moves) => $"Rename {What(moves)}";

    private static string What(IReadOnlyList<ItemMove> moves) =>
        moves.Count == 1 ? System.IO.Path.GetFileName(moves[0].To.TrimEnd('\\', '/')) : $"{moves.Count} items";
}
