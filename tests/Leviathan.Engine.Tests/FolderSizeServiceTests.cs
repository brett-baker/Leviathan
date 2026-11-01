using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Engine.FolderSizes;
using Leviathan.Testing;

namespace Leviathan.Engine.Tests;

public class FolderSizeServiceTests
{
    private static (FolderSizeService Service, InMemoryFolderSizeStore Store, FakeLocationProvider Fake) Create(Action<FakeLocationProvider> build, Func<FakeLocationProvider, ILocationProvider>? wrap = null)
    {
        var fake = new FakeLocationProvider();
        build(fake);
        var store = new InMemoryFolderSizeStore();
        var provider = wrap?.Invoke(fake) ?? fake;
        return (new FolderSizeService(new LocationProviderRegistry([provider]), store, new ManualTimeProvider()), store, fake);
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task Compute_SumsBottomUp_AndStoresEverySubfolder()
    {
        var (service, store, _) = Create(f => f
            .AddFile(@"\\nas\media\a.mkv", 100)
            .AddFile(@"\\nas\media\inbox\b.mkv", 200)
            .AddFile(@"\\nas\media\inbox\2026-10\c.mkv", 300)
            .AddFile(@"\\nas\media\inbox\2026-10\d.mkv", 400)
            .AddFolder(@"\\nas\media\empty"));

        var root = await service.ComputeAsync(P(@"\\nas\media"), null, CancellationToken.None);

        Assert.Equal(1000, root.TotalBytes);
        Assert.Equal(4, root.TotalFiles);
        Assert.Equal(3, root.TotalFolders);
        Assert.Equal(100, root.OwnBytes);
        Assert.Equal(FolderSizeState.Fresh, root.State);
        Assert.Equal(900, store.Get(P(@"\\nas\media\inbox"))!.TotalBytes);
        Assert.Equal(700, store.Get(P(@"\\nas\media\inbox\2026-10"))!.TotalBytes);
        Assert.Equal(0, store.Get(P(@"\\nas\media\empty"))!.TotalBytes);
    }

    [Fact]
    public async Task Compute_DoesNotFollowLinks()
    {
        var (service, _, fake) = Create(f => f
            .AddFile(@"C:\data\real.bin", 10)
            .AddLink(@"C:\data\loop"));

        var size = await service.ComputeAsync(P(@"C:\data"), null, CancellationToken.None);

        Assert.Equal(10, size.TotalBytes);
        Assert.False(fake.ListCalls.ContainsKey(P(@"C:\data\loop")));
    }

    [Fact]
    public async Task Compute_ReportsPartialLowerBounds_ForTheRoot()
    {
        var (service, _, _) = Create(f => f
            .AddFile(@"C:\r\a\1.bin", 10)
            .AddFile(@"C:\r\b\2.bin", 20)
            .AddFile(@"C:\r\c\3.bin", 30));
        var partials = new List<FolderSize>();

        var final = await service.ComputeAsync(P(@"C:\r"), new SyncProgress(partials.Add), CancellationToken.None);

        Assert.Equal(3, partials.Count);
        Assert.All(partials, p => Assert.Equal(FolderSizeState.Partial, p.State));
        Assert.True(partials.Select(p => p.TotalBytes).SequenceEqual(partials.Select(p => p.TotalBytes).Order()));
        Assert.Equal(60, final.TotalBytes);
    }

    [Fact]
    public async Task Compute_UnreadableSubfolder_IsMarkedError_AndCountingContinues()
    {
        var (service, store, _) = Create(
            f => f.AddFile(@"C:\r\ok\1.bin", 10).AddFile(@"C:\r\locked\2.bin", 20),
            fake => new FaultInjectingLocationProvider(fake).Inject(@"C:\r\locked", new Fault.AccessDenied()));

        var root = await service.ComputeAsync(P(@"C:\r"), null, CancellationToken.None);

        Assert.Equal(FolderSizeState.Error, root.State);
        Assert.True(root.IsLowerBound);
        Assert.Equal(10, root.TotalBytes);
        Assert.Equal(FolderSizeState.Error, store.Get(P(@"C:\r\locked"))!.State);
    }

    [Fact]
    public async Task Compute_NetworkDrop_PropagatesAsTransient()
    {
        var (service, _, _) = Create(
            f => f.AddFile(@"\\nas\media\x\1.bin", 10),
            fake => new FaultInjectingLocationProvider(fake).Inject(@"\\nas\media\x", new Fault.NetworkDrop()));

        var ex = await Assert.ThrowsAsync<LocationException>(() => service.ComputeAsync(P(@"\\nas\media"), null, CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Compute_Cancellation_Stops()
    {
        var (service, _, _) = Create(
            f => f.AddFile(@"\\nas\media\x\1.bin", 10),
            fake => new FaultInjectingLocationProvider(fake).Inject(@"\\nas\media\x", new Fault.Hang()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ComputeAsync(P(@"\\nas\media"), null, cts.Token));
    }

    [Fact]
    public async Task ApplyDelta_UpdatesFolderAndAllAncestors_WithoutRescanning()
    {
        var (service, store, fake) = Create(f => f
            .AddFile(@"\\nas\media\inbox\a.mkv", 100)
            .AddFile(@"\\nas\media\keep\b.mkv", 50));
        await service.ComputeAsync(P(@"\\nas\media"), null, CancellationToken.None);
        var listCallsBefore = fake.ListCalls.Values.Sum();

        // Move a.mkv from inbox to keep: -100 in inbox, +100 in keep.
        service.ApplyDelta(P(@"\\nas\media\inbox"), -100, -1);
        service.ApplyDelta(P(@"\\nas\media\keep"), 100, 1);

        Assert.Equal(0, store.Get(P(@"\\nas\media\inbox"))!.TotalBytes);
        Assert.Equal(150, store.Get(P(@"\\nas\media\keep"))!.TotalBytes);
        Assert.Equal(150, store.Get(P(@"\\nas\media\keep"))!.OwnBytes);
        Assert.Equal(150, store.Get(P(@"\\nas\media"))!.TotalBytes);
        Assert.Equal(listCallsBefore, fake.ListCalls.Values.Sum());
    }

    [Fact]
    public async Task MarkStale_AndIsOutdated()
    {
        var (service, store, _) = Create(f => f.AddFile(@"C:\r\a.bin", 1));
        await service.ComputeAsync(P(@"C:\r"), null, CancellationToken.None);
        var stored = store.Get(P(@"C:\r"))!;

        Assert.False(service.IsOutdated(P(@"C:\r"), stored.FolderModified ?? default) && stored.FolderModified is not null);
        Assert.True(service.IsOutdated(P(@"C:\r"), DateTimeOffset.UnixEpoch.AddDays(1)));

        service.MarkStale(P(@"C:\r"));
        Assert.Equal(FolderSizeState.Stale, store.Get(P(@"C:\r"))!.State);
        Assert.True(service.IsOutdated(P(@"C:\r"), stored.FolderModified ?? default));
        Assert.True(service.IsOutdated(P(@"C:\unknown"), default));
    }

    [Fact]
    public void Store_RemoveTree_RemovesDescendantsOnly()
    {
        var store = new InMemoryFolderSizeStore();
        foreach (var p in new[] { @"C:\a", @"C:\a\b", @"C:\a\b\c", @"C:\ab" })
        {
            store.Upsert(new FolderSize(P(p), 0, 0, 0, 0, 0, FolderSizeState.Fresh, default, null));
        }

        store.RemoveTree(P(@"C:\a\b"));

        Assert.NotNull(store.Get(P(@"C:\a")));
        Assert.Null(store.Get(P(@"C:\a\b")));
        Assert.Null(store.Get(P(@"C:\a\b\c")));
        Assert.NotNull(store.Get(P(@"C:\ab")));
    }

    private sealed class SyncProgress(Action<FolderSize> report) : IProgress<FolderSize>
    {
        public void Report(FolderSize value) => report(value);
    }
}
