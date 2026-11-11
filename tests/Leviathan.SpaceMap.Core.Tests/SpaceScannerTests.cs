using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;

namespace Leviathan.SpaceMap.Core.Tests;

/// <summary>Scanning a tree into a space map, pausing it, saving it and picking it up again (spec §14).</summary>
public sealed class SpaceScannerTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private sealed class MemoryScans : ISpaceScanStore
    {
        public Dictionary<string, SpaceScanSnapshot> Saved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Saves { get; private set; }

        public void Save(SpaceScanSnapshot snapshot)
        {
            lock (Saved)
            {
                Saved[snapshot.Root] = snapshot;
                Saves++;
            }
        }

        public SpaceScanSnapshot? Load(string root)
        {
            lock (Saved)
            {
                return Saved.GetValueOrDefault(root);
            }
        }

        public void Delete(string root)
        {
            lock (Saved)
            {
                Saved.Remove(root);
            }
        }
    }

    private static FakeLocationProvider Tree(int folders = 20, int filesPerFolder = 10)
    {
        var files = new FakeLocationProvider();
        for (var f = 0; f < folders; f++)
        {
            for (var i = 0; i < filesPerFolder; i++)
            {
                files.AddFile($@"D:\data\f{f}\g{f % 3}\file{i}.bin", 100 + (f * 10) + i);
            }
        }

        files.AddFolder(@"D:\data\empty");
        return files;
    }

    private static long ExpectedBytes(int folders, int filesPerFolder) =>
        Enumerable.Range(0, folders).Sum(f => Enumerable.Range(0, filesPerFolder).Sum(i => (long)(100 + (f * 10) + i)));

    private static SpaceScanner Scanner(FakeLocationProvider files, ISpaceScanStore? store = null, SpaceScanOptions? options = null) =>
        new(new LocationProviderRegistry([files]), store, new ManualTimeProvider(), options);

    [Fact]
    public async Task Scan_BuildsTheTree_WithTheTotalsOfEveryFolder()
    {
        using var session = Scanner(Tree()).Start(P(@"D:\data"));
        await session.Completion;

        Assert.Equal(ExpectedBytes(20, 10), session.Root.Size);
        Assert.Equal(200, session.Root.Files);
        var progress = session.Progress;
        Assert.True(progress.IsComplete);
        Assert.Equal(200, progress.Files);
        Assert.Equal(ExpectedBytes(20, 10), progress.Bytes);
        Assert.Equal(0, progress.FoldersPending);
        Assert.Equal(0, progress.Errors);
        Assert.Equal(1 + 20 + 20 + 1, progress.FoldersListed); // root, f*, g*, empty
        Assert.All(session.Root.Children.Where(c => c.IsFolder), c => Assert.True(c.Listed));
    }

    [Fact]
    public async Task Scan_SortsEveryFolderBySize_SoTheMapAndTheTreeAgree()
    {
        using var session = Scanner(Tree()).Start(P(@"D:\data"));
        await session.Completion;

        var sizes = session.Root.Children.Select(c => c.Size).ToList();
        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
    }

    [Fact]
    public async Task Scan_RootNode_IsNamedByItsPath_AndChildrenKnowTheirFullPaths()
    {
        using var session = Scanner(Tree(2, 2)).Start(P(@"D:\data"));
        await session.Completion;

        Assert.Equal(@"D:\data", session.Root.Name);
        var file = session.Read(r => r.Children.First(c => c.Name == "f0").Children[0].Children[0]);
        Assert.StartsWith(@"D:\data\f0\g0\file", file.FullPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_AFolderThatCannotBeRead_IsMarked_AndTheRestIsScanned()
    {
        var files = new FakeLocationProvider().AddFile(@"D:\data\ok\a.bin", 10).AddFolder(@"D:\data\denied");
        var registry = new LocationProviderRegistry([new DenyingProvider(files, @"D:\data\denied")]);
        using var session = new SpaceScanner(registry, null, new ManualTimeProvider()).Start(P(@"D:\data"));
        await session.Completion;

        Assert.Equal(10, session.Root.Size);
        Assert.Equal(1, session.Progress.Errors);
        Assert.True(session.Root.Children.Single(c => c.Name == "denied").Unreadable);
    }

    private sealed class DenyingProvider(FakeLocationProvider inner, string denied) : ILocationProvider
    {
        public string Name => "denying";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (string.Equals(location.ToString(), denied, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Yield();
                throw new LocationException(LocationError.AccessDenied, location, "Access is denied.");
            }

            await foreach (var batch in inner.ListAsync(location, options, cancellationToken))
            {
                yield return batch;
            }
        }
    }

    [Fact]
    public async Task Scan_CountsCloudPlaceholdersAsNoSpace_AndMarksThem()
    {
        var files = new FakeLocationProvider()
            .AddFile(@"D:\data\real.bin", 1000)
            .AddFile(@"D:\data\cloud.bin", 5_000_000, (FileAttributes)0x400000)
            .AddFile(@"D:\data\offline.bin", 7_000_000, FileAttributes.Offline);
        using var session = Scanner(files).Start(P(@"D:\data"));
        await session.Completion;

        Assert.Equal(1000, session.Root.Size);
        Assert.Equal(1000, session.Progress.Bytes);
        Assert.Equal(3, session.Root.Files);
        Assert.True(session.Root.Children.Single(c => c.Name == "cloud.bin").IsPlaceholder);
        Assert.False(session.Root.Children.Single(c => c.Name == "real.bin").IsPlaceholder);
    }

    [Fact]
    public async Task Scan_ShowsTheTreeWhileItRuns_SoTheMapFillsIn()
    {
        var files = Tree(40, 10);
        using var session = Scanner(files).Start(P(@"D:\data"));
        var growth = new List<long>();
        session.Changed += (_, _) =>
        {
            lock (growth)
            {
                growth.Add(session.Read(r => r.Size));
            }
        };

        await session.Completion;

        lock (growth)
        {
            Assert.True(growth.Count >= 2);
            Assert.Equal(growth.Order(), growth); // only ever grows
        }
    }

    [Fact]
    public async Task Pause_HoldsTheScanBack_AndResumeLetsItFinish()
    {
        var files = Tree(30, 10);
        using var session = Scanner(files, options: new SpaceScanOptions { Parallelism = 2 }).Start(P(@"D:\data"));
        session.Pause();
        await Task.Delay(100);
        var held = session.Progress.FoldersListed;
        await Task.Delay(100);

        Assert.True(session.IsPaused);
        Assert.True(session.Progress.IsPaused);
        Assert.Equal(held, session.Progress.FoldersListed); // nothing moves while paused
        Assert.False(session.Completion.IsCompleted);

        session.Resume();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ExpectedBytes(30, 10), session.Root.Size);
    }

    [Fact]
    public async Task Cancel_KeepsWhatWasFound_AndSavesItForResume()
    {
        var store = new MemoryScans();
        var files = Tree(60, 10);
        using var session = Scanner(files, store, new SpaceScanOptions { Parallelism = 1 }).Start(P(@"D:\data"));
        session.Changed += (_, _) =>
        {
            if (session.Progress.FoldersListed >= 10)
            {
                session.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Completion);

        var saved = store.Load(@"D:\data")!;
        Assert.False(saved.IsComplete);
        Assert.True(saved.Rows.Count > 10);
        Assert.Contains(saved.Rows, r => r.IsFolder && !r.Listed); // folders still to do
    }

    [Fact]
    public async Task Resume_FromAStoppedScan_GivesTheSameMapAsAScanThatWasNeverStopped_AndListsOnlyWhatWasLeft()
    {
        var store = new MemoryScans();
        var files = Tree(60, 10);
        using (var first = Scanner(files, store, new SpaceScanOptions { Parallelism = 1 }).Start(P(@"D:\data")))
        {
            first.Changed += (_, _) =>
            {
                if (first.Progress.FoldersListed >= 25)
                {
                    first.Cancel();
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Completion);
        }

        files.ListCalls.Clear();
        using var second = Scanner(files, store).Start(P(@"D:\data"), store.Load(@"D:\data"));
        await second.Completion;
        using var reference = Scanner(Tree(60, 10)).Start(P(@"D:\data"));
        await reference.Completion;

        Assert.Equal(reference.Root.Size, second.Root.Size);
        Assert.Equal(reference.Root.Files, second.Root.Files);
        Assert.Equal(reference.Progress.FoldersListed, second.Progress.FoldersListed);
        Assert.True(files.ListCalls.Count < reference.Progress.FoldersListed, "the folders already listed were not listed again");
        Assert.True(store.Load(@"D:\data")!.IsComplete);
    }

    [Fact]
    public async Task Resume_AfterTheScannerWasKilled_ContinuesFromTheLastCheckpoint()
    {
        // A killed worker leaves only its last periodic save. Take such a copy half-way, throw the scan away, and continue from the copy.
        var files = Tree(40, 10);
        using var doomed = Scanner(files, null, new SpaceScanOptions { Parallelism = 1 }).Start(P(@"D:\data"));
        SpaceScanSnapshot? checkpoint = null;
        doomed.Changed += (_, _) =>
        {
            if (checkpoint is null && doomed.Progress.FoldersListed >= 30)
            {
                checkpoint = doomed.Snapshot(DateTimeOffset.UnixEpoch, complete: false);
            }
        };
        await doomed.Completion;

        var bytes = SpaceScanCodec.Encode(checkpoint!);
        var restored = SpaceScanCodec.Decode(bytes)!;
        using var resumed = Scanner(Tree(40, 10)).Start(P(@"D:\data"), restored);
        await resumed.Completion;

        Assert.Equal(doomed.Root.Size, resumed.Root.Size);
        Assert.Equal(ExpectedBytes(40, 10), resumed.Root.Size);
        Assert.Equal(doomed.Progress.FoldersListed, resumed.Progress.FoldersListed);
    }

    [Fact]
    public async Task Resume_FromACompleteScan_ListsNothing_SoACachedMapOpensAtOnce()
    {
        var store = new MemoryScans();
        var files = Tree(10, 5);
        using (var first = Scanner(files, store).Start(P(@"D:\data")))
        {
            await first.Completion;
        }

        files.ListCalls.Clear();
        using var again = Scanner(files, store).Start(P(@"D:\data"), store.Load(@"D:\data"));
        await again.Completion;

        Assert.Empty(files.ListCalls);
        Assert.Equal(ExpectedBytes(10, 5), again.Root.Size);
        Assert.True(again.Progress.IsComplete);
    }

    [Fact]
    public async Task Resume_FromASavedScanOfAnotherFolder_StartsOver()
    {
        var files = Tree(3, 3);
        var other = new SpaceScanSnapshot(@"E:\elsewhere", DateTimeOffset.UnixEpoch, true, [new SpaceScanRow(-1, @"E:\elsewhere", true, 0, 0, true, false)]);

        using var session = Scanner(files).Start(P(@"D:\data"), other);
        await session.Completion;

        Assert.Equal(ExpectedBytes(3, 3), session.Root.Size);
    }

    [Fact]
    public async Task Checkpoints_AreSavedWhileItRuns_AndAtTheEnd()
    {
        var store = new MemoryScans();
        var time = new ManualTimeProvider();
        var files = Tree(30, 5);
        using var session = new SpaceScanner(new LocationProviderRegistry([files]), store, time, new SpaceScanOptions { Parallelism = 1, CheckpointEvery = TimeSpan.FromSeconds(10) }).Start(P(@"D:\data"));
        await session.Completion;

        Assert.True(store.Saves >= 1);
        Assert.True(store.Load(@"D:\data")!.IsComplete);
    }

    [Fact]
    public void Codec_RoundTripsASnapshot()
    {
        var snapshot = new SpaceScanSnapshot(
            @"\\nas\media",
            new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.Zero),
            false,
            [
                new SpaceScanRow(-1, @"\\nas\media", true, 0, 0, false, false),
                new SpaceScanRow(0, "Ünïcödé ✓", true, 0, 5, true, false),
                new SpaceScanRow(1, "big.mkv", false, 5_000_000_000, 638_000_000_000_000_000L, false, false),
                new SpaceScanRow(0, "denied", true, 0, 0, true, true),
            ]);

        var back = SpaceScanCodec.Decode(SpaceScanCodec.Encode(snapshot))!;

        Assert.Equal(snapshot.Root, back.Root);
        Assert.Equal(snapshot.ScannedAt, back.ScannedAt);
        Assert.False(back.IsComplete);
        Assert.Equal(snapshot.Rows, back.Rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Codec_DamagedBytes_GiveNullNotAnException(int keep)
    {
        var good = SpaceScanCodec.Encode(new SpaceScanSnapshot(@"C:\x", DateTimeOffset.UnixEpoch, true, [new SpaceScanRow(-1, @"C:\x", true, 0, 0, true, false)]));

        Assert.Null(SpaceScanCodec.Decode(good[..Math.Min(keep, good.Length)]));
        Assert.Null(SpaceScanCodec.Decode(good[..(good.Length / 2)])); // cut short in the middle
        Assert.Null(SpaceScanCodec.Decode([1, 2, 3, 4, 5, 6, 7, 8]));
    }
}
