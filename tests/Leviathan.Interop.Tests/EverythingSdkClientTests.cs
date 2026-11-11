using Leviathan.Interop;
using Leviathan.Search;

namespace Leviathan.Interop.Tests;

/// <summary>
/// Everything's SDK library is optional (spec §13). These tests cover the machine without it, which is the case to get right: a clear
/// state, no exception, no native crash. The calls into a real Everything are not covered here (none is installed on the development machine).
/// </summary>
public sealed class EverythingSdkClientTests
{
    [Fact]
    public async Task State_WithNoLibrary_IsNotInstalled()
    {
        using var client = new EverythingSdkClient(() => [Path.Combine(Path.GetTempPath(), "no-such-folder", "Everything64.dll")]);

        Assert.Equal(EverythingState.NotInstalled, await client.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task State_WithAFileThatIsNotTheLibrary_IsNotInstalled_NotACrash()
    {
        var fake = Path.Combine(Path.GetTempPath(), "leviathan-fake-everything-" + Guid.NewGuid().ToString("N") + ".dll");
        await File.WriteAllTextAsync(fake, "this is not a library");
        try
        {
            using var client = new EverythingSdkClient(() => [fake]);

            Assert.Equal(EverythingState.NotInstalled, await client.GetStateAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public async Task State_WithKernel32_WhichLacksTheExports_IsNotInstalled()
    {
        // A real library that loads but is not Everything's: binding must fail cleanly.
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        using var client = new EverythingSdkClient(() => [kernel32]);

        Assert.Equal(EverythingState.NotInstalled, await client.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Query_WithNoLibrary_SaysSo()
    {
        using var client = new EverythingSdkClient(() => []);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.QueryAsync("holiday", 0, 10, CancellationToken.None));
    }
}
