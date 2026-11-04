using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;

namespace Leviathan.Core.Tests;

public sealed class ServerIdentityTests
{
    private sealed class FakeResolver(Dictionary<string, string[]> table) : IHostAddressResolver
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(table.TryGetValue(host, out var a) ? a : []);
        }
    }

    private sealed class HangingResolver : IHostAddressResolver
    {
        public async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private static ServerIdentityMap Nas() => new(new FakeResolver(new()
    {
        ["diskstation"] = ["192.168.1.5", "192.168.1.6"],
        ["nas.example.net"] = ["192.168.1.6"],
        ["other"] = ["192.168.1.9"],
    }));

    [Theory]
    [InlineData("NAS.", "nas")]
    [InlineData(" Diskstation ", "diskstation")]
    [InlineData("[fe80::1%12]", "fe80::1")]
    public void Normalize_SpellingsOfOneName_CompareEqual(string input, string expected) =>
        Assert.Equal(expected, ServerIdentityMap.Normalize(input));

    [Fact]
    public void KeyFor_AnUnknownName_IsItself() => Assert.Equal("nas", Nas().KeyFor("NAS"));

    [Fact]
    public async Task Identify_NamesSharingAnAddress_BecomeOneServer()
    {
        var map = Nas();

        var first = await map.IdentifyAsync("diskstation", Wait, default);
        await map.IdentifyAsync("nas.example.net", Wait, default);
        await map.IdentifyAsync("192.168.1.5", Wait, default);

        Assert.Equal("diskstation", first);
        Assert.True(map.AreSame("NAS.example.net", "DISKSTATION"));
        Assert.True(map.AreSame("192.168.1.5", "nas.example.net"));
        Assert.False(map.AreSame("other", "diskstation"));
    }

    [Fact]
    public async Task Identify_AKeyAlreadyShown_DoesNotChangeWhenAliasesJoin()
    {
        var map = Nas();
        await map.IdentifyAsync("nas.example.net", Wait, default);
        var before = map.KeyFor("nas.example.net");

        await map.IdentifyAsync("diskstation", Wait, default);

        Assert.Equal(before, map.KeyFor("diskstation"));
        Assert.Equal(before, map.KeyFor("192.168.1.5"));
    }

    [Fact]
    public async Task Identify_LoopbackAddresses_NeverMergeDifferentNames()
    {
        var map = new ServerIdentityMap(new FakeResolver(new() { ["a"] = ["127.0.0.1"], ["b"] = ["127.0.0.1"] }));

        await map.IdentifyAsync("a", Wait, default);
        await map.IdentifyAsync("b", Wait, default);

        Assert.False(map.AreSame("a", "b"));
    }

    [Fact]
    public async Task Identify_AnAddress_IsNotResolved()
    {
        var resolver = new FakeResolver([]);
        var map = new ServerIdentityMap(resolver);

        await map.IdentifyAsync("10.0.0.2", Wait, default);

        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Identify_ALookupThatHangs_GivesUpAtTheTimeout_AndKeepsTheName()
    {
        var map = new ServerIdentityMap(new HangingResolver());

        var key = await map.IdentifyAsync("Dead", TimeSpan.FromMilliseconds(50), default);

        Assert.Equal("dead", key);
    }

    [Fact]
    public async Task Identify_WhenTheCallerCancels_Throws()
    {
        var map = new ServerIdentityMap(new HangingResolver());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => map.IdentifyAsync("dead", Wait, cts.Token));
    }

    [Fact]
    public async Task DeviceKeys_OneNasUnderTwoNames_IsOneDevice()
    {
        var map = Nas();
        await map.IdentifyAsync("diskstation", Wait, default);
        await map.IdentifyAsync("nas.example.net", Wait, default);

        var byName = DeviceKeys.For(LocationPath.Parse(@"\\diskstation\media"), map);
        var byDdns = DeviceKeys.For(LocationPath.Parse(@"\\nas.example.net\backup\x"), map);
        var job = DeviceKeys.ForJob([LocationPath.Parse(@"D:\Exports"), LocationPath.Parse(@"\\NAS.example.net\media")], map);

        Assert.Equal(byName, byDdns);
        Assert.Equal(job, byName);
        Assert.Equal("C:", DeviceKeys.For(LocationPath.Parse(@"c:\x"), map));
        Assert.Null(DeviceKeys.ForJob([], map));
    }
}
