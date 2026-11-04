using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// A folder of tens of thousands of files must not stall the window (spec §3.2): rows reach the UI in a few updates a second and
/// in chunks small enough to keep every callback short, and nothing sorts all of them again when only the folder type was found.
/// </summary>
public sealed class LargeFolderTests
{
    /// <summary>A listing the test feeds by hand: batches go out when the test writes them, the listing ends when it completes.</summary>
    private sealed class ScriptedProvider : ILocationProvider
    {
        private readonly Channel<ListingBatch> _batches = Channel.CreateUnbounded<ListingBatch>();

        public string Name => "scripted";

        public bool CanHandle(LocationPath location) => true;

        public void Send(IEnumerable<FileEntry> items) => _batches.Writer.TryWrite(new ListingBatch([.. items], false));

        public void Complete() => _batches.Writer.TryComplete();

        public void Fail(Exception exception) => _batches.Writer.TryComplete(exception);

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var batch in _batches.Reader.ReadAllAsync(cancellationToken))
            {
                yield return batch;
            }
        }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static List<FileEntry> Files(int from, int count) =>
        [.. Enumerable.Range(from, count).Select(i => new FileEntry($"f{i:D6}.txt", P($@"C:\big\f{i:D6}.txt"), FileEntryKind.File, 1, Stamp, Stamp, FileAttributes.Normal))];

    private static (FolderViewModel Folder, ScriptedProvider Provider, ManualTimeProvider Time) Make()
    {
        var provider = new ScriptedProvider();
        var time = new ManualTimeProvider();
        return (new FolderViewModel(new LocationProviderRegistry([provider]), new InlineUiContext(), time), provider, time);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(5000, 40)]
    [InlineData(10000, 40)]
    [InlineData(50000, 200)]
    [InlineData(100000, 400)]
    [InlineData(5000000, 400)]
    public void TheUpdateInterval_GrowsWithTheFolder_BetweenTheLimits(int rows, double milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), FolderViewModel.FlushIntervalFor(rows));

    [Fact]
    public async Task TheFirstBatch_ReachesTheListAtOnce_WithoutWaitingForTheInterval()
    {
        var (folder, provider, _) = Make();
        var load = folder.LoadAsync(P(@"C:\big"), CancellationToken.None);

        provider.Send(Files(0, 100));
        await UntilAsync(() => folder.Entries.Count == 100);

        Assert.Equal(FolderLoadState.Loading, folder.State);
        provider.Complete();
        await load;
        folder.Dispose();
    }

    [Fact]
    public async Task LaterBatches_AreHeldBackUntilTheIntervalPasses_ThenArriveTogether()
    {
        var (folder, provider, time) = Make();
        var adds = new List<int>();
        folder.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                adds.Add(e.NewItems!.Count);
            }
        };
        var load = folder.LoadAsync(P(@"C:\big"), CancellationToken.None);
        provider.Send(Files(0, 100));
        await UntilAsync(() => folder.Entries.Count == 100);

        provider.Send(Files(100, 100));
        provider.Send(Files(200, 100));
        provider.Send(Files(300, 100));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Nothing more yet: the interval has not passed on the folder's clock.
        Assert.Equal(100, folder.Entries.Count);
        time.Advance(FolderViewModel.MinFlushInterval);
        await UntilAsync(() => folder.Entries.Count == 400);

        Assert.Equal([100, 300], adds);
        provider.Complete();
        await load;
        folder.Dispose();
    }

    [Fact]
    public async Task ABigBatch_ReachesTheListInChunks_AndTheFolderIsLoadedOnlyWhenTheLastChunkIsIn()
    {
        var (folder, provider, _) = Make();
        var adds = new List<int>();
        var countWhenLoaded = -1;
        folder.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                adds.Add(e.NewItems!.Count);
            }
        };
        folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderViewModel.State) && folder.State == FolderLoadState.Loaded)
            {
                countWhenLoaded = folder.Entries.Count;
            }
        };

        provider.Send(Files(0, 5000));
        provider.Complete();
        await folder.LoadAsync(P(@"C:\big"), CancellationToken.None);

        Assert.Equal(5000, folder.Entries.Count);
        Assert.All(adds, n => Assert.True(n <= FolderViewModel.MaxRowsPerUpdate));
        Assert.Equal(5000, adds.Sum());
        Assert.True(adds.Count >= 3);
        Assert.Equal(5000, countWhenLoaded);
        folder.Dispose();
    }

    [Fact]
    public async Task EveryRowArrivesOnce_InOrder_WhateverTheChunking()
    {
        var (folder, provider, time) = Make();
        var load = folder.LoadAsync(P(@"C:\big"), CancellationToken.None);
        for (var i = 0; i < 12; i++)
        {
            provider.Send(Files(i * 1000, 1000));
            time.Advance(TimeSpan.FromMilliseconds(30));
        }

        provider.Complete();
        await load;

        Assert.Equal(12000, folder.Entries.Count);
        Assert.Equal(Enumerable.Range(0, 12000).Select(i => $"f{i:D6}.txt"), folder.Entries.Select(e => e.Name));
        folder.Dispose();
    }

    [Fact]
    public async Task AFailureAfterSomeRows_KeepsTheRows_AndShowsTheMessageOnlyAfterThem()
    {
        var (folder, provider, _) = Make();
        var rowsWhenFailed = -1;
        folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderViewModel.State) && folder.State == FolderLoadState.Offline)
            {
                rowsWhenFailed = folder.Entries.Count;
            }
        };
        provider.Send(Files(0, 3000));
        provider.Fail(new LocationException(LocationError.Unreachable, P(@"C:\big"), "The share stopped answering."));

        await folder.LoadAsync(P(@"C:\big"), CancellationToken.None);

        Assert.Equal(FolderLoadState.Offline, folder.State);
        Assert.Equal(3000, rowsWhenFailed);
        folder.Dispose();
    }

    [Fact]
    public async Task ANewLoad_DropsWhatTheOldOneStillHeldBack()
    {
        var (folder, provider, time) = Make();
        var first = folder.LoadAsync(P(@"C:\big"), CancellationToken.None);
        provider.Send(Files(0, 100));
        await UntilAsync(() => folder.Entries.Count == 100);
        provider.Send(Files(100, 100));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        folder.Cancel();
        time.Advance(TimeSpan.FromSeconds(5));
        provider.Complete();
        await first;

        // The held-back rows were never shown: a cancelled listing does not leak into the folder.
        Assert.Equal(100, folder.Entries.Count);
        Assert.Equal(FolderLoadState.Cancelled, folder.State);
        folder.Dispose();
    }

    [Fact]
    public void ApplyingTheSameView_DoesNotSortAgain_ButAChangedViewDoes()
    {
        var entries = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(entries);
        entries.AddRange(Files(0, 2000));
        var changes = 0;
        view.Rows.CollectionChanged += (_, _) => changes++;

        view.Apply(FolderViewSettings.Default);
        var afterSame = changes;
        view.Apply(FolderViewSettings.Default with { Sort = [new SortKey("name", SortDirection.Descending)] });
        var afterChanged = changes;

        Assert.Equal(0, afterSame);
        Assert.True(afterChanged > 0);
        Assert.Equal("f001999.txt", view.Rows[0].Name);
    }

    // Names in a scrambled order, so the first sort has real work to do.
    private static FileEntry Scattered(int i)
    {
        var name = $"f{(i * 7919) % 40_000:D5}.txt";
        return new FileEntry(name, P(@"C:\big\" + name), FileEntryKind.File, 1, Stamp, Stamp, FileAttributes.Normal);
    }

    [Fact]
    public async Task RemoveGone_TakesOnlyThoseRowsOut_AndKeepsTheRestInOrder()
    {
        // A running delete removes rows from the list while it works, without listing the folder again (so the view keeps its place).
        var (folder, provider, _) = Make();
        provider.Send(Files(0, 100));
        provider.Complete();
        await folder.LoadAsync(P(@"C:\big"), CancellationToken.None);

        folder.RemoveGone([P(@"C:\big\f000003.txt"), P(@"C:\big\f000050.txt"), P(@"C:\big\elsewhere.txt")]);

        Assert.Equal(98, folder.Entries.Count);
        Assert.DoesNotContain(folder.Entries, e => e.Name is "f000003.txt" or "f000050.txt");
        Assert.Equal("f000002.txt", folder.Entries[2].Name);
        Assert.Equal("f000004.txt", folder.Entries[3].Name);
        folder.Dispose();
    }

    [Fact]
    public async Task RemoveGone_RaisesOneChange_NotOnePerRow()
    {
        var (folder, provider, _) = Make();
        provider.Send(Files(0, 1000));
        provider.Complete();
        await folder.LoadAsync(P(@"C:\big"), CancellationToken.None);
        var changes = 0;
        folder.Entries.CollectionChanged += (_, _) => changes++;

        folder.RemoveGone([.. Enumerable.Range(0, 500).Select(i => P($@"C:\big\f{i:D6}.txt"))]);

        Assert.Equal(500, folder.Entries.Count);
        Assert.True(changes <= 2, $"{changes} change notifications");
        folder.Dispose();
    }

    [Fact]
    public void ChangingTheFilter_OfAnAlreadySortedFolder_DoesNotSortAgain()
    {
        // Showing or hiding items and typing in the filter box used to sort every row afresh on the UI thread (300 to 500 ms for
        // 40,000 rows, the 572 ms stall of "show hidden"). The sorted list is kept, so a filter change is one pass over it.
        var source = new RangeObservableCollection<FileEntry>();
        source.AddRange([.. Enumerable.Range(0, 40_000).Select(i => Scattered(i))]);
        var view = new FolderItemsView(source);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        view.SetSort([new SortKey("name", SortDirection.Descending)]);
        var fullSort = sw.Elapsed;

        sw.Restart();
        view.SetFilter("f0");
        view.SetFilter(null);
        var twoFilterChanges = sw.Elapsed;

        Assert.Equal(40_000, view.Rows.Count);
        Assert.Equal("f39999.txt", view.Rows[0].Name);
        Assert.True(twoFilterChanges < fullSort / 2, $"two filter changes took {twoFilterChanges.TotalMilliseconds:F0} ms, a full sort {fullSort.TotalMilliseconds:F0} ms");
    }
}
