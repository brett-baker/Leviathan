using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Testing;

namespace Leviathan.Core.Tests;

public sealed class ShareHealthWiringTests
{
    private sealed class NoDns : IHostAddressResolver
    {
        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class SameNas : IHostAddressResolver
    {
        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(host is "diskstation" or "nas.example.net" ? ["192.168.1.5"] : []);
    }

    /// <summary>A provider that takes <paramref name="delay"/> of manual time per listing, or throws.</summary>
    private sealed class ScriptedProvider(ManualTimeProvider time, TimeSpan delay, Exception? fail = null) : ILocationProvider
    {
        public int Calls { get; private set; }

        public string Name => "scripted";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            time.Advance(delay);
            if (fail is not null)
            {
                throw fail;
            }

            yield return new ListingBatch([], true);
        }
    }

    private static readonly LocationPath Share = LocationPath.Parse(@"\\diskstation\media");
    private static readonly LocationPath Alias = LocationPath.Parse(@"\\nas.example.net\media");

    private static (HealthTrackingLocationProvider Provider, ShareHealthRegistry Registry, ManualTimeProvider Time, ScriptedProvider Inner) Make(
        TimeSpan delay, Exception? fail = null, IHostAddressResolver? dns = null)
    {
        var time = new ManualTimeProvider();
        var registry = new ShareHealthRegistry(new ServerIdentityMap(dns ?? new NoDns()), time);
        var inner = new ScriptedProvider(time, delay, fail);
        return (new HealthTrackingLocationProvider(inner, registry, time), registry, time, inner);
    }

    private static async Task ListAsync(ILocationProvider provider, LocationPath location)
    {
        await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None))
        {
            _ = batch;
        }
    }

    private static IOException Dropped() => new("The specified network name is no longer available.", unchecked((int)0x80070040));

    [Fact]
    public async Task List_AFastShare_IsOnline_WithItsLatency()
    {
        var (provider, registry, _, _) = Make(TimeSpan.FromMilliseconds(20));

        await ListAsync(provider, Share);

        Assert.Equal(ShareHealth.Online, registry.HealthOf(Share));
        Assert.Equal(TimeSpan.FromMilliseconds(20), registry.Snapshot().Single().Latency);
    }

    [Fact]
    public async Task List_ASlowShare_IsSlow()
    {
        var (provider, registry, _, _) = Make(TimeSpan.FromSeconds(1));

        await ListAsync(provider, Share);

        Assert.Equal(ShareHealth.Slow, registry.HealthOf(Share));
    }

    [Fact]
    public async Task List_TwoDropsInARow_MakeTheShareUnreachable_AndTheThirdCallFailsFastWithoutTouchingTheServer()
    {
        var (provider, registry, _, inner) = Make(TimeSpan.Zero, Dropped());
        for (var i = 0; i < 2; i++)
        {
            var ex = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));
            Assert.Equal(LocationError.Unreachable, ex.Error);
        }

        Assert.Equal(ShareHealth.Unreachable, registry.HealthOf(Share));
        var callsBefore = inner.Calls;

        var fast = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));

        Assert.True(fast.IsTransient);
        Assert.Contains("not responding", fast.Message);
        Assert.Equal(callsBefore, inner.Calls);
    }

    [Fact]
    public async Task List_AnUnreachableShare_IsProbedAgain_AfterTheBackoff_AndRecovers()
    {
        var time = new ManualTimeProvider();
        var registry = new ShareHealthRegistry(new ServerIdentityMap(new NoDns()), time);
        var failing = new ScriptedProvider(time, TimeSpan.Zero, Dropped());
        var provider = new HealthTrackingLocationProvider(failing, registry, time);
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));
        }

        time.Advance(TimeSpan.FromSeconds(5));
        var healthy = new HealthTrackingLocationProvider(new ScriptedProvider(time, TimeSpan.FromMilliseconds(5)), registry, time);
        await ListAsync(healthy, Share);

        Assert.Equal(ShareHealth.Online, registry.HealthOf(Share));
    }

    [Fact]
    public async Task List_LogonFailure_MarksAuthenticationRequired()
    {
        var (provider, registry, _, _) = Make(TimeSpan.Zero, new IOException("logon", unchecked((int)0x8007052E)));

        var ex = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));

        Assert.Equal(LocationError.AuthenticationRequired, ex.Error);
        Assert.Equal(ShareHealth.AuthenticationRequired, registry.HealthOf(Share));
    }

    [Fact]
    public async Task List_AccessDenied_MeansTheServerIsAlive()
    {
        var (provider, registry, _, _) = Make(TimeSpan.FromMilliseconds(10), new UnauthorizedAccessException());

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));

        Assert.Equal(ShareHealth.Online, registry.HealthOf(Share));
    }

    [Fact]
    public async Task List_Cancelled_RecordsNothing()
    {
        var (provider, registry, _, _) = Make(TimeSpan.Zero, new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ListAsync(provider, Share));

        Assert.Equal(ShareHealth.Unknown, registry.HealthOf(Share));
    }

    [Fact]
    public async Task List_ALocalFolder_PassesThroughWithoutTracking()
    {
        var (provider, registry, _, inner) = Make(TimeSpan.Zero);

        await ListAsync(provider, LocationPath.Parse(@"C:\Users"));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task OneNasUnderTwoNames_SharesOneHealth()
    {
        var (provider, registry, _, _) = Make(TimeSpan.Zero, Dropped(), new SameNas());
        // Let the identity lookups for both names finish before the failures are counted.
        registry.For("diskstation");
        registry.For("nas.example.net");
        await Task.Delay(100);

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));
        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Alias));

        Assert.Equal(ShareHealth.Unreachable, registry.HealthOf(Share));
        Assert.Equal(ShareHealth.Unreachable, registry.HealthOf(Alias));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public async Task HealthChanged_IsRaisedOnTransitions_WithTheServerKey()
    {
        var (provider, registry, _, _) = Make(TimeSpan.Zero, Dropped());
        var seen = new List<(string, ShareHealth)>();
        registry.HealthChanged += (_, e) => seen.Add(e);

        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));
        await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));

        Assert.Equal([("diskstation", ShareHealth.Unreachable)], seen);
    }

    [Fact]
    public void HealthOf_ALocalLocation_IsUnknown() =>
        Assert.Equal(ShareHealth.Unknown, new ShareHealthRegistry(new ServerIdentityMap(new NoDns())).HealthOf(LocationPath.Parse(@"C:\")));

    [Fact]
    public void Name_AndCanHandle_FollowTheInnerProvider()
    {
        var (provider, _, _, _) = Make(TimeSpan.Zero);

        Assert.Equal("scripted", provider.Name);
        Assert.True(provider.CanHandle(Share));
    }

    [Fact]
    public async Task FileEntryListing_StillPassesBatchesThrough()
    {
        var fake = new FakeLocationProvider().AddFile(@"\\diskstation\media\a.mkv", 1);
        var registry = new ShareHealthRegistry(new ServerIdentityMap(new NoDns()));
        var provider = new HealthTrackingLocationProvider(fake, registry);
        var names = new List<string>();

        await foreach (var batch in provider.ListAsync(Share, ListingOptions.Default, CancellationToken.None))
        {
            names.AddRange(batch.Items.Select(e => e.Name));
        }

        Assert.Equal(["a.mkv"], names);
        Assert.Equal(ShareHealth.Online, registry.HealthOf(Share));
        _ = FileEntryKind.File;
    }
}
