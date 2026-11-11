using System.Globalization;
using Leviathan.Core.Paths;

namespace Leviathan.Search;

/// <summary>What to ask Everything, and whether that is enough to narrow the search.</summary>
/// <param name="Search">Text in Everything's search syntax.</param>
/// <param name="Narrows">True when the text asks for something beyond "everything under these folders", so Everything is worth asking.</param>
public sealed record EverythingPlan(string Search, bool Narrows);

/// <summary>
/// Turns a <see cref="Query"/> into Everything's syntax (spec §13.1). It only ever asks for a <b>superset</b> of the matches: every condition it
/// writes is one Everything judges at least as loosely as <see cref="QueryMatcher"/> does, and everything else (excluded terms, dates, tags,
/// media, regular expressions, paths) is left out and applied afterwards by the matcher. Everything is thereby only a fast way to narrow, never a
/// second opinion on what matches, which keeps the results the same whichever source answered. Dates are not pushed yet because their syntax
/// could not be checked against a real Everything (see docs/decisions.md).
/// </summary>
public static class EverythingQueryTranslator
{
    /// <summary>Writes the Everything search for <paramref name="query"/> under <paramref name="roots"/>.</summary>
    public static EverythingPlan Translate(Query query, IReadOnlyList<LocationPath> roots)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        var parts = new List<string>();
        var narrows = false;
        if (roots.Count > 0)
        {
            var folders = roots.Select(r => "\"" + (r.ToString().EndsWith('\\') ? r.ToString() : r + "\\") + "\"").ToList();
            parts.Add(folders.Count == 1 ? folders[0] : "<" + string.Join('|', folders) + ">");
        }

        foreach (var clause in query.Clauses.Where(c => !c.Negated))
        {
            var text = Push(clause.Term);
            if (text is not null)
            {
                parts.Add(text);
                narrows = true;
            }
        }

        return new EverythingPlan(string.Join(' ', parts), narrows);
    }

    private static string? Push(QueryTerm term) => term switch
    {
        NameTerm n => PushName(n),
        ExtensionFilter e => "ext:" + string.Join(';', e.Extensions),
        SizeFilter s => PushSize(s.Size),
        TypeFilter { Kind: FileTypeKind.Folder } => "folder:",
        TypeFilter { Kind: FileTypeKind.File } => "file:",
        TypeFilter t => "ext:" + string.Join(';', FileTypes.ExtensionsOf(t.Kind)),
        _ => null,
    };

    // Everything gives a bare word the same meaning as ours (a substring of the name, any case); a term with wildcards is the whole name in both.
    // A term it would read as syntax (quotes, brackets, bars, bangs) is left to the matcher.
    private static string? PushName(NameTerm name)
    {
        if (name.Text.Length == 0 || name.Text.AsSpan().IndexOfAny("\"<>|!") >= 0 || name.Text.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        return name.Quoted || name.Text.Any(char.IsWhiteSpace) ? "\"" + name.Text + "\"" : name.Text;
    }

    private static string? PushSize(Constraint<long> size)
    {
        string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        return size.Op switch
        {
            Comparison.Equal => "size:" + N(size.Value),
            Comparison.Greater => "size:>" + N(size.Value),
            Comparison.GreaterOrEqual => "size:>=" + N(size.Value),
            Comparison.Less => "size:<" + N(size.Value),
            Comparison.LessOrEqual => "size:<=" + N(size.Value),
            Comparison.Between => "size:" + N(size.Value) + ".." + N(size.Upper),
            _ => null,
        };
    }
}
