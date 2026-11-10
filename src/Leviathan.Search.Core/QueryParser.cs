using System.Globalization;
using System.Text.RegularExpressions;

namespace Leviathan.Search;

/// <summary>
/// Turns what is typed in the search box into a <see cref="Query"/> (spec §13.1). Never throws: whatever cannot be understood becomes a
/// <see cref="QueryError"/> that points at the token, so the box can underline it while the rest of the query still works.
/// </summary>
public static class QueryParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private delegate bool TryOperand<T>(string text, out T value, out string? error);

    /// <summary>Parses <paramref name="text"/>. A null or blank text gives an empty, valid query.</summary>
    public static Query Parse(string? text)
    {
        text ??= string.Empty;
        var clauses = new List<QueryClause>();
        var errors = new List<QueryError>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var token = ReadToken(text, i);
            i = token.End;
            ParseToken(token, clauses, errors);
        }

        return new Query(text, clauses, errors);
    }

    private readonly record struct Token(int Start, int End, bool Negated, string? Field, string Value, bool Quoted, bool Unterminated);

    private static Token ReadToken(string text, int start)
    {
        var i = start;
        var negated = false;
        if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
        {
            negated = true;
            i++;
        }

        string? field = null;
        var f = i;
        while (f < text.Length && char.IsAsciiLetter(text[f]))
        {
            f++;
        }

        if (f > i && f < text.Length && text[f] == ':')
        {
            // A one-letter field is a drive letter and keeps its case; real filter names are not case sensitive.
            field = f - i == 1 ? text[i..f] : text[i..f].ToLowerInvariant();
            i = f + 1;
        }

        var quoted = false;
        var unterminated = false;
        string value;
        if (i < text.Length && text[i] == '"')
        {
            quoted = true;
            var close = text.IndexOf('"', i + 1);
            if (close < 0)
            {
                unterminated = true;
                value = text[(i + 1)..];
                i = text.Length;
            }
            else
            {
                value = text[(i + 1)..close];
                i = close + 1; // Anything glued to the closing quote starts the next token, so a quoted value never holds a quote.
            }
        }
        else
        {
            var from = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            value = text[from..i];
        }

        return new Token(start, i, negated, field, value, quoted, unterminated);
    }

    private static void ParseToken(Token token, List<QueryClause> clauses, List<QueryError> errors)
    {
        var length = token.End - token.Start;
        if (token.Unterminated)
        {
            errors.Add(new QueryError(token.Start, length, "A quote was opened and never closed."));
            return;
        }

        QueryTerm? term;
        string? error;
        if (token.Field is null)
        {
            term = token.Value.Length == 0 ? null : new NameTerm(token.Value, token.Quoted);
            error = null;
        }
        else if (token.Field.Length == 1)
        {
            // A drive letter, as in C:\Videos: not a filter, just text.
            term = new NameTerm(token.Field + ":" + token.Value, token.Quoted);
            error = null;
        }
        else
        {
            term = BuildFilter(token.Field, token.Value, out error);
        }

        if (error is not null)
        {
            errors.Add(new QueryError(token.Start, length, error));
        }
        else if (term is not null)
        {
            clauses.Add(new QueryClause(term, token.Negated));
        }
    }

    private static QueryTerm? BuildFilter(string field, string value, out string? error)
    {
        error = null;
        if (value.Length == 0)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"'{field}:' needs a value.");
            return null;
        }

        switch (field)
        {
            case "ext" or "extension":
                var extensions = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => e.TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (extensions.Count == 0)
                {
                    error = "'ext:' needs at least one extension, such as ext:mkv,mp4.";
                    return null;
                }

                return new ExtensionFilter(extensions);
            case "size":
                return TryConstraint<long>(value, TryParseSize, out var size, out error) ? new SizeFilter(size!) : null;
            case "modified" or "mod":
                return BuildDate(DateField.Modified, value, out error);
            case "created":
                return BuildDate(DateField.Created, value, out error);
            case "tag":
                return new TagFilter(value);
            case "type":
                if (TryParseType(value, out var kind))
                {
                    return new TypeFilter(kind);
                }

                error = $"'{value}' is not a type. Try video, audio, image, document, archive, code, folder or file.";
                return null;
            case "path":
                return new PathFilter(value);
            case "duration" or "len":
                return TryConstraint<TimeSpan>(value, TryParseDuration, out var duration, out error) ? new DurationFilter(duration!) : null;
            case "res" or "resolution":
                return TryConstraint<int>(value, TryParseHeight, out var height, out error) ? new ResolutionFilter(height!) : null;
            case "codec":
                return new CodecFilter(value.ToLowerInvariant());
            case "content":
                return new ContentFilter(value);
            case "regex":
                try
                {
                    _ = new Regex(value, RegexOptions.None, RegexTimeout);
                    return new RegexTerm(value);
                }
                catch (ArgumentException ex)
                {
                    error = "Not a valid regular expression: " + ex.Message;
                    return null;
                }

            default:
                error = $"'{field}:' is not a filter. The filters are ext, size, modified, created, tag, type, path, duration, res, codec, content and regex.";
                return null;
        }
    }

    // ---- comparison operators and ranges ----

    private static Comparison SplitOperator(string value, out string rest)
    {
        foreach (var (prefix, op) in new[] { (">=", Comparison.GreaterOrEqual), ("<=", Comparison.LessOrEqual), (">", Comparison.Greater), ("<", Comparison.Less), ("=", Comparison.Equal) })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                rest = value[prefix.Length..].Trim();
                return op;
            }
        }

        rest = value;
        return Comparison.Equal;
    }

    private static bool TryConstraint<T>(string value, TryOperand<T> parse, out Constraint<T>? result, out string? error)
        where T : struct, IComparable<T>
    {
        result = null;
        error = null;
        var range = value.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (!parse(value[..range].Trim(), out var low, out error) || !parse(value[(range + 2)..].Trim(), out var high, out error))
            {
                return false;
            }

            if (low.CompareTo(high) > 0)
            {
                error = "The range runs backwards: the first value is larger than the second.";
                return false;
            }

            result = new Constraint<T>(Comparison.Between, low, high);
            return true;
        }

        var op = SplitOperator(value, out var rest);
        if (!parse(rest, out var operand, out error))
        {
            return false;
        }

        result = new Constraint<T>(op, operand);
        return true;
    }

    // ---- operands ----

    private static bool TryParseNumber(string text, out decimal number) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) && number >= 0;

    private static bool TryParseSize(string text, out long bytes, out string? error)
    {
        bytes = 0;
        error = null;
        var split = 0;
        while (split < text.Length && (char.IsAsciiDigit(text[split]) || text[split] == '.'))
        {
            split++;
        }

        if (split == 0 || !TryParseNumber(text[..split], out var number))
        {
            error = $"'{text}' is not a size. Try 500KB, 2GB or 1.5MB.";
            return false;
        }

        long unit;
        switch (text[split..].Trim().ToLowerInvariant())
        {
            case "" or "b":
                unit = 1;
                break;
            case "k" or "kb" or "kib":
                unit = 1L << 10;
                break;
            case "m" or "mb" or "mib":
                unit = 1L << 20;
                break;
            case "g" or "gb" or "gib":
                unit = 1L << 30;
                break;
            case "t" or "tb" or "tib":
                unit = 1L << 40;
                break;
            default:
                error = $"'{text[split..]}' is not a size unit. Use B, KB, MB, GB or TB.";
                return false;
        }

        var total = number * unit;
        if (total > long.MaxValue)
        {
            error = $"'{text}' is too large.";
            return false;
        }

        bytes = (long)total;
        return true;
    }

    private static bool TryParseDuration(string text, out TimeSpan duration, out string? error)
    {
        duration = default;
        error = $"'{text}' is not a duration. Try 90s, 30m, 1h30m or 1:30:00.";
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            var parts = text.Split(':');
            if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
            {
                return false;
            }

            var numbers = parts.Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).ToArray();
            if (numbers.Any(n => n < 0 || n > 100_000))
            {
                return false;
            }

            var seconds = parts.Length == 3 ? (numbers[0] * 3600) + (numbers[1] * 60) + numbers[2] : (numbers[0] * 60) + numbers[1];
            duration = TimeSpan.FromSeconds(seconds);
            error = null;
            return true;
        }

        decimal totalMs = 0;
        var i = 0;
        var any = false;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }

            if (i == start || !TryParseNumber(text[start..i], out var number))
            {
                return false;
            }

            var unitStart = i;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            decimal perUnit;
            switch (text[unitStart..i].ToLowerInvariant())
            {
                case "" when !any && i == text.Length:
                case "s" or "sec":
                    perUnit = 1000;
                    break;
                case "ms":
                    perUnit = 1;
                    break;
                case "m" or "min":
                    perUnit = 60_000;
                    break;
                case "h" or "hr":
                    perUnit = 3_600_000;
                    break;
                case "d":
                    perUnit = 86_400_000;
                    break;
                default:
                    return false;
            }

            totalMs += number * perUnit;
            any = true;
            if (totalMs > 3_000_000_000_000m)
            {
                return false;
            }
        }

        duration = TimeSpan.FromMilliseconds((double)Math.Round(totalMs));
        error = null;
        return any;
    }

    private static bool TryParseHeight(string text, out int height, out string? error)
    {
        height = 0;
        error = $"'{text}' is not a resolution. Use the height in pixels (2160), 1080p, or 4k.";
        var lower = text.ToLowerInvariant();
        switch (lower)
        {
            case "8k":
                height = 4320;
                break;
            case "4k" or "uhd":
                height = 2160;
                break;
            case "2k" or "qhd":
                height = 1440;
                break;
            default:
                var digits = lower.EndsWith('p') ? lower[..^1] : lower;
                if (digits.Length == 0 || digits.Length > 5 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out height))
                {
                    return false;
                }

                break;
        }

        error = null;
        return true;
    }

    private static bool TryParseType(string text, out FileTypeKind kind)
    {
        var parsed = text.ToLowerInvariant() switch
        {
            "video" or "videos" or "movie" or "movies" => FileTypeKind.Video,
            "audio" or "music" or "sound" => FileTypeKind.Audio,
            "image" or "images" or "picture" or "pictures" or "photo" or "photos" => FileTypeKind.Image,
            "document" or "documents" or "doc" or "docs" => FileTypeKind.Document,
            "archive" or "archives" or "zip" => FileTypeKind.Archive,
            "code" or "source" => FileTypeKind.Code,
            "folder" or "folders" or "dir" or "directory" => FileTypeKind.Folder,
            "file" or "files" => FileTypeKind.File,
            _ => (FileTypeKind?)null,
        };
        kind = parsed.GetValueOrDefault();
        return parsed is not null;
    }

    // ---- dates ----

    private static DateFilter? BuildDate(DateField field, string value, out string? error)
    {
        error = null;
        var range = value.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (!TryParseDate(value[..range].Trim(), out var low, out error) || !TryParseDate(value[(range + 2)..].Trim(), out var high, out error))
            {
                return null;
            }

            return new DateFilter(field, Comparison.Between, low!, high);
        }

        var op = SplitOperator(value, out var rest);
        if (!TryParseDate(rest, out var operand, out error))
        {
            return null;
        }

        return new DateFilter(field, op, operand!, null);
    }

    private static bool TryParseDate(string text, out DateOperand? operand, out string? error)
    {
        operand = null;
        error = $"'{text}' is not a date. Try today, lastweek, 2025, 2025-03 or 2025-03-14.";
        var keyword = text.ToLowerInvariant() switch
        {
            "today" => DateKeyword.Today,
            "yesterday" => DateKeyword.Yesterday,
            "thisweek" => DateKeyword.ThisWeek,
            "lastweek" => DateKeyword.LastWeek,
            "thismonth" => DateKeyword.ThisMonth,
            "lastmonth" => DateKeyword.LastMonth,
            "thisyear" => DateKeyword.ThisYear,
            "lastyear" => DateKeyword.LastYear,
            _ => (DateKeyword?)null,
        };
        if (keyword is not null)
        {
            operand = DateOperand.FromKeyword(keyword.Value);
            error = null;
            return true;
        }

        var parts = text.Split('-');
        if (parts.Length is < 1 or > 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)) || parts[0].Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 1 || year > 9998)
        {
            return false;
        }

        var month = 1;
        var day = 1;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out month) || month is < 1 or > 12))
        {
            return false;
        }

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out day) || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                return false;
            }
        }

        var precision = parts.Length switch { 1 => DatePrecision.Year, 2 => DatePrecision.Month, _ => DatePrecision.Day };
        operand = new DateOperand(null, new DateOnly(year, month, day), precision);
        error = null;
        return true;
    }
}
