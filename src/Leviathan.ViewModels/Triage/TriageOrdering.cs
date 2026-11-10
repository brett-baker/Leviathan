using Leviathan.Core.Text;

namespace Leviathan.ViewModels.Triage;

/// <summary>The order of the files in the triage queue list (spec §10.1), chosen in the window and remembered in the settings.</summary>
public enum TriageListOrder
{
    /// <summary>The order the queue was made in (the folder's order, or the inbox's).</summary>
    AsOpened,

    /// <summary>A to Z, digits by value (clip2 before clip10).</summary>
    NameAscending,

    /// <summary>Z to A.</summary>
    NameDescending,

    /// <summary>Oldest modified first.</summary>
    OldestFirst,

    /// <summary>Newest modified first.</summary>
    NewestFirst,

    /// <summary>Largest first, to reclaim the most space early.</summary>
    LargestFirst,

    /// <summary>Smallest first.</summary>
    SmallestFirst,
}

/// <summary>The names of the orders and the rule for ordering the files still to be decided.</summary>
public static class TriageListOrdering
{
    /// <summary>Every order, in the order the window lists them.</summary>
    public static IReadOnlyList<TriageListOrder> All { get; } = [.. Enum.GetValues<TriageListOrder>()];

    /// <summary>The words the window shows for <paramref name="order"/>.</summary>
    public static string Title(TriageListOrder order) => order switch
    {
        TriageListOrder.AsOpened => "As opened",
        TriageListOrder.NameAscending => "Name A to Z",
        TriageListOrder.NameDescending => "Name Z to A",
        TriageListOrder.OldestFirst => "Oldest first",
        TriageListOrder.NewestFirst => "Newest first",
        TriageListOrder.LargestFirst => "Largest first",
        TriageListOrder.SmallestFirst => "Smallest first",
        _ => order.ToString(),
    };

    /// <summary>
    /// <paramref name="items"/> in <paramref name="order"/>. Ties are broken by the position the queue was made in, so the result is the
    /// same every time. Sizes and dates are the ones from when the queue was made (<see cref="TriageItem.Original"/>), so a file that a
    /// Keep has already moved sorts the same as before.
    /// </summary>
    internal static List<TriageItem> Sort(IEnumerable<TriageItem> items, IReadOnlyDictionary<TriageItem, int> opened, TriageListOrder order)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(opened);
        IOrderedEnumerable<TriageItem> sorted = order switch
        {
            TriageListOrder.NameAscending => items.OrderBy(i => i.Original.Name, NaturalStringComparer.Instance),
            TriageListOrder.NameDescending => items.OrderByDescending(i => i.Original.Name, NaturalStringComparer.Instance),
            TriageListOrder.OldestFirst => items.OrderBy(i => i.Original.Modified),
            TriageListOrder.NewestFirst => items.OrderByDescending(i => i.Original.Modified),
            TriageListOrder.LargestFirst => items.OrderByDescending(i => i.Original.Size),
            TriageListOrder.SmallestFirst => items.OrderBy(i => i.Original.Size),
            _ => items.OrderBy(i => opened[i]),
        };
        return [.. sorted.ThenBy(i => opened[i])];
    }
}
