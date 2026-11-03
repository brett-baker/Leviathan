namespace Leviathan.ViewModels.Icons;

/// <summary>
/// What the app means by an icon, independent of any icon pack (spec §4.3, D33).
/// </summary>
/// <param name="Key">The semantic name packs map to a file, e.g. <c>delete</c>.</param>
/// <param name="Glyph">The Segoe Fluent glyph used when a pack has no such icon, and in high-contrast mode.</param>
/// <param name="Accent">The colour of the icon in a single-colour pack as <c>#RRGGBB</c>, or null to follow the text colour. The role owns the colour, not the pack, so swapping packs keeps red for Delete.</param>
public sealed record IconDefinition(string Key, string Glyph, string? Accent = null);

/// <summary>
/// Every icon the app's own chrome uses (command bar, tree, tabs, status bar, menus) by semantic key (spec §4.3). A pack
/// (<see cref="IconPackManifest"/>) says which of its files draws each key; keys it leaves out fall back to the glyph here, so a
/// partial pack still works. File-view icons are not here: they come from the shell.
/// </summary>
public static class IconCatalog
{
    /// <summary>Blue: copy and paste, links, information.</summary>
    public const string Blue = "#0F6CBD";

    /// <summary>Green: new, confirm, play, download.</summary>
    public const string Green = "#107C10";

    /// <summary>Red: delete, danger.</summary>
    public const string Red = "#D13438";

    /// <summary>Amber: folders, favourites, warnings.</summary>
    public const string Amber = "#E8A317";

    /// <summary>Purple: video.</summary>
    public const string Purple = "#8764B8";

    /// <summary>Teal: pictures.</summary>
    public const string Teal = "#0F7B8B";

    /// <summary>Orange: music.</summary>
    public const string Orange = "#E3562A";

    private static readonly IReadOnlyList<IconDefinition> Definitions =
    [
        new("back", ""),
        new("forward", ""),
        new("up", ""),
        new("reload", ""),
        new("chevron-down", ""),
        new("chevron-up", ""),
        new("chevron-right", ""),
        new("add", "", Green),
        new("cut", "", Blue),
        new("copy", "", Blue),
        new("paste", "", Blue),
        new("rename", ""),
        new("delete", "", Red),
        new("undo", ""),
        new("redo", ""),
        new("settings", ""),
        new("close", ""),
        new("folder", "", Amber),
        new("folder-open", "", Amber),
        new("file", ""),
        new("link", "", Blue),
        new("lock", ""),
        new("warning", "", Amber),
        new("check", "", Green),
        new("not-found", "", Red),
        new("cloud", "", Blue),
        new("home", ""),
        new("desktop", "", Blue),
        new("downloads", "", Green),
        new("pictures", "", Teal),
        new("videos", "", Purple),
        new("music", "", Orange),
        new("drive", ""),
        new("removable", ""),
        new("network", "", Blue),
        new("star", "", Amber),
        new("star-filled", "", Amber),
        new("heart", "", Red),
        new("flag", "", Red),
        new("pin", "", Red),
        new("work", ""),
        new("code", "", Blue),
        new("video", "", Purple),
        new("photo", "", Teal),
        new("tag", ""),
        new("swatch", ""),
        new("layout-details", ""),
        new("layout-list", ""),
        new("layout-tiles", ""),
        new("layout-thumbnails", ""),
        new("layout-gallery", ""),
        new("zoom-in", ""),
        new("zoom-out", ""),
        new("columns", ""),
        new("select-all", ""),
        new("select-none", ""),
        new("select-invert", ""),
        new("select-pattern", ""),
        new("terminal", ""),
        new("space-map", "", Blue),
        new("archive", "", Amber),
        new("split", ""),
        new("open-new-tab", ""),
        new("properties", "", Blue),
        new("compare", ""),
        new("move-to-folder", ""),
        new("play", "", Green),
        new("frame", ""),
        new("rotate-left", ""),
        new("rotate-right", ""),
        new("convert", ""),
        new("print", ""),
        new("preview", ""),
        new("open", ""),
        new("admin", "", Blue),
        new("checksum", ""),
        new("target", ""),
        new("edit", ""),
        new("burn", ""),
        new("tab", ""),
        new("wallpaper", ""),
    ];

    private static readonly Dictionary<string, IconDefinition> ByKey = Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    // The first definition of a glyph wins: "wallpaper" shares the desktop glyph, which means "desktop".
    private static readonly Dictionary<string, IconDefinition> ByGlyph = Definitions
        .GroupBy(d => d.Glyph, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>Every icon, in the order they are declared.</summary>
    public static IReadOnlyList<IconDefinition> All => Definitions;

    /// <summary>The definition of <paramref name="key"/>, or null when the app has no such icon.</summary>
    public static IconDefinition? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ByKey.GetValueOrDefault(key);
    }

    /// <summary>
    /// The definition that stands for a Segoe Fluent glyph, or null. This lets menus and models that still carry a glyph string
    /// (command bar model, favourite styles) draw from the chosen pack without every call site naming a key.
    /// </summary>
    public static IconDefinition? FindByGlyph(string? glyph) =>
        string.IsNullOrEmpty(glyph) ? null : ByGlyph.GetValueOrDefault(glyph);
}
