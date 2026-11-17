namespace Leviathan.App;

/// <summary>The window-side ends of a search: where its facts come from and who hears its progress. Set once the app's stores exist.</summary>
internal sealed class SearchHooks
{
    /// <summary>Tags, media information and content for search conditions that need them.</summary>
    public Func<Leviathan.Search.ISearchFacts> Facts { get; set; } = () => Leviathan.Search.NoSearchFacts.Instance;

    /// <summary>Everything's client (the SDK library is optional), created on first use.</summary>
    public Leviathan.Search.IEverythingClient Everything { get; set; } = new Leviathan.Interop.EverythingSdkClient();

    /// <summary>The name index of the folders crawled (the NAS index and local disks without Everything).</summary>
    public Leviathan.Search.NameIndex Index { get; } = new();

    /// <summary>Builds the search for one request: Everything for local drives when it runs, the index for crawled folders, a live listing for the rest (spec 13).</summary>
    public Leviathan.Search.SearchCoordinator Coordinator(Leviathan.Core.Providers.LocationProviderRegistry providers, Leviathan.Search.LiveSearchOptions options) => new(
        new Leviathan.Search.LiveSearch(providers, options),
        Everything,
        new Leviathan.Search.EverythingSearch(Everything, new Leviathan.Search.EverythingSearchOptions { IncludeHidden = options.IncludeHidden, IncludeSystem = options.IncludeSystem, BatchSize = options.BatchSize }),
        Index,
        new Leviathan.Search.NameIndexSearch(Index, new Leviathan.Search.NameIndexSearchOptions { IncludeHidden = options.IncludeHidden, IncludeSystem = options.IncludeSystem, BatchSize = options.BatchSize }));

    /// <summary>Raised on a pool thread after each batch of results and at the end of a search.</summary>
    public Action<Leviathan.Search.SearchReport>? Reported { get; set; }
}
