namespace Leviathan.Interop.Tests;

/// <summary>
/// The Windows share enumerator (spec §7.1). Only this computer is asked (never a real NAS); what it shares varies, so the tests check
/// the rules, not the names.
/// </summary>
public sealed class ShareEnumeratorTests
{
    [Fact]
    public async Task ListShares_OfThisComputer_LeavesOutAdministrativeShares()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        try
        {
            var shares = await new WindowsShareEnumerator().ListSharesAsync("localhost", CancellationToken.None);

            Assert.All(shares, s => Assert.False(s.Name.EndsWith('$'), s.Name));
        }
        catch (IOException)
        {
            Assert.Skip("The Server service is not running on this machine, so it has no shares to list.");
        }
    }

    [Fact]
    public async Task ListShares_WithACancelledToken_IsCancelled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsShareEnumerator().ListSharesAsync("localhost", cancel.Token));
    }

    [Fact]
    public async Task ListShares_OfAServerThatDoesNotExist_FailsAsAnIoError()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // .invalid never resolves (RFC 2606), so no real machine is contacted.
        var error = await Assert.ThrowsAsync<IOException>(() => new WindowsShareEnumerator().ListSharesAsync("no-such-server.invalid", timeout.Token));

        Assert.NotEqual(0, error.HResult);
    }
}
