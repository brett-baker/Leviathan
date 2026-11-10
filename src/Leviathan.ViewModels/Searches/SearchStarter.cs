using Leviathan.Core.Paths;
using Leviathan.Search;

namespace Leviathan.ViewModels.Searches;

/// <summary>The outcome of asking for a search: where to go, or why not.</summary>
/// <param name="Results">The folder that will hold the results; null when the search cannot start.</param>
/// <param name="Message">Why it cannot start, in words for the user; null when it can.</param>
public sealed record SearchPlan(LocationPath? Results, string? Message);

/// <summary>
/// Decides what a search typed in the search box means (spec §13): what is wrong with the text, and which folders it looks in. Searching from a
/// results tab looks in the same folders again, so the box refines the search instead of searching the search.
/// </summary>
public static class SearchStarter
{
    /// <summary>Plans a search of <paramref name="text"/> started while <paramref name="current"/> is shown.</summary>
    public static SearchPlan Plan(string? text, LocationPath current, bool includeHidden)
    {
        ArgumentNullException.ThrowIfNull(current);
        var query = QueryParser.Parse(text);
        if (query.IsEmpty && query.IsValid)
        {
            return new SearchPlan(null, "Type something to search for, such as holiday, *.mkv or type:video size:>1GB.");
        }

        if (!query.IsValid)
        {
            var first = query.Errors[0];
            return new SearchPlan(null, $"{first.Message} (in \"{query.Source.Substring(first.Start, first.Length)}\")");
        }

        if (query.Clauses.Any(c => c.Term is ContentFilter))
        {
            return new SearchPlan(null, "content: is not available yet. Search by name, type, size, date, tag or media fields for now.");
        }

        IReadOnlyList<LocationPath> roots;
        if (SearchLocations.TryRead(current) is { } before)
        {
            roots = before.Roots;
        }
        else if (current.Kind == LocationKind.Shell)
        {
            return new SearchPlan(null, "Open a folder first: search looks in the folder you are in and everything below it.");
        }
        else
        {
            roots = [current];
        }

        return new SearchPlan(SearchLocations.For(new SearchRequest(query.Source.Trim(), roots, includeHidden)), null);
    }
}
