namespace Leviathan.ViewModels.Tags;

/// <summary>The colours the Tags page offers (spec §11.1): the seven defaults and a few more, as <c>#RRGGBB</c>.</summary>
public static class TagPalette
{
    /// <summary>Name shown as the tip, and the colour.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Colours { get; } =
    [
        ("Red", "#E5484D"),
        ("Orange", "#F76B15"),
        ("Yellow", "#F5C400"),
        ("Green", "#30A46C"),
        ("Teal", "#12A594"),
        ("Blue", "#0090FF"),
        ("Purple", "#8E4EC6"),
        ("Pink", "#D6409F"),
        ("Brown", "#AD7F58"),
        ("Gray", "#8B8D98"),
    ];

    /// <summary>True for <c>#RRGGBB</c>.</summary>
    public static bool IsColour(string? text) =>
        Sidebar.FavoriteStyles.IsColor(text);
}
