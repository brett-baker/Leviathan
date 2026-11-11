using System.Runtime.CompilerServices;
using Leviathan.Core.Paths;

namespace Leviathan.Search;

/// <summary>Limits for a search of the <see cref="NameIndex"/>.</summary>
public sealed record NameIndexSearchOptions
{
    /// <summary>The most matches handed over in one batch (default 256).</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>The most candidates taken from the index for one search (default 1,000,000).</summary>
    public int MaxCandidates { get; init; } = 1_000_000;

    /// <summary>Whether hidden items are included.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Whether system items are included.</summary>
    public bool IncludeSystem { get; init; }
}

/// <summary>
/// Searches the <see cref="NameIndex"/> (spec §13: "instant once built"). The index scan uses only what it can decide from a name, a kind and a size
/// (a superset of the answer); the surviving items are then judged by the same <see cref="QueryMatcher"/> as every other source, so an index, Everything
/// and a live search agree on what a query means. Results come from the index as it was last crawled, so an item deleted since is still listed until the
/// next crawl.
/// </summary>
public sealed class NameIndexSearch(NameIndex index, NameIndexSearchOptions? options = null)
{
    private const int FirstPage = 256;
    private const int Page = 4096;

    private readonly NameIndexSearchOptions _options = options ?? new NameIndexSearchOptions();

    /// <summary>Searches under <paramref name="roots"/> (the whole index when empty) and streams the matches.</summary>
    public async IAsyncEnumerable<SearchBatch> RunAsync(
        Query query,
        IReadOnlyList<LocationPath> roots,
        QueryMatcher matcher,
        ISearchFacts facts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(facts);
        var literal = LongestLiteral(query);
        var keep = KeepFor(query);
        var batch = new List<Leviathan.Core.Model.FileEntry>();
        var examined = 0L;
        var matches = 0;
        var cursor = new IndexCursor();
        var taken = 0;
        while (!cursor.IsDone && taken < _options.MaxCandidates)
        {
            // The first page is small so the first results show at once; the scan runs on a pool thread, never the caller's.
            var current = cursor;
            var size = Math.Min(taken == 0 ? FirstPage : Page, _options.MaxCandidates - taken);
            var (page, next) = await Task.Run(() =>
            {
                var c = current;
                var items = index.CandidatePage(literal, roots, keep, ref c, size, cancellationToken);
                return (items, c);
            }, cancellationToken).ConfigureAwait(false);
            cursor = next;
            taken += page.Count;
            foreach (var entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                if (!(_options.IncludeHidden || !entry.IsHidden) || !(_options.IncludeSystem || !entry.IsSystem))
                {
                    continue;
                }

                if (await matcher.MatchesAsync(entry, facts, cancellationToken).ConfigureAwait(false))
                {
                    matches++;
                    batch.Add(entry);
                    if (batch.Count >= _options.BatchSize)
                    {
                        yield return new SearchBatch(batch, new SearchProgress(0, examined, matches, 0), false, []);
                        batch = [];
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return new SearchBatch(batch, new SearchProgress(0, examined, matches, 0), false, []);
                batch = [];
            }
        }

        yield return new SearchBatch(batch, new SearchProgress(0, examined, matches, 0), true, []);
    }

    /// <summary>The longest run of plain characters in a required word, which a name must contain for the word to match; null when no word gives one.</summary>
    internal static string? LongestLiteral(Query query)
    {
        string? best = null;
        foreach (var clause in query.Clauses.Where(c => !c.Negated && c.Term is NameTerm))
        {
            foreach (var chunk in ((NameTerm)clause.Term).Text.Split(['*', '?'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (best is null || chunk.Length > best.Length)
                {
                    best = chunk;
                }
            }
        }

        return best;
    }

    // What can be decided from a name, a kind and a size without building the path. Each test only drops items the full matcher would drop too.
    internal static Func<IndexedChild, bool>? KeepFor(Query query)
    {
        var tests = new List<Func<IndexedChild, bool>>();
        foreach (var clause in query.Clauses.Where(c => !c.Negated))
        {
            switch (clause.Term)
            {
                case ExtensionFilter ext:
                    var set = new HashSet<string>(ext.Extensions, StringComparer.OrdinalIgnoreCase);
                    tests.Add(c => !c.IsFolder && set.Contains(ExtensionOf(c.Name)));
                    break;
                case SizeFilter size:
                    tests.Add(c => !c.IsFolder && size.Size.Test(c.Size));
                    break;
                case TypeFilter type:
                    tests.Add(c => type.Kind switch
                    {
                        FileTypeKind.Folder => c.IsFolder,
                        FileTypeKind.File => !c.IsFolder,
                        _ => !c.IsFolder && FileTypes.IsKind(ExtensionOf(c.Name), type.Kind),
                    });
                    break;
                case NameTerm name when !name.HasWildcards:
                    var text = name.Text;
                    tests.Add(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }

        return tests.Count == 0 ? null : c => tests.TrueForAll(t => t(c));
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[(dot + 1)..] : string.Empty;
    }
}
