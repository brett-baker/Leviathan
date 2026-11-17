using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Commands;

/// <summary>The decisions of the inline rename that need no tab: pure, so they are tested on their own (spec §8.4).</summary>
public static class RenameFlow
{
    /// <summary>
    /// The row to rename after <paramref name="renamed"/> when Tab was pressed: the next row of the list, or null when the item is not in
    /// the list or is its last row.
    /// </summary>
    public static T? NextRow<T>(IReadOnlyList<T> rows, Func<T, LocationPath> locationOf, LocationPath renamed)
        where T : class
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (locationOf(rows[i]) == renamed)
            {
                return i + 1 < rows.Count ? rows[i + 1] : null;
            }
        }

        return null;
    }
}
