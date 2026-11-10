using System.Globalization;
using System.Text;

namespace Leviathan.Search;

/// <summary>
/// Writes a <see cref="Query"/> back as text in one canonical form (spec §13.2). Saved searches keep this text, and the text parses back
/// to the same query, so a saved search reads the same after it is opened and saved again.
/// </summary>
public static class QueryFormatter
{
    /// <summary>The canonical text of the understood clauses (errors are not written).</summary>
    public static string Format(Query query)
    {
        var text = new StringBuilder();
        foreach (var clause in query.Clauses)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            if (clause.Negated)
            {
                text.Append('-');
            }

            text.Append(Format(clause.Term));
        }

        return text.ToString();
    }

    /// <summary>The canonical text of one term.</summary>
    public static string Format(QueryTerm term) => term switch
    {
        NameTerm n => n.Quoted ? Quote(n.Text) : Wrap(n.Text),
        RegexTerm r => "regex:" + Wrap(r.Pattern),
        ExtensionFilter e => "ext:" + string.Join(',', e.Extensions),
        SizeFilter s => "size:" + Constrained(s.Size, FormatSize),
        DateFilter d => FormatDate(d),
        TagFilter t => "tag:" + Wrap(t.Name),
        TypeFilter t => "type:" + t.Kind.ToString().ToLowerInvariant(),
        PathFilter p => "path:" + Wrap(p.Text),
        DurationFilter d => "duration:" + Constrained(d.Duration, FormatDuration),
        ResolutionFilter r => "res:" + Constrained(r.Height, h => h.ToString(CultureInfo.InvariantCulture)),
        CodecFilter c => "codec:" + Wrap(c.Codec),
        ContentFilter c => "content:" + Wrap(c.Text),
        _ => string.Empty,
    };

    private static string Quote(string text) => "\"" + text + "\"";

    private static string Wrap(string text) => text.Length == 0 || text.Any(char.IsWhiteSpace) ? Quote(text) : text;

    private static string Operator(Comparison op) => op switch
    {
        Comparison.Greater => ">",
        Comparison.GreaterOrEqual => ">=",
        Comparison.Less => "<",
        Comparison.LessOrEqual => "<=",
        _ => string.Empty,
    };

    private static string Constrained<T>(Constraint<T> constraint, Func<T, string> write)
        where T : IComparable<T> =>
        constraint.Op == Comparison.Between
            ? write(constraint.Value) + ".." + write(constraint.Upper!)
            : Operator(constraint.Op) + write(constraint.Value);

    private static string FormatSize(long bytes)
    {
        (string Unit, int Shift)[] units = [("TB", 40), ("GB", 30), ("MB", 20), ("KB", 10)];
        foreach (var (unit, shift) in units)
        {
            if (bytes != 0 && bytes % (1L << shift) == 0)
            {
                return (bytes >> shift).ToString(CultureInfo.InvariantCulture) + unit;
            }
        }

        return bytes.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var ms = (long)duration.TotalMilliseconds;
        return ms % 1000 == 0 ? (ms / 1000).ToString(CultureInfo.InvariantCulture) + "s" : ms.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    private static string FormatDate(DateFilter filter)
    {
        var name = filter.Field == DateField.Created ? "created:" : "modified:";
        return filter.Op == Comparison.Between
            ? name + FormatOperand(filter.Value) + ".." + FormatOperand(filter.Upper!)
            : name + Operator(filter.Op) + FormatOperand(filter.Value);
    }

    private static string FormatOperand(DateOperand operand) =>
        operand.Keyword is { } keyword
            ? keyword.ToString().ToLowerInvariant()
            : operand.Precision switch
            {
                DatePrecision.Year => operand.Start.ToString("yyyy", CultureInfo.InvariantCulture),
                DatePrecision.Month => operand.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                _ => operand.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
}
