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

    // A server on its own is signed in to through \\server\IPC$, as Explorer does; the old target (\\server as a disk) could never connect.
    [Theory]
    [InlineData(@"\\192.168.1.39", @"\\192.168.1.39\IPC$")]
    [InlineData(@"\\nas\media", @"\\nas\media")]
    [InlineData(@"\\nas\media\show\ep1.mkv", @"\\nas\media")]
    public void SignInTarget_IsTheServersIpcShare_OrTheShareItself(string location, string expected) =>
        Assert.Equal(expected, WindowsShareConnector.ShareRootOf(LocationPath.Parse(location)));

    // Regression: Sign in failed with "Invalid flags" (Win32 1004) and no dialog. Found by calling CredUIPromptForCredentials with each
    // combination: ALWAYS_SHOW_UI alone, or with SERVER_CREDENTIAL, is rejected; it is valid only with GENERIC_CREDENTIALS. The dialog
    // itself needs a person, so this pins the rules the real calls showed.
    [Fact]
    public void SignInFlags_FollowWhatWindowsAccepts()
    {
        var flags = WindowsShareConnector.SignInFlags;

        Assert.False(flags.HasFlag(Windows.Win32.Security.Credentials.CREDUI_FLAGS.CREDUI_FLAGS_ALWAYS_SHOW_UI));
        Assert.False(flags.HasFlag(Windows.Win32.Security.Credentials.CREDUI_FLAGS.CREDUI_FLAGS_GENERIC_CREDENTIALS));
        Assert.True(flags.HasFlag(Windows.Win32.Security.Credentials.CREDUI_FLAGS.CREDUI_FLAGS_SERVER_CREDENTIAL));
        Assert.True(flags.HasFlag(Windows.Win32.Security.Credentials.CREDUI_FLAGS.CREDUI_FLAGS_EXPECT_CONFIRMATION));
    }

    [Fact]
    public async Task SigningInToALocalFolder_ShowsNoDialog_AndHasNothingToDo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        var result = await new WindowsShareConnector().SignInAsync(LocationPath.Parse(Environment.SystemDirectory), 0, CancellationToken.None);

        Assert.Equal(ConnectOutcome.NotNeeded, result.Outcome);
    }
}
