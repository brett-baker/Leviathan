using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Search;

/// <summary>How a search ended, for the status bar: what was searched and what could not be.</summary>
/// <param name="Request">The search.</param>
/// <param name="Progress">The final counts.</param>
/// <param name="Errors">Folders that could not be read.</param>
/// <param name="Cancelled">True when the search was stopped before it finished.</param>
/// <param name="Finished">True on the report that ends a search that ran to the end.</param>
/// <param name="Sources">Which sources answered which folders; null before the first batch.</param>
public sealed record SearchReport(SearchRequest Request, SearchProgress Progress, IReadOnlyList<SearchFolderError> Errors, bool Cancelled, bool Finished = false, IReadOnlyList<SearchSourceChoice>? Sources = null)
{
    /// <summary>True while the search is still listing folders.</summary>
    public bool IsRunning => !Cancelled && !Finished;
}

/// <summary>
/// Lists the results of a search as a folder (spec §13): the file view shows them like any other listing, in every layout, with columns, tags
/// and actions, because the entries are the real files. Matches stream in as they are found. Progress is reported to <paramref name="progress"/>
/// after every batch and the last report says whether the search finished or was stopped.
/// </summary>
/// <param name="coordinator">Chooses Everything, the index or a live search per folder; made on use because it needs the registry that holds this provider.</param>
/// <param name="facts">Tags, media information and content for the conditions that need them; resolved on use.</param>
/// <param name="time">The clock for date words such as <c>lastweek</c>.</param>
/// <param name="progress">Receives a report after each batch and at the end.</param>
public sealed class SearchListingProvider(Func<LiveSearchOptions, SearchCoordinator> coordinator, Func<ISearchFacts> facts, TimeProvider time, Action<SearchReport>? progress = null) : ILocationProvider
{
    /// <inheritdoc />
    public string Name => "search";

    /// <inheritdoc />
    public bool CanHandle(LocationPath location) => SearchLocations.IsSearchLocation(location);

    /// <inheritdoc />
    public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(options);
        var request = SearchLocations.TryRead(location)
            ?? throw new LocationException(LocationError.NotFound, location, "This search is damaged and cannot be run again. Search again from the search box.");
        var query = QueryParser.Parse(request.QueryText);
        var matcher = new QueryMatcher(query, time);
        var search = coordinator(new LiveSearchOptions { IncludeHidden = request.IncludeHidden || options.IncludeHidden, IncludeSystem = options.IncludeSystem, BatchSize = options.BatchSize });
        var last = new SearchProgress(0, 0, 0, 0);
        var done = false;
        try
        {
            await foreach (var batch in search.RunAsync(query, request.Roots, matcher, matcher.NeedsFacts ? facts() : NoSearchFacts.Instance, cancellationToken).ConfigureAwait(false))
            {
                last = batch.Progress;
                if (batch.IsFinal)
                {
                    done = true;
                    progress?.Invoke(new SearchReport(request, last, batch.Errors, false, Finished: true, Sources: batch.Sources));
                    yield return new ListingBatch(batch.Matches, true);
                }
                else
                {
                    progress?.Invoke(new SearchReport(request, last, [], false, Sources: batch.Sources));
                    yield return new ListingBatch(batch.Matches, false);
                }
            }
        }
        finally
        {
            if (!done)
            {
                progress?.Invoke(new SearchReport(request, last, [], true));
            }
        }
    }
}
