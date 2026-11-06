using System.Diagnostics;

namespace Leviathan.Integration.Tests.Lab;

/// <summary>
/// Proves the lab itself works before anything is built on it (spec §19.2): Samba speaks SMB2, the proxy passes it through, and
/// latency and a cut line behave as the chaos tests will need. Skipped, with the reason, when the containers are not running.
/// </summary>
public sealed class NetworkLabTests(NetworkLab lab) : IClassFixture<NetworkLab>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        lab.Require();
        await lab.ResetAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (lab.Available)
        {
            await lab.ResetAsync();
        }
    }

    [Fact]
    public async Task Samba_AnswersAnSmb2Negotiate()
    {
        Assert.True(await NetworkLab.SmbAnswersAsync(NetworkLab.SambaPort, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Proxy_PassesSmbThrough()
    {
        Assert.True(await NetworkLab.SmbAnswersAsync(NetworkLab.ProxyPort, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Latency_SlowsTheAnswer()
    {
        await lab.AddLatencyAsync(600);

        var clock = Stopwatch.StartNew();
        var answered = await NetworkLab.SmbAnswersAsync(NetworkLab.ProxyPort, TimeSpan.FromSeconds(10));

        Assert.True(answered);
        Assert.True(clock.ElapsedMilliseconds >= 500, $"answered in {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task CutLine_StopsAnswers_AndRestoreBringsThemBack()
    {
        await lab.CutAsync();
        Assert.False(await NetworkLab.SmbAnswersAsync(NetworkLab.ProxyPort, TimeSpan.FromSeconds(3)));

        await lab.RestoreAsync();
        Assert.True(await NetworkLab.SmbAnswersAsync(NetworkLab.ProxyPort, TimeSpan.FromSeconds(5)));
    }
}
