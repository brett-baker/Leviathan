using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.Search.Core.Tests;

/// <summary>Search results as a folder: the location that holds the request, and the provider that lists it (spec §13).</summary>
public sealed class SearchLocationTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void For_ThenTryRead_RoundTripsTheRequest()
    {
        var request = new SearchRequest("type:video -tag:green \"holiday 2025\"", [P(@"\\nas\media\inbox"), P(@"C:\Users\b\Videos")], IncludeHidden: true);

        var read = SearchLocations.TryRead(SearchLocations.For(request));

        Assert.NotNull(read);
        Assert.Equal(request.QueryText, read.QueryText);
        Assert.Equal(request.Roots, read.Roots);
        Assert.True(read.IncludeHidden);
    }

    [Fact]
    public void For_KeepsUnicodeAndLongQueries()
    {
        var request = new SearchRequest("Ünïcödé ✓ " + new string('x', 400), [P(@"C:\é")]);

        var read = SearchLocations.TryRead(SearchLocations.For(request));

        Assert.Equal(request.QueryText, read!.QueryText);
        Assert.Equal(request.Roots, read.Roots);
    }

    [Fact]
    public void For_FlattensNewlines_AndCapsTheLength()
    {
        var read = SearchLocations.TryRead(SearchLocations.For(new SearchRequest("a\r\nb" + new string('y', 900), [])));

        Assert.StartsWith("a b", read!.QueryText, StringComparison.Ordinal);
        Assert.Equal(500, read.QueryText.Length);
    }

    [Fact]
    public void Locations_DifferingOnlyByCase_AreDifferentLocations()
    {
        // Shell locations compare without regard to case, so the request is written in hex: "Abc" and "abc" must not collapse into one history entry or cache key.
        var upper = SearchLocations.For(new SearchRequest("Abc", [P(@"C:\x")]));
        var lower = SearchLocations.For(new SearchRequest("abc", [P(@"C:\x")]));

        Assert.NotEqual(upper, lower);
    }

    [Fact]
    public void Location_IsRecognised_AndOthersAreNot()
    {
        Assert.True(SearchLocations.IsSearchLocation(SearchLocations.For(new SearchRequest("a", []))));
        Assert.False(SearchLocations.IsSearchLocation(P(@"C:\Videos")));
        Assert.False(SearchLocations.IsSearchLocation(P(@"shell:Downloads")));
        Assert.Null(SearchLocations.TryRead(P(@"C:\Videos")));
    }

    [Theory]
    [InlineData(@"shell:search")]
    [InlineData(@"shell:search\zzzz")]
    [InlineData(@"shell:search\0102")]
    public void TryRead_DamagedLocation_IsNullNotAnException(string path) => Assert.Null(SearchLocations.TryRead(P(path)));

    [Fact]
    public void Titles_NameTheQueryAndWhereItLooks()
    {
        var one = SearchLocations.For(new SearchRequest("holiday", [P(@"C:\Videos")]));
        var two = SearchLocations.For(new SearchRequest("holiday", [P(@"C:\Videos"), P(@"D:\More")]));

        Assert.Equal("Search: holiday", SearchLocations.TitleOf(one));
        Assert.Equal("Search for \"holiday\" in C:\\Videos", SearchLocations.DescribeOf(one));
        Assert.Contains("2 folders", SearchLocations.DescribeOf(two), StringComparison.Ordinal);
        Assert.Equal("Search", SearchLocations.TitleOf(P(@"shell:search\zz")));
    }

    private static async Task<(List<ListingBatch> Batches, List<SearchReport> Reports)> ListAsync(FakeLocationProvider files, SearchRequest request, ISearchFacts? facts = null, CancellationToken token = default)
    {
        var reports = new List<SearchReport>();
        LocationProviderRegistry? registry = null;
        var provider = new SearchListingProvider(o => new SearchCoordinator(new LiveSearch(registry!, o)), () => facts ?? NoSearchFacts.Instance, new ManualTimeProvider(), reports.Add);
        registry = new LocationProviderRegistry([provider, files]);
        var batches = new List<ListingBatch>();
        await foreach (var batch in provider.ListAsync(SearchLocations.For(request), ListingOptions.Default, token))
        {
            batches.Add(batch);
        }

        return (batches, reports);
    }

    [Fact]
    public async Task ListAsync_ReturnsTheMatchingFiles_AndEndsWithAFinalBatch()
    {
        var files = new FakeLocationProvider().AddFile(@"C:\v\a.mkv", 1).AddFile(@"C:\v\sub\b.mkv", 2).AddFile(@"C:\v\c.txt", 3);

        var (batches, reports) = await ListAsync(files, new SearchRequest("ext:mkv", [P(@"C:\v")]));

        Assert.Equal(["a.mkv", "b.mkv"], batches.SelectMany(b => b.Items).Select(i => i.Name).Order());
        Assert.True(batches[^1].IsFinal);
        Assert.Single(batches, b => b.IsFinal);
        var last = reports[^1];
        Assert.False(last.Cancelled);
        Assert.Equal(2, last.Progress.Matches);
    }

    [Fact]
    public async Task ListAsync_ResolvesTagsFromTheFacts()
    {
        var files = new FakeLocationProvider().AddFile(@"C:\v\a.mkv", 1).AddFile(@"C:\v\b.mkv", 2);
        var facts = new TagByName("b.mkv");

        var (batches, _) = await ListAsync(files, new SearchRequest("tag:red", [P(@"C:\v")]), facts);

        Assert.Equal(["b.mkv"], batches.SelectMany(b => b.Items).Select(i => i.Name));
    }

    private sealed class TagByName(string tagged) : ISearchFacts
    {
        public ValueTask<IReadOnlyList<string>> GetTagsAsync(FileEntry entry, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>(entry.Name == tagged ? ["Red"] : []);

        public ValueTask<MediaFacts?> GetMediaAsync(FileEntry entry, CancellationToken cancellationToken) => ValueTask.FromResult<MediaFacts?>(null);

        public ValueTask<bool> ContentContainsAsync(FileEntry entry, string text, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    [Fact]
    public async Task ListAsync_ReportsFoldersThatCouldNotBeRead()
    {
        var files = new FakeLocationProvider().AddFile(@"C:\v\a.mkv", 1);

        var (_, reports) = await ListAsync(files, new SearchRequest("mkv", [P(@"C:\v"), P(@"C:\missing")]));

        var last = reports[^1];
        Assert.Single(last.Errors);
        Assert.Equal(P(@"C:\missing"), last.Errors[0].Folder);
    }

    [Fact]
    public async Task ListAsync_Cancelled_ReportsCancelledAndThrows()
    {
        var files = new FakeLocationProvider();
        for (var i = 0; i < 20; i++)
        {
            files.AddFile($@"C:\v\f{i}\x.mkv", 1);
        }

        using var cts = new CancellationTokenSource();
        var reports = new List<SearchReport>();
        LocationProviderRegistry? registry = null;
        var provider = new SearchListingProvider(o => new SearchCoordinator(new LiveSearch(registry!, o)), () => NoSearchFacts.Instance, new ManualTimeProvider(), reports.Add);
        registry = new LocationProviderRegistry([provider, files]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.ListAsync(SearchLocations.For(new SearchRequest("mkv", [P(@"C:\v")])), ListingOptions.Default, cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.True(reports[^1].Cancelled);
    }

    [Fact]
    public async Task ListAsync_DamagedLocation_IsALocationErrorWithAMessage()
    {
        var provider = new SearchListingProvider(o => new SearchCoordinator(new LiveSearch(new LocationProviderRegistry([]), o)), () => NoSearchFacts.Instance, new ManualTimeProvider());

        var error = await Assert.ThrowsAsync<LocationException>(async () =>
        {
            await foreach (var _ in provider.ListAsync(P(@"shell:search\zz"), ListingOptions.Default, CancellationToken.None))
            {
            }
        });

        Assert.Equal(LocationError.NotFound, error.Error);
    }
}
