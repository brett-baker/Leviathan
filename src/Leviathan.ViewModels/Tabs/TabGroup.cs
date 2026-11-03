using CommunityToolkit.Mvvm.ComponentModel;

namespace Leviathan.ViewModels.Tabs;

/// <summary>A tab group in a form that can be saved or carried to another window (spec §4.1, §17.1).</summary>
/// <param name="Id">Identity; tabs name their group by it.</param>
/// <param name="Name">Label shown on the chip; may be empty (a group that only has a colour).</param>
/// <param name="Color">Colour as <c>#RRGGBB</c>.</param>
/// <param name="IsCollapsed">True when the group is folded down to its chip.</param>
public sealed record TabGroupInfo(Guid Id, string Name, string Color, bool IsCollapsed = false);

/// <summary>
/// A named, coloured, collapsible set of tabs (spec §4.1, D32), modelled on tab groups in Microsoft Edge. The tabs of a group sit
/// next to each other in the strip; <see cref="TabStripViewModel"/> keeps that true. Pinned tabs are never in a group.
/// </summary>
public sealed partial class TabGroup : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _color;

    [ObservableProperty]
    private bool _isCollapsed;

    /// <summary>Creates a group.</summary>
    public TabGroup(Guid id, string name, string color, bool isCollapsed = false)
    {
        Id = id;
        _name = name ?? string.Empty;
        _color = TabGroupColors.IsColor(color) ? color.ToUpperInvariant() : TabGroupColors.Default.Hex;
        _isCollapsed = isCollapsed;
    }

    /// <summary>Stable identity.</summary>
    public Guid Id { get; }

    /// <summary>Captures the group for saving.</summary>
    public TabGroupInfo ToInfo() => new(Id, Name, Color, IsCollapsed);

    /// <summary>Rebuilds a group from <paramref name="info"/>.</summary>
    public static TabGroup FromInfo(TabGroupInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new TabGroup(info.Id, info.Name, info.Color, info.IsCollapsed);
    }
}

/// <summary>The colours a group can have: Edge's palette (spec §4.1 "from the same palette as tab colours").</summary>
public static class TabGroupColors
{
    /// <summary>Colours in the order new groups are given them.</summary>
    public static IReadOnlyList<(string Title, string Hex)> All { get; } =
    [
        ("Blue", "#0078D4"),
        ("Red", "#E74856"),
        ("Yellow", "#FFB900"),
        ("Green", "#16C60C"),
        ("Pink", "#E3008C"),
        ("Purple", "#886CE4"),
        ("Cyan", "#00B7C3"),
        ("Orange", "#F7630C"),
        ("Grey", "#8A8A8A"),
    ];

    /// <summary>The colour of a group made without a choice and when everything else is taken.</summary>
    public static (string Title, string Hex) Default => All[0];

    /// <summary>True for <c>#RRGGBB</c>.</summary>
    public static bool IsColor(string? text) => Sidebar.FavoriteStyles.IsColor(text);

    /// <summary>The first palette colour not in <paramref name="used"/>, or the one used least when all are taken.</summary>
    public static string Next(IEnumerable<string> used)
    {
        var counts = used.Select(u => u.ToUpperInvariant()).GroupBy(u => u).ToDictionary(g => g.Key, g => g.Count());
        return All.OrderBy(c => counts.GetValueOrDefault(c.Hex.ToUpperInvariant())).ThenBy(c => All.ToList().IndexOf(c)).First().Hex;
    }
}
