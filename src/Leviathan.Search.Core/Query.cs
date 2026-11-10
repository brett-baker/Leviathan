namespace Leviathan.Search;

/// <summary>How a numeric or date filter compares (spec §13.1: <c>size:&gt;2GB</c>, <c>duration:&lt;=30m</c>, <c>1MB..2GB</c>).</summary>
public enum Comparison
{
    /// <summary>The value equals the operand (for dates: falls inside the span the operand names).</summary>
    Equal,

    /// <summary>Greater than.</summary>
    Greater,

    /// <summary>Greater than or equal.</summary>
    GreaterOrEqual,

    /// <summary>Less than.</summary>
    Less,

    /// <summary>Less than or equal.</summary>
    LessOrEqual,

    /// <summary>From the operand up to and including the upper operand (<c>a..b</c>).</summary>
    Between,
}

/// <summary>A comparison against one value, or against a range when <see cref="Op"/> is <see cref="Comparison.Between"/>.</summary>
/// <typeparam name="T">The kind of value compared.</typeparam>
/// <param name="Op">How to compare.</param>
/// <param name="Value">The operand (the lower end of a range).</param>
/// <param name="Upper">The upper end of a range; null otherwise.</param>
public sealed record Constraint<T>(Comparison Op, T Value, T? Upper = default)
    where T : IComparable<T>
{
    /// <summary>True when <paramref name="actual"/> satisfies the constraint.</summary>
    public bool Test(T actual) => Op switch
    {
        Comparison.Equal => actual.CompareTo(Value) == 0,
        Comparison.Greater => actual.CompareTo(Value) > 0,
        Comparison.GreaterOrEqual => actual.CompareTo(Value) >= 0,
        Comparison.Less => actual.CompareTo(Value) < 0,
        Comparison.LessOrEqual => actual.CompareTo(Value) <= 0,
        Comparison.Between => actual.CompareTo(Value) >= 0 && Upper is not null && actual.CompareTo(Upper) <= 0,
        _ => false,
    };
}

/// <summary>The broad kinds <c>type:</c> understands (spec §13.1).</summary>
public enum FileTypeKind
{
    /// <summary>Any file (not a folder).</summary>
    File,

    /// <summary>A folder.</summary>
    Folder,

    /// <summary>A video file.</summary>
    Video,

    /// <summary>An audio file.</summary>
    Audio,

    /// <summary>A picture.</summary>
    Image,

    /// <summary>An office, PDF or text document.</summary>
    Document,

    /// <summary>An archive.</summary>
    Archive,

    /// <summary>Source code or markup.</summary>
    Code,
}

/// <summary>Which date a date filter looks at.</summary>
public enum DateField
{
    /// <summary>The modified time (<c>modified:</c>).</summary>
    Modified,

    /// <summary>The created time (<c>created:</c>).</summary>
    Created,
}

/// <summary>How exactly an absolute date was written, which decides how wide the span it names is.</summary>
public enum DatePrecision
{
    /// <summary><c>2025</c>: the whole year.</summary>
    Year,

    /// <summary><c>2025-03</c>: the whole month.</summary>
    Month,

    /// <summary><c>2025-03-14</c>: the whole day.</summary>
    Day,
}

/// <summary>A date word that moves with the clock (<c>lastweek</c>), so a saved search stays current.</summary>
public enum DateKeyword
{
    /// <summary>Today.</summary>
    Today,

    /// <summary>Yesterday.</summary>
    Yesterday,

    /// <summary>The current week (Monday to Sunday).</summary>
    ThisWeek,

    /// <summary>The week before the current one.</summary>
    LastWeek,

    /// <summary>The current month.</summary>
    ThisMonth,

    /// <summary>The month before the current one.</summary>
    LastMonth,

    /// <summary>The current year.</summary>
    ThisYear,

    /// <summary>The year before the current one.</summary>
    LastYear,
}

/// <summary>A date operand: a keyword, or an absolute year, month or day. It names a span, resolved against a clock when matching.</summary>
/// <param name="Keyword">The keyword, or null for an absolute date.</param>
/// <param name="Start">The first day of the span for an absolute date.</param>
/// <param name="Precision">How wide the absolute span is.</param>
public sealed record DateOperand(DateKeyword? Keyword, DateOnly Start, DatePrecision Precision)
{
    /// <summary>An operand that is a keyword.</summary>
    public static DateOperand FromKeyword(DateKeyword keyword) => new(keyword, default, DatePrecision.Day);

    /// <summary>The span the operand names as a half-open range of local dates, relative to <paramref name="today"/>.</summary>
    public (DateOnly Start, DateOnly End) Span(DateOnly today)
    {
        if (Keyword is null)
        {
            return Precision switch
            {
                DatePrecision.Year => (Start, Start.AddYears(1)),
                DatePrecision.Month => (Start, Start.AddMonths(1)),
                _ => (Start, Start.AddDays(1)),
            };
        }

        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var firstOfYear = new DateOnly(today.Year, 1, 1);
        return Keyword.Value switch
        {
            DateKeyword.Today => (today, today.AddDays(1)),
            DateKeyword.Yesterday => (today.AddDays(-1), today),
            DateKeyword.ThisWeek => (monday, monday.AddDays(7)),
            DateKeyword.LastWeek => (monday.AddDays(-7), monday),
            DateKeyword.ThisMonth => (firstOfMonth, firstOfMonth.AddMonths(1)),
            DateKeyword.LastMonth => (firstOfMonth.AddMonths(-1), firstOfMonth),
            DateKeyword.ThisYear => (firstOfYear, firstOfYear.AddYears(1)),
            _ => (firstOfYear.AddYears(-1), firstOfYear),
        };
    }
}

/// <summary>One condition of a query. Conditions in a query are combined with AND (spec §13.1).</summary>
public abstract record QueryTerm;

/// <summary>Plain words match names; <c>*</c> and <c>?</c> are wildcards; a quoted phrase is one term.</summary>
/// <param name="Text">The text as typed (without the quotes).</param>
/// <param name="Quoted">True for a quoted phrase.</param>
public sealed record NameTerm(string Text, bool Quoted) : QueryTerm
{
    /// <summary>True when the text contains <c>*</c> or <c>?</c>.</summary>
    public bool HasWildcards => Text.AsSpan().IndexOfAny('*', '?') >= 0;
}

/// <summary><c>regex:</c> matches the name with a regular expression.</summary>
/// <param name="Pattern">The pattern, checked to compile when parsed.</param>
public sealed record RegexTerm(string Pattern) : QueryTerm;

/// <summary><c>ext:mkv,mp4</c>.</summary>
/// <param name="Extensions">Extensions in lower case, without the dot.</param>
public sealed record ExtensionFilter(IReadOnlyList<string> Extensions) : QueryTerm;

/// <summary><c>size:&gt;2GB</c>.</summary>
/// <param name="Size">The size constraint in bytes.</param>
public sealed record SizeFilter(Constraint<long> Size) : QueryTerm;

/// <summary><c>modified:lastweek</c>, <c>created:2025</c>.</summary>
/// <param name="Field">Which date.</param>
/// <param name="Op">How to compare.</param>
/// <param name="Value">The operand (lower end of a range).</param>
/// <param name="Upper">Upper end of a range; null otherwise.</param>
public sealed record DateFilter(DateField Field, Comparison Op, DateOperand Value, DateOperand? Upper) : QueryTerm;

/// <summary><c>tag:red</c>: the tag name (matched without regard to case).</summary>
/// <param name="Name">The tag name.</param>
public sealed record TagFilter(string Name) : QueryTerm;

/// <summary><c>type:video</c>.</summary>
/// <param name="Kind">The kind.</param>
public sealed record TypeFilter(FileTypeKind Kind) : QueryTerm;

/// <summary><c>path:\\nas\inbox</c>: the folder (or part of the path) the item is under.</summary>
/// <param name="Text">The path text.</param>
public sealed record PathFilter(string Text) : QueryTerm;

/// <summary><c>duration:&gt;30m</c>, from the media metadata cache.</summary>
/// <param name="Duration">The duration constraint.</param>
public sealed record DurationFilter(Constraint<TimeSpan> Duration) : QueryTerm;

/// <summary><c>res:&gt;=2160</c>: the height of the first video stream in pixels.</summary>
/// <param name="Height">The height constraint.</param>
public sealed record ResolutionFilter(Constraint<int> Height) : QueryTerm;

/// <summary><c>codec:hevc</c>: a video or audio codec name.</summary>
/// <param name="Codec">The codec in lower case.</param>
public sealed record CodecFilter(string Codec) : QueryTerm;

/// <summary><c>content:"invoice"</c>: text inside documents.</summary>
/// <param name="Text">The text to find.</param>
public sealed record ContentFilter(string Text) : QueryTerm;

/// <summary>A term, optionally excluded with a leading <c>-</c>.</summary>
/// <param name="Term">The term.</param>
/// <param name="Negated">True to require that the term does not match.</param>
public sealed record QueryClause(QueryTerm Term, bool Negated);

/// <summary>A problem found while parsing, with where it is, so the search box can point at it (spec §13.1).</summary>
/// <param name="Start">Index of the first character of the offending token.</param>
/// <param name="Length">Length of the token.</param>
/// <param name="Message">What is wrong, in words for the user.</param>
public sealed record QueryError(int Start, int Length, string Message);

/// <summary>A parsed search: the clauses that could be understood and the errors for those that could not.</summary>
/// <param name="Source">The text that was parsed.</param>
/// <param name="Clauses">The understood clauses, all required.</param>
/// <param name="Errors">What could not be understood; empty for a valid query.</param>
public sealed record Query(string Source, IReadOnlyList<QueryClause> Clauses, IReadOnlyList<QueryError> Errors)
{
    /// <summary>True when every token was understood.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>True when there is nothing to search for.</summary>
    public bool IsEmpty => Clauses.Count == 0;
}
