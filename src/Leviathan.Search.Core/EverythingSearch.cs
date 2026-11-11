using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;

namespace Leviathan.Search;

/// <summary>Limits for a search answered by Everything.</summary>
public sealed record EverythingSearchOptions
{
    /// <summary>Items fetched from Everything per call (default 2,000): large enough to be quick, small enough to show results while the rest is read.</summary>
    public int PageSize { get; init; } = 2000;

    /// <summary>The most items read from Everything for one search (default 500,000), so a query that narrows nothing cannot run away.</summary>
    public int MaxHits { get; init; } = 500_000;

    /// <summary>The most matches handed over in one batch (default 256).</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>Whether hidden items are included.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Whether system items are included.</summary>
    public bool IncludeSystem { get; init; }
}

/// <summary>
/// Searches the local disks through Everything (spec §13): ask it for a superset with <see cref="EverythingQueryTranslator"/>, then judge every
/// item with the same <see cref="QueryMatcher"/> the live search uses, so both sources agree on what a query means. The output has the shape of
/// <see cref="LiveSearch"/> so the caller treats them alike. Items outside the folders asked for are dropped.
/// </summary>
public sealed class EverythingSearch(IEverythingClient client, EverythingSearchOptions? options = null)
{
    private readonly EverythingSearchOptions _options = options ?? new EverythingSearchOptions();

    /// <summary>True when asking Everything is worthwhile for this query: it can narrow, and every folder is on a local drive.</summary>
    public static bool CanAnswer(Query query, IReadOnlyList<LocationPath> roots)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        return roots.Count > 0 && roots.All(r => r.Kind == LocationKind.Drive) && EverythingQueryTranslator.Translate(query, roots).Narrows;
    }

    /// <summary>Searches under <paramref name="roots"/> and streams the matches.</summary>
    /// <exception cref="InvalidOperationException">Everything is not ready (see <see cref="IEverythingClient.GetStateAsync"/>).</exception>
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
        var plan = EverythingQueryTranslator.Translate(query, roots);
        var examined = 0L;
        var matches = 0;
        var offset = 0;
        var batch = new List<FileEntry>();
        long total;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await client.QueryAsync(plan.Search, offset, _options.PageSize, cancellationToken).ConfigureAwait(false);
            total = Math.Min(page.Total, _options.MaxHits);
            foreach (var hit in page.Hits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                if (ToEntry(hit, roots) is not { } entry || !(_options.IncludeHidden || !entry.IsHidden) || !(_options.IncludeSystem || !entry.IsSystem))
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

            if (page.Hits.Count == 0)
            {
                break;
            }

            offset += page.Hits.Count;
            if (batch.Count > 0)
            {
                yield return new SearchBatch(batch, new SearchProgress(0, examined, matches, 0), false, []);
                batch = [];
            }
        }
        while (offset < total);

        yield return new SearchBatch(batch, new SearchProgress(0, examined, matches, 0), true, []);
    }

    private static FileEntry? ToEntry(EverythingHit hit, IReadOnlyList<LocationPath> roots)
    {
        if (!LocationPath.TryParse(hit.FullPath, out var location) || !roots.Any(r => r != location && r.Contains(location)))
        {
            return null;
        }

        var kind = hit.IsFolder ? FileEntryKind.Folder : FileEntryKind.File;
        return new FileEntry(location.Name, location, kind, hit.IsFolder || hit.Size < 0 ? 0 : hit.Size, hit.Created, hit.Modified, hit.Attributes);
    }
}
