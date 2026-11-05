using System.Net;
using Leviathan.Core.Resilience;
using Leviathan.Testing;

namespace Leviathan.Core.Tests;

/// <summary>The negotiated link speed shown in the status bar (spec §3.5 item 3).</summary>
public sealed class LinkSpeedTests
{
    private sealed class FakeResolver(params string[] addresses) : IHostAddressResolver
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(addresses);
        }
    }

    private sealed class FakeAdapters(Func<IPAddress, long?> speed) : IAdapterTable
    {
        public List<IPAddress> Asked { get; } = [];

        public long? SpeedTo(IPAddress remote)
        {
            Asked.Add(remote);
            return speed(remote);
        }
    }

    [Theory]
    [InlineData(1_000_000_000L, "1 Gb/s")]
    [InlineData(2_500_000_000L, "2.5 Gb/s")]
    [InlineData(10_000_000_000L, "10 Gb/s")]
    [InlineData(100_000_000L, "100 Mb/s")]
    [InlineData(54_000_000L, "54 Mb/s")]
    [InlineData(0L, "")]
    public void Format_NamesTheSpeedTheWayAdaptersAre(long bits, string expected) =>
        Assert.Equal(expected, LinkSpeedProbe.Format(bits));

    [Fact]
    public async Task SpeedTo_ResolvesTheHost_AndAsksTheAdapterThatReachesIt()
    {
        var adapters = new FakeAdapters(_ => 1_000_000_000);
        var probe = new LinkSpeedProbe(new FakeResolver("192.168.1.10"), adapters);

        var speed = await probe.SpeedToAsync("diskstation", CancellationToken.None);

        Assert.Equal(1_000_000_000, speed);
        Assert.Equal([IPAddress.Parse("192.168.1.10")], adapters.Asked);
    }

    [Fact]
    public async Task SpeedTo_ForAnAddress_NeedsNoNameLookup()
    {
        var resolver = new FakeResolver();

        var speed = await new LinkSpeedProbe(resolver, new FakeAdapters(_ => 2_500_000_000)).SpeedToAsync("192.168.1.10", CancellationToken.None);

        Assert.Equal(2_500_000_000, speed);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task SpeedTo_TriesTheNextAddress_WhenTheFirstHasNoRoute()
    {
        var adapters = new FakeAdapters(a => a.ToString() == "10.0.0.5" ? 1_000_000_000 : null);

        var speed = await new LinkSpeedProbe(new FakeResolver("192.168.1.10", "10.0.0.5"), adapters).SpeedToAsync("nas", CancellationToken.None);

        Assert.Equal(1_000_000_000, speed);
        Assert.Equal(2, adapters.Asked.Count);
    }

    [Fact]
    public async Task SpeedTo_ForThisMachineOrAnUnknownName_IsNull()
    {
        Assert.Null(await new LinkSpeedProbe(new FakeResolver("127.0.0.1"), new FakeAdapters(_ => 1)).SpeedToAsync("localhost", CancellationToken.None));
        Assert.Null(await new LinkSpeedProbe(new FakeResolver(), new FakeAdapters(_ => 1)).SpeedToAsync("nowhere", CancellationToken.None));
    }

    [Fact]
    public async Task Answers_AreRemembered_ForAMinute_EvenWhenTheAnswerIsNothing()
    {
        var time = new ManualTimeProvider();
        var resolver = new FakeResolver("192.168.1.10");
        var probe = new LinkSpeedProbe(resolver, new FakeAdapters(_ => null), time);
        Assert.False(probe.Cached("nas").Known);

        await probe.SpeedToAsync("nas", CancellationToken.None);
        await probe.SpeedToAsync("NAS", CancellationToken.None);

        Assert.Equal(1, resolver.Calls);
        Assert.Equal((null, true), probe.Cached("nas"));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(probe.Cached("nas").Known);
        await probe.SpeedToAsync("nas", CancellationToken.None);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public void SystemAdapterTable_ForTheLoopbackOrNothing_NeverThrows()
    {
        var table = new SystemAdapterTable();

        _ = table.SpeedTo(IPAddress.Loopback);
        _ = table.SpeedTo(IPAddress.IPv6Loopback);
    }
}
