using Leviathan.Core.Paths;
using Leviathan.Core.Resilience;

namespace Leviathan.Interop.Tests;

/// <summary>
/// The Windows share connector (spec §3.3 Reconnect). Only the paths that never reach a share or show a dialog are exercised here:
/// the real reconnect and the Windows sign-in dialog need a NAS and a person, and are part of the manual lab run.
/// </summary>
public sealed class ShareConnectorTests
{
    private static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory)![..1];

    [Fact]
    public async Task ReconnectingALocalDisk_HasNothingToDo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        var result = await new WindowsShareConnector().ReconnectDriveAsync(SystemDrive[0], CancellationToken.None);

        Assert.Equal(ConnectOutcome.NotNeeded, result.Outcome);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData('1')]
    [InlineData('-')]
    public async Task ReconnectingSomethingThatIsNotADriveLetter_HasNothingToDo(char letter)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        var result = await new WindowsShareConnector().ReconnectDriveAsync(letter, CancellationToken.None);

        Assert.Equal(ConnectOutcome.NotNeeded, result.Outcome);
    }

    [Fact]
    public async Task SigningInToALocalFolder_ShowsNoDialog_AndHasNothingToDo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        var result = await new WindowsShareConnector().SignInAsync(LocationPath.Parse(Environment.SystemDirectory), 0, CancellationToken.None);

        Assert.Equal(ConnectOutcome.NotNeeded, result.Outcome);
    }
}
