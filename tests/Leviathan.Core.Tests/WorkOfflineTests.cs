using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Testing;

namespace Leviathan.Core.Tests;

/// <summary>"Work offline" (spec §3.3): a server chosen to be worked with offline is not asked at all, and its health stays as it was.</summary>
public sealed class WorkOfflineTests
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

    private sealed class CountingProvider : ILocationProvider
    {
        public int Calls { get; private set; }

        public string Name => "counting";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            yield return new ListingBatch([], true);
        }
    }

    private static readonly LocationPath Share = LocationPath.Parse(@"\\diskstation\media\shows");
    private static readonly LocationPath Alias = LocationPath.Parse(@"\\nas.example.net\backup");

    private static async Task ListAsync(HealthTrackingLocationProvider provider, LocationPath location)
    {
        await foreach (var batch in provider.ListAsync(location, ListingOptions.Default, CancellationToken.None))
        {
            _ = batch;
        }
    }

    [Fact]
    public void Set_TurnsAServerOfflineAndOnline_AndSaysWhenNothingChanged()
    {
        var offline = new WorkOfflineRegistry();
        var changes = new List<(string, bool)>();
        offline.Changed += (_, change) => changes.Add(change);

        Assert.False(offline.IsOffline(Share));
        Assert.True(offline.Set(Share, true));
        Assert.False(offline.Set(Share, true));
        Assert.True(offline.IsOffline(Share));
        Assert.True(offline.Set(Share, false));
        Assert.False(offline.IsOffline(Share));

        Assert.Equal([("diskstation", true), ("diskstation", false)], changes);
    }

    [Fact]
    public void LocalLocations_CannotBeWorkedWithOffline()
    {
        var offline = new WorkOfflineRegistry();

        Assert.False(offline.Set(LocationPath.Parse(@"C:\data"), true));
        Assert.False(offline.IsOffline(LocationPath.Parse(@"C:\data")));
        Assert.Empty(offline.Servers);
    }

    [Fact]
    public async Task OneNasUnderTwoNames_IsOneOfflineEntry()
    {
        var identities = new ServerIdentityMap(new SameNas());
        await identities.IdentifyAsync("diskstation", TimeSpan.FromSeconds(1), default);
        await identities.IdentifyAsync("nas.example.net", TimeSpan.FromSeconds(1), default);
        var offline = new WorkOfflineRegistry(identities);

        offline.Set(Share, true);

        Assert.True(offline.IsOffline(Alias));
        Assert.Single(offline.Servers);
    }

    [Fact]
    public async Task List_WhileWorkingOffline_FailsAtOnce_NeverTouchesTheServer_AndLeavesItsHealthAlone()
    {
        var time = new ManualTimeProvider();
        var registry = new ShareHealthRegistry(new ServerIdentityMap(new NoDns()), time);
        var offline = new WorkOfflineRegistry();
        var inner = new CountingProvider();
        var provider = new HealthTrackingLocationProvider(inner, registry, time, offline);
        await ListAsync(provider, Share);
        offline.Set(Share, true);

        var error = await Assert.ThrowsAsync<LocationException>(() => ListAsync(provider, Share));

        Assert.True(error.WorkingOffline);
        Assert.True(error.IsTransient);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(ShareHealth.Online, registry.HealthOf(Share));

        offline.Set(Share, false);
        await ListAsync(provider, Share);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task List_OtherServersAndLocalFolders_AreNotAffected()
    {
        var time = new ManualTimeProvider();
        var registry = new ShareHealthRegistry(new ServerIdentityMap(new NoDns()), time);
        var offline = new WorkOfflineRegistry();
        var inner = new CountingProvider();
        var provider = new HealthTrackingLocationProvider(inner, registry, time, offline);
        offline.Set(Share, true);

        await ListAsync(provider, LocationPath.Parse(@"\\other\media"));
        await ListAsync(provider, LocationPath.Parse(@"C:\data"));

        Assert.Equal(2, inner.Calls);
    }
}
