using System.Globalization;
using System.Text.RegularExpressions;

namespace Leviathan.ViewModels.Tags;

/// <summary>One colour tag (spec §11.1): a stable id, a name the user can change and a colour as <c>#RRGGBB</c>.</summary>
/// <param name="Id">Never changes and never reused, because it is what is stored on files; the order and the name can change.</param>
/// <param name="Name">What the tag is called.</param>
/// <param name="Colour">The colour as <c>#RRGGBB</c>.</param>
public sealed record ColorTag(int Id, string Name, string Colour);

/// <summary>
/// The set of colour tags and their order (spec §11.1). Starts as the seven defaults (Red, Orange, Yellow, Green "Keep", Blue, Purple,
/// Gray); any of them can be renamed, recoloured and reordered and up to 32 can exist. Keys 1 to 9 mean the first nine in the current
/// order, so reordering changes what a key does but never what is already on a file (files store the id). A catalog built from a
/// hand-edited settings file is cleaned instead of rejected, so a bad entry can never leave the app without tags.
/// Immutable: every change returns a new catalog.
/// </summary>
public sealed partial class TagCatalog
{
    /// <summary>The most tags there can be.</summary>
    public const int MaxTags = 32;

    /// <summary>How many tags the number keys reach.</summary>
    public const int KeyedTags = 9;

    /// <summary>The longest tag name.</summary>
    public const int MaxNameLength = 40;

    private const string GrayColour = "#8B8D98";

    private static readonly ColorTag[] Defaults =
    [
        new(1, "Red", "#E5484D"),
        new(2, "Orange", "#F76B15"),
        new(3, "Yellow", "#F5C400"),
        new(4, "Green", "#30A46C"),
        new(5, "Blue", "#0090FF"),
        new(6, "Purple", "#8E4EC6"),
        new(7, "Gray", GrayColour),
    ];

    /// <summary>The seven default tags.</summary>
    public static TagCatalog Default { get; } = new(Defaults);

    /// <summary>Creates a catalog from <paramref name="tags"/>, cleaning them: bad ids and repeats are dropped, names are trimmed, colours made valid, and the list cut at 32. An empty result is the defaults.</summary>
    public TagCatalog(IEnumerable<ColorTag>? tags)
    {
        var seen = new HashSet<int>();
        var list = new List<ColorTag>();
        foreach (var tag in tags ?? [])
        {
            if (tag is null || tag.Id <= 0 || !seen.Add(tag.Id) || list.Count >= MaxTags)
            {
                continue;
            }

            list.Add(new ColorTag(tag.Id, CleanName(tag.Name, tag.Id), CleanColour(tag.Colour)));
        }

        if (list.Count == 0)
        {
            list.AddRange(Defaults);
        }

        Tags = list;
    }

    /// <summary>The tags in the order the keys and lists use.</summary>
    public IReadOnlyList<ColorTag> Tags { get; }

    /// <summary>The tag with this id, or null.</summary>
    public ColorTag? Find(int id) => Tags.FirstOrDefault(t => t.Id == id);

    /// <summary>The tag key <paramref name="number"/> (1 to 9) toggles, or null when there are fewer tags than that.</summary>
    public ColorTag? ForKey(int number) => number is >= 1 and <= KeyedTags && number <= Tags.Count ? Tags[number - 1] : null;

    /// <summary>The key (1 to 9) that toggles this tag, or 0 when it is beyond the ninth.</summary>
    public int KeyOf(int id)
    {
        var index = IndexOf(id);
        return index is >= 0 and < KeyedTags ? index + 1 : 0;
    }

    /// <summary>The same tags with one renamed. A blank name is ignored.</summary>
    public TagCatalog Rename(int id, string name) =>
        string.IsNullOrWhiteSpace(name) ? this : Change(id, t => t with { Name = name });

    /// <summary>The same tags with one recoloured. A colour that is not <c>#RRGGBB</c> is ignored.</summary>
    public TagCatalog Recolour(int id, string colour) => ColourRegex().IsMatch(colour ?? string.Empty) ? Change(id, t => t with { Colour = colour! }) : this;

    /// <summary>The same tags with one moved to <paramref name="index"/> (0 is first; clamped).</summary>
    public TagCatalog Move(int id, int index)
    {
        var from = IndexOf(id);
        if (from < 0)
        {
            return this;
        }

        var list = Tags.ToList();
        var tag = list[from];
        list.RemoveAt(from);
        list.Insert(Math.Clamp(index, 0, list.Count), tag);
        return new TagCatalog(list);
    }

    /// <summary>The same tags with a new one at the end, given the next unused id. Throws when there are already 32.</summary>
    public TagCatalog Add(string name, string colour)
    {
        if (Tags.Count >= MaxTags)
        {
            throw new InvalidOperationException($"There can be at most {MaxTags} tags.");
        }

        var id = Tags.Max(t => t.Id) + 1;
        return new TagCatalog([.. Tags, new ColorTag(id, name, colour)]);
    }

    /// <summary>
    /// The standard tags back in their standard names, colours and order, with any tags the user added after them (ids above the
    /// defaults) kept as they are, so nothing already tagged with them loses its meaning.
    /// </summary>
    public TagCatalog ResetStandard() => new([.. Defaults, .. Tags.Where(t => t.Id > Defaults.Length)]);

    private TagCatalog Change(int id, Func<ColorTag, ColorTag> change) =>
        IndexOf(id) < 0 ? this : new TagCatalog(Tags.Select(t => t.Id == id ? change(t) : t));

    private int IndexOf(int id)
    {
        for (var i = 0; i < Tags.Count; i++)
        {
            if (Tags[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static string CleanName(string? name, int id)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Defaults.FirstOrDefault(d => d.Id == id)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"Tag {id}");
        }

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static string CleanColour(string? colour) => ColourRegex().IsMatch(colour ?? string.Empty) ? colour!.ToUpperInvariant() : GrayColour;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColourRegex();
}
