using Leviathan.Core.Discovery;
using Leviathan.Testing;
using Leviathan.ViewModels.Sidebar;

namespace Leviathan.ViewModels.Tests;

/// <summary>The Nearby list (spec §7.1): runs discoveries in the background, merges what they find, never lets one failure through.</summary>
public sealed class NearbyServiceTests
{
    private sealed class FakeDiscovery(params DiscoveredDevice[] devices) : IDeviceDiscovery
    {
        public string Name => "fake";

        public Exception? Failure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public List<DiscoveredDevice> Devices { get; } = [.. devices];

        public int Runs { get; private set; }

        public async Task DiscoverAsync(Action<DiscoveredDevice> found, CancellationToken cancellationToken)
        {
            Runs++;
            foreach (var device in Devices)
            {
                found(device);
            }

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private static string[] Hosts(NearbyService service) => [.. service.Devices.Select(d => d.Host).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Refresh_CollectsDevicesFromEveryDiscovery_AndMergesTheSameHost()
    {
        var service = new NearbyService(
        [
            new FakeDiscovery(new DiscoveredDevice("192.168.1.10", "DiskStation"), new DiscoveredDevice("192.168.1.20")),
            new FakeDiscovery(new DiscoveredDevice("192.168.1.20", "QNAP"), new DiscoveredDevice("192.168.1.10")),
        ]);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(["192.168.1.10", "192.168.1.20"], Hosts(service));
        Assert.Equal("DiskStation", service.Devices.Single(d => d.Host == "192.168.1.10").Name);
        Assert.Equal("QNAP", service.Devices.Single(d => d.Host == "192.168.1.20").Name);
        Assert.False(service.IsSearching);
    }

    [Fact]
    public async Task Refresh_ShowsDevicesWhileSearching_AndSaysSo()
    {
        var gate = new TaskCompletionSource();
        var service = new NearbyService([new FakeDiscovery(new DiscoveredDevice("nas")) { Gate = gate }]);
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);

        var run = service.RefreshAsync(CancellationToken.None);
        while (service.Devices.Count == 0)
        {
            await Task.Delay(5);
        }

        Assert.True(service.IsSearching);
        Assert.Equal(["nas"], Hosts(service));

        gate.SetResult();
        await run;

        Assert.False(service.IsSearching);
        Assert.True(changes >= 3, $"changes: {changes}");
    }

    [Fact]
    public async Task Refresh_WhileRunning_JoinsTheRunInsteadOfStartingAnother()
    {
        var gate = new TaskCompletionSource();
        var discovery = new FakeDiscovery { Gate = gate };
        var service = new NearbyService([discovery]);

        var first = service.RefreshAsync(CancellationToken.None);
        var second = service.RefreshAsync(CancellationToken.None, force: true);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, discovery.Runs);
    }

    [Fact]
    public async Task Refresh_WaitsForTheGapBetweenRuns_UnlessForced()
    {
        var time = new ManualTimeProvider();
        var discovery = new FakeDiscovery();
        var service = new NearbyService([discovery], time);

        await service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, discovery.Runs);

        await service.RefreshAsync(CancellationToken.None, force: true);
        Assert.Equal(2, discovery.Runs);

        time.Advance(NearbyService.MinGap + TimeSpan.FromSeconds(1));
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, discovery.Runs);
    }

    [Fact]
    public async Task ADiscoveryThatFails_OnlyLosesItsOwnAnswers()
    {
        var broken = new FakeDiscovery { Failure = new System.Net.Sockets.SocketException() };
        var good = new FakeDiscovery(new DiscoveredDevice("nas"));
        var service = new NearbyService([broken, good]);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(["nas"], Hosts(service));
    }

    [Fact]
    public async Task ADeviceThatStopsAnswering_IsGoneAfterTheNextRun()
    {
        var discovery = new FakeDiscovery(new DiscoveredDevice("nas"), new DiscoveredDevice("pi"));
        var service = new NearbyService([discovery]);
        await service.RefreshAsync(CancellationToken.None);

        discovery.Devices.RemoveAt(1);
        await service.RefreshAsync(CancellationToken.None, force: true);

        Assert.Equal(["nas"], Hosts(service));
    }

    [Fact]
    public async Task ACancelledRun_KeepsWhatWasFoundBefore()
    {
        var discovery = new FakeDiscovery(new DiscoveredDevice("nas"));
        var service = new NearbyService([discovery]);
        await service.RefreshAsync(CancellationToken.None);

        discovery.Devices.Clear();
        discovery.Gate = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var run = service.RefreshAsync(cancel.Token, force: true);
        await cancel.CancelAsync();
        await run;

        Assert.Equal(["nas"], Hosts(service));
        Assert.False(service.IsSearching);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData(@"nas\media")]
    public async Task DevicesWithoutAUsableHost_AreIgnored(string host)
    {
        var service = new NearbyService([new FakeDiscovery(new DiscoveredDevice(host), new DiscoveredDevice(@"\\nas"))]);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(["nas"], Hosts(service));
    }
}
