using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;

namespace Leviathan.Core.Tests;

public class BatchingTests
{
    private static readonly LocationPath Folder = LocationPath.Parse(@"C:\data");

    private static IEnumerable<FileEntry> Entries(int count, Action<int>? beforeEach = null)
    {
        for (var i = 0; i < count; i++)
        {
            beforeEach?.Invoke(i);
            yield return new FileEntry($"f{i}", Folder.Combine($"f{i}"), FileEntryKind.File, i, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task StreamAsync_SplitsIntoBatches_AndMarksLastAsFinal()
    {
        var options = ListingOptions.Default with { BatchSize = 100, MaxBatchDelay = TimeSpan.FromSeconds(10) };
        var batches = new List<ListingBatch>();

        await foreach (var batch in Batching.StreamAsync(_ => Entries(250), Folder, options, TimeProvider.System, CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(250, batches.Sum(b => b.Items.Count));
        Assert.True(batches[^1].IsFinal);
        Assert.All(batches.Take(batches.Count - 1), b => Assert.False(b.IsFinal));
        Assert.All(batches, b => Assert.True(b.Items.Count <= 100));
    }

    [Fact]
    public async Task StreamAsync_EmptyFolder_YieldsOneFinalEmptyBatch()
    {
        var batches = new List<ListingBatch>();

        await foreach (var batch in Batching.StreamAsync(_ => [], Folder, ListingOptions.Default, TimeProvider.System, CancellationToken.None))
        {
            batches.Add(batch);
        }

        var only = Assert.Single(batches);
        Assert.True(only.IsFinal);
        Assert.Empty(only.Items);
    }

    [Fact]
    public async Task StreamAsync_SlowProducer_FlushesPartialBatchesEarly()
    {
        var options = ListingOptions.Default with { BatchSize = 1000, MaxBatchDelay = TimeSpan.FromMilliseconds(20) };
        var batches = new List<ListingBatch>();

        await foreach (var batch in Batching.StreamAsync(_ => Entries(6, i => Thread.Sleep(i == 3 ? 200 : 0)), Folder, options, TimeProvider.System, CancellationToken.None))
        {
            batches.Add(batch);
        }

        // The pause in the middle forces at least one partial batch before the final one.
        Assert.True(batches.Count >= 2, $"Expected a partial batch, got {batches.Count} batch(es).");
        Assert.Equal(6, batches.Sum(b => b.Items.Count));
    }

    [Fact]
    public async Task StreamAsync_FiltersHiddenItems_ByDefault()
    {
        IEnumerable<FileEntry> Produce(CancellationToken _)
        {
            yield return new FileEntry("visible", Folder.Combine("visible"), FileEntryKind.File, 1, default, default, FileAttributes.Normal);
            yield return new FileEntry("hidden", Folder.Combine("hidden"), FileEntryKind.File, 1, default, default, FileAttributes.Hidden);
        }

        var names = new List<string>();
        await foreach (var batch in Batching.StreamAsync(Produce, Folder, ListingOptions.Default, TimeProvider.System, CancellationToken.None))
        {
            names.AddRange(batch.Items.Select(i => i.Name));
        }

        Assert.Equal(new[] { "visible" }, names);
    }

    [Fact]
    public async Task StreamAsync_ProducerFailure_SurfacesAsLocationException()
    {
        IEnumerable<FileEntry> Produce(CancellationToken _)
        {
            yield return Entries(1).First();
            throw new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));
        }

        var ex = await Assert.ThrowsAsync<LocationException>(async () =>
        {
            await foreach (var _ in Batching.StreamAsync(Produce, Folder, ListingOptions.Default, TimeProvider.System, CancellationToken.None))
            {
            }
        });

        Assert.Equal(LocationError.Unreachable, ex.Error);
    }

    [Fact]
    public async Task StreamAsync_Cancellation_DoesNotWaitForAStuckProducer()
    {
        using var cts = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();

        IEnumerable<FileEntry> Stuck(CancellationToken _)
        {
            yield return Entries(1).First();
            release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); // simulates a blocking OS call on a dead share
            yield break;
        }

        var options = ListingOptions.Default with { MaxBatchDelay = TimeSpan.FromMilliseconds(10) };
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Batching.StreamAsync(Stuck, Folder, options, TimeProvider.System, cts.Token))
            {
                cts.Cancel();
            }
        });

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "Cancellation waited for the stuck producer.");
        release.Set();
    }
}
