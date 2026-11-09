using System.Runtime.InteropServices;

namespace Leviathan.ViewModels.Preview;

/// <summary>Writes Windows properties of a file through the shell's property handler (the seam to <c>StorageFile.Properties</c>).</summary>
public interface IPropertyWriter
{
    /// <summary>Saves <paramref name="values"/> (property name to value) into the file at <paramref name="path"/>.</summary>
    Task WriteAsync(string path, IReadOnlyDictionary<string, object> values, CancellationToken cancellationToken);
}

/// <summary>What a save did.</summary>
/// <param name="Saved">True when the file now holds the new values (or nothing needed saving).</param>
/// <param name="Message">One line for the pane.</param>
public sealed record PropertySaveResult(bool Saved, string Message);

/// <summary>
/// In-place editing of title, comments and rating in the details pane (spec §9.2). Holds the values the file had and the values being
/// typed; a save sends only what changed, through the shell's property handler, so a format that cannot take a property (a text file
/// has no rating) says so instead of failing silently. Windows stores a rating as 1, 25, 50, 75 or 99 for one to five stars, and reads
/// any value in a star's band as that star count. No UI types.
/// </summary>
public sealed class PropertyEditor
{
    /// <summary>Property name of the title.</summary>
    public const string TitleKey = "System.Title";

    /// <summary>Property name of the comments.</summary>
    public const string CommentKey = "System.Comment";

    /// <summary>Property name of the rating.</summary>
    public const string RatingKey = "System.Rating";

    private readonly string _title;
    private readonly string _comment;
    private readonly int _stars;

    /// <summary>Creates an editor over the values the shell reported (any may be missing).</summary>
    public PropertyEditor(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _title = TextOf(values, TitleKey);
        _comment = TextOf(values, CommentKey);
        _stars = values.TryGetValue(RatingKey, out var rating) ? StarsOf(rating) : 0;
        Title = _title;
        Comment = _comment;
        Stars = _stars;
    }

    /// <summary>The title as typed.</summary>
    public string Title { get; set; }

    /// <summary>The comments as typed.</summary>
    public string Comment { get; set; }

    /// <summary>The rating as chosen, 0 (none) to 5.</summary>
    public int Stars { get; set; }

    /// <summary>True when something differs from what the file holds.</summary>
    public bool IsDirty => Changes().Count > 0;

    /// <summary>The stars (0 to 5) a stored rating value stands for; Windows reads 1-12 as one star, 13-37 two, 38-62 three, 63-87 four, 88-99 five.</summary>
    public static int StarsOf(object? value)
    {
        if (value is null || !TryNumber(value, out var n) || n <= 0)
        {
            return 0;
        }

        return n switch
        {
            <= 12 => 1,
            <= 37 => 2,
            <= 62 => 3,
            <= 87 => 4,
            _ => 5,
        };
    }

    /// <summary>The value Windows stores for <paramref name="stars"/> stars (1, 25, 50, 75, 99; 0 for none).</summary>
    public static uint StoredRating(int stars) => stars switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 25,
        3 => 50,
        4 => 75,
        _ => 99,
    };

    /// <summary>The properties that differ from what the file holds, with the value to write; an emptied text writes an empty string.</summary>
    public IReadOnlyDictionary<string, object> Changes()
    {
        var changes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.Equals(Title.Trim(), _title, StringComparison.Ordinal))
        {
            changes[TitleKey] = Title.Trim();
        }

        if (!string.Equals(Comment.Trim(), _comment, StringComparison.Ordinal))
        {
            changes[CommentKey] = Comment.Trim();
        }

        if (Math.Clamp(Stars, 0, 5) != _stars)
        {
            changes[RatingKey] = StoredRating(Math.Clamp(Stars, 0, 5));
        }

        return changes;
    }

    /// <summary>Writes the changes to <paramref name="path"/>. Never throws for a file that refuses: the answer says which properties it would not take.</summary>
    public async Task<PropertySaveResult> SaveAsync(string path, IPropertyWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var changes = Changes();
        if (changes.Count == 0)
        {
            return new PropertySaveResult(true, "Nothing changed.");
        }

        try
        {
            await writer.WriteAsync(path, changes, cancellationToken).ConfigureAwait(false);
            return new PropertySaveResult(true, "Saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or NotSupportedException or ArgumentException)
        {
            return new PropertySaveResult(false, $"This file did not take the change: {ex.Message}");
        }
    }

    private static string TextOf(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value switch
        {
            string s => s.Trim(),
            string[] list => string.Join("; ", list.Where(x => !string.IsNullOrWhiteSpace(x))).Trim(),
            _ => string.Empty,
        }
        : string.Empty;

    private static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case uint u:
                number = u;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case ulong ul:
                number = ul;
                return true;
            case double d:
                number = d;
                return true;
            case byte b:
                number = b;
                return true;
            case short s:
                number = s;
                return true;
            case ushort us:
                number = us;
                return true;
            default:
                number = 0;
                return false;
        }
    }
}
