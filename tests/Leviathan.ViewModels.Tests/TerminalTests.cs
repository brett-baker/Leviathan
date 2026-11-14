using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Commands;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>Open terminal here (spec §15.7).</summary>
public sealed class TerminalTests
{
    private static readonly LocationPath Here = LocationPath.Parse(@"D:\Work");

    private static FileEntry Folder(string path) =>
        new(Path.GetFileName(path), LocationPath.Parse(path), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    private static FileEntry File(string path) =>
        new(Path.GetFileName(path), LocationPath.Parse(path), FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Fact]
    public void Folder_IsTheSelectedFolder_ElseTheSelectedFilesFolder_ElseTheFolderShown()
    {
        Assert.Equal(@"D:\Work\src", TerminalTargets.FolderFor([Folder(@"D:\Work\src")], Here)!.Value);
        Assert.Equal(@"D:\Work\src", TerminalTargets.FolderFor([File(@"D:\Work\src\a.txt")], Here)!.Value);
        Assert.Equal(@"D:\Work", TerminalTargets.FolderFor([], Here)!.Value);
    }

    [Theory]
    [InlineData(@"\\nas\media\films", true)]
    [InlineData(@"\\nas", false)]
    [InlineData(@"C:\a\pack.zip\inside", false)]
    [InlineData("sftp://host/dir", false)]
    [InlineData("shell:tag", false)]
    public void OnlyRealFolders_OnADriveOrAShare_Qualify(string folder, bool usable)
    {
        Assert.Equal(usable, TerminalTargets.IsUsable(LocationPath.Parse(folder)));
        Assert.Equal(usable, TerminalTargets.FolderFor([], LocationPath.Parse(folder)) is not null);
    }

    [Fact]
    public void AFileInsideAnArchive_HasNoFolderToOpenIn()
    {
        Assert.Null(TerminalTargets.FolderFor([File(@"C:\a\pack.zip\inner.txt")], Here));
    }

    [Fact]
    public void WindowsTerminalFallsBackToPowerShell_NothingElseFallsBack()
    {
        Assert.Equal(TerminalKind.PowerShell, TerminalTargets.FallbackFor(TerminalKind.WindowsTerminal));
        Assert.Null(TerminalTargets.FallbackFor(TerminalKind.PowerShell));
        Assert.Null(TerminalTargets.FallbackFor(TerminalKind.Wsl));
        Assert.Equal("Command Prompt", TerminalTargets.Title(TerminalKind.CommandPrompt));
    }

    [Fact]
    public async Task TheCommand_OpensTheChosenTerminal_AtTheFolderShown()
    {
        var h = new Harness(terminal: () => TerminalKind.Wsl);
        await h.Ready;

        await h.Run(CommandIds.TerminalHere);

        Assert.Equal([(TabTestSupport.P(@"C:\a"), TerminalKind.Wsl)], h.Launcher.Terminals);
    }

    [Fact]
    public async Task TheCommand_OnAFolderEntry_OpensInThatFolder()
    {
        var h = new Harness();
        await h.Ready;
        var folder = h.Tab.Items.Rows.Single(r => r.Name == "b");

        await h.Run(CommandIds.TerminalHere, folder.Location.Value, folder);

        Assert.Equal(folder.Location, h.Launcher.Terminals[0].Folder);
    }

    [Fact]
    public async Task WhenWindowsTerminalIsMissing_PowerShellOpens_AndTheMessageSaysSo()
    {
        var h = new Harness();
        await h.Ready;
        h.Launcher.FailingTerminals.Add(TerminalKind.WindowsTerminal);

        await h.Run(CommandIds.TerminalHere);

        Assert.Equal([TerminalKind.WindowsTerminal, TerminalKind.PowerShell], h.Launcher.Terminals.Select(t => t.Kind));
        Assert.Contains("PowerShell was opened instead", Assert.Single(h.Shell.Messages));
    }

    [Fact]
    public async Task WhenNothingCanStart_TheReasonIsShown()
    {
        var h = new Harness(terminal: () => TerminalKind.PowerShell);
        await h.Ready;
        h.Launcher.FailingTerminals.Add(TerminalKind.PowerShell);

        await h.Run(CommandIds.TerminalHere);

        Assert.Equal(["wt.exe was not found."], h.Shell.Messages);
    }

    [Fact]
    public void TheKey_IsCtrlAltT()
    {
        Assert.Equal(CommandIds.TerminalHere, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse("Ctrl+Alt+T"), false));
    }
}
