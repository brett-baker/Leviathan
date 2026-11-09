using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Leviathan.Core.Model;

namespace Leviathan.ViewModels.Folders;

/// <summary>Per-entry data that comes from outside the file system listing: tags and media duration (Phase 3).</summary>
public interface IEntryMetadata
{
    /// <summary>Tag names of the entry, or null when unknown.</summary>
    IReadOnlySet<string>? GetTags(FileEntry entry);

    /// <summary>Media duration of the entry, or null when unknown or not media.</summary>
    TimeSpan? GetDuration(FileEntry entry);
}

/// <summary>No tags and no durations known. Terms that need them match nothing.</summary>
public sealed class NoEntryMetadata : IEntryMetadata
{
    /// <summary>The shared instance.</summary>
    public static NoEntryMetadata Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlySet<string>? GetTags(FileEntry entry) => null;

    /// <inheritdoc />
    public TimeSpan? GetDuration(FileEntry entry) => null;
}

/// <summary>
/// The filter box syntax (spec §6.3): plain words match names, <c>*.mp4</c> is a wildcard, and
/// <c>size:&gt;1GB</c>, <c>duration:&lt;2m</c>, <c>tag:red</c> and <c>ext:mkv,mp4</c> narrow by property. Terms are ANDed, a
/// leading <c>-</c> excludes, and quotes keep spaces together. It filters the current folder; it never searches below it.
/// </summary>
public sealed class FilterQuery
{
    private readonly List<Term> _terms = [];

    private FilterQuery(string text) => Text = text;

    /// <summary>The empty query: everything matches.</summary>
    public static FilterQuery Empty { get; } = new(string.Empty);

    /// <summary>The text as typed.</summary>
    public string Text { get; }

    /// <summary>Problems found while parsing, e.g. "size:abc is not a size". Bad terms are ignored.</summary>
    public IReadOnlyList<string> Errors => _errors;

    private readonly List<string> _errors = [];

    /// <summary>True when the query has no usable terms.</summary>
    public bool IsEmpty => _terms.Count == 0;

    /// <summary>True when a <c>tag:</c> term is present, so the tags of every file in the folder must be known before the filter is right.</summary>
    public bool UsesTags { get; private set; }

    /// <summary>Parses filter text. Never throws.</summary>
    public static FilterQuery Parse(string? text)
    {
        var query = new FilterQuery(text ?? string.Empty);
        foreach (var raw in Tokenize(query.Text))
        {
            query.AddTerm(raw);
        }

        return query;
    }

    /// <summary>True when <paramref name="entry"/> passes every term.</summary>
    public bool Matches(FileEntry entry, IEntryMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        metadata ??= NoEntryMetadata.Instance;
        foreach (var term in _terms)
        {
            if (term.Matches(entry, metadata) == term.Exclude)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private void AddTerm(string raw)
    {
        var exclude = raw.Length > 1 && raw[0] == '-';
        var body = exclude ? raw[1..] : raw;
        var colon = body.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var key = body[..colon].ToLowerInvariant();
            var value = body[(colon + 1)..];
            switch (key)
            {
                case "size":
                    AddNumeric(raw, exclude, value, ParseSize, "a size such as >1GB", (e, _) => e.Size);
                    return;
                case "duration":
                    AddDuration(raw, exclude, value);
                    return;
                case "tag":
                    UsesTags = true;
                    _terms.Add(new Term(exclude, (e, m) => m.GetTags(e) is { } tags && tags.Any(t => t.Equals(value, StringComparison.OrdinalIgnoreCase))));
                    return;
                case "ext":
                    var extensions = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => "." + x.TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _terms.Add(new Term(exclude, (e, _) => extensions.Contains(e.Extension)));
                    return;
            }
        }

        if (body.Contains('*', StringComparison.Ordinal) || body.Contains('?', StringComparison.Ordinal))
        {
            var pattern = new Regex("^" + Regex.Escape(body).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            _terms.Add(new Term(exclude, (e, _) => pattern.IsMatch(e.Name)));
            return;
        }

        _terms.Add(new Term(exclude, (e, _) => e.Name.Contains(body, StringComparison.OrdinalIgnoreCase)));
    }

    private void AddNumeric(string raw, bool exclude, string value, Func<string, long?> parse, string expected, Func<FileEntry, IEntryMetadata, long> read)
    {
        var (op, number) = SplitOperator(value);
        if (parse(number) is not { } limit)
        {
            _errors.Add($"'{raw}': expected {expected}.");
            return;
        }

        _terms.Add(new Term(exclude, (e, m) => Compare(read(e, m), op, limit)));
    }

    private void AddDuration(string raw, bool exclude, string value)
    {
        var (op, number) = SplitOperator(value);
        if (ParseDuration(number) is not { } limit)
        {
            _errors.Add($"'{raw}': expected a duration such as <2m or >1h30m.");
            return;
        }

        _terms.Add(new Term(exclude, (e, m) => m.GetDuration(e) is { } d && Compare(d.Ticks, op, limit.Ticks)));
    }

    private static (string Op, string Number) SplitOperator(string value)
    {
        foreach (var op in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (value.StartsWith(op, StringComparison.Ordinal))
            {
                return (op, value[op.Length..]);
            }
        }

        return ("=", value);
    }

    private static bool Compare(long actual, string op, long limit) => op switch
    {
        ">" => actual > limit,
        ">=" => actual >= limit,
        "<" => actual < limit,
        "<=" => actual <= limit,
        _ => actual == limit,
    };

    /// <summary>Parses <c>1GB</c>, <c>500 MB</c>, <c>1.5g</c>, <c>200k</c> or a plain byte count. Units are powers of 1024.</summary>
    public static long? ParseSize(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(?<n>\d+(\.\d+)?)\s*(?<u>[kmgt]?)b?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var scale = match.Groups["u"].Value.ToLowerInvariant() switch
        {
            "k" => 1024d,
            "m" => 1024d * 1024,
            "g" => 1024d * 1024 * 1024,
            "t" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)(number * scale);
    }

    /// <summary>Parses <c>90s</c>, <c>2m</c>, <c>1h30m</c>, <c>1:30</c>, <c>1:02:03</c> or a plain number of seconds.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        var value = text.Trim();
        if (value.Contains(':', StringComparison.Ordinal))
        {
            var parts = value.Split(':');
            if (parts.Length is 2 or 3 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                return parts.Length == 2 ? new TimeSpan(0, numbers[0], numbers[1]) : new TimeSpan(numbers[0], numbers[1], numbers[2]);
            }

            return null;
        }

        var match = Regex.Match(value, @"^(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || value.Length == 0)
        {
            return null;
        }

        int Read(string group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(Read("h"), Read("m"), Read("s"));
    }

    private sealed record Term(bool Exclude, Func<FileEntry, IEntryMetadata, bool> Matches);
}
