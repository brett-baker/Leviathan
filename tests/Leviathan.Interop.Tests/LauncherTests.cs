using System.Diagnostics;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Interop.Tests;

public sealed class LauncherTests
{
    private sealed class RecordingStarter(LaunchResult? result = null) : IProcessStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public Task<LaunchResult> StartAsync(ProcessStartInfo info, CancellationToken cancellationToken)
        {
            Started.Add(info);
            return Task.FromResult(result ?? LaunchResult.Ok);
        }
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task OpenAsync_UsesShellExecuteOnTheFile_InItsFolder()
    {
        var starter = new RecordingStarter();

        await new WindowsFileLauncher(starter).OpenAsync(P(@"C:\Videos\clip.mkv"), CancellationToken.None);

        var info = Assert.Single(starter.Started);
        Assert.Equal(@"C:\Videos\clip.mkv", info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Videos", info.WorkingDirectory);
        Assert.Equal(string.Empty, info.Verb);
    }

    [Fact]
    public async Task RunAsAdministrator_UsesTheRunAsVerb()
    {
        var starter = new RecordingStarter();

        await new WindowsFileLauncher(starter).RunAsAdministratorAsync(P(@"C:\Tools\setup.exe"), CancellationToken.None);

        Assert.Equal("runas", Assert.Single(starter.Started).Verb);
    }

    private sealed class RecordingSheet(bool result = true) : IPropertiesSheet
    {
        public List<string> Shown { get; } = [];

        public bool Show(string win32Path)
        {
            Shown.Add(win32Path);
            return result;
        }
    }

    // Regression: Process.Start with the "properties" verb failed ("No application is associated"), so Properties showed an error.
    [Fact]
    public async Task ShowProperties_UsesTheShellPropertiesSheet_NotAProcess()
    {
        var starter = new RecordingStarter();
        var sheet = new RecordingSheet();

        var result = await new WindowsFileLauncher(starter, sheet).ShowPropertiesAsync(P(@"C:\Tools\setup.exe"), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal([@"C:\Tools\setup.exe"], sheet.Shown);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task ShowProperties_WhenWindowsRefuses_SaysSo()
    {
        var result = await new WindowsFileLauncher(new RecordingStarter(), new RecordingSheet(false)).ShowPropertiesAsync(P(@"C:\Tools"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("Tools", result.Error);
    }

    [Fact]
    public async Task OpenWithAsync_RunsTheChosenProgram_WithThePathAsOneArgument()
    {
        var starter = new RecordingStarter();

        await new WindowsFileLauncher(starter).OpenWithAsync(P(@"C:\My Files\a b.txt"), @"C:\Program Files\Editor\editor.exe", CancellationToken.None);

        var info = Assert.Single(starter.Started);
        Assert.Equal(@"C:\Program Files\Editor\editor.exe", info.FileName);
        Assert.Equal([@"C:\My Files\a b.txt"], info.ArgumentList);
    }

    [Fact]
    public async Task ChooseAppAsync_OpensTheSystemDialog()
    {
        var starter = new RecordingStarter();

        await new WindowsFileLauncher(starter).ChooseAppAsync(P(@"C:\x\f.xyz"), CancellationToken.None);

        var info = Assert.Single(starter.Started);
        Assert.Equal("rundll32.exe", info.FileName);
        Assert.Equal(@"shell32.dll,OpenAs_RunDLL C:\x\f.xyz", Assert.Single(info.ArgumentList));
    }

    [Fact]
    public void TerminalInfo_WindowsTerminal_UsesTheDirectoryArgument()
    {
        var info = WindowsFileLauncher.TerminalInfo(P(@"C:\My Projects"), TerminalKind.WindowsTerminal);

        Assert.Equal("wt.exe", info.FileName);
        Assert.Equal(["-d", @"C:\My Projects"], info.ArgumentList);
    }

    [Fact]
    public void TerminalInfo_PowerShellAndCmd_StartInTheFolder()
    {
        var powershell = WindowsFileLauncher.TerminalInfo(P(@"C:\src"), TerminalKind.PowerShell);
        var cmd = WindowsFileLauncher.TerminalInfo(P(@"C:\src"), TerminalKind.CommandPrompt);

        Assert.Equal(@"C:\src", powershell.WorkingDirectory);
        Assert.Contains("-NoExit", powershell.ArgumentList);
        Assert.Equal(@"C:\src", cmd.WorkingDirectory);
        Assert.Empty(cmd.ArgumentList);
    }

    [Fact]
    public void TerminalInfo_CmdInAShare_UsesPushdBecauseCmdCannotStartInAUncFolder()
    {
        var info = WindowsFileLauncher.TerminalInfo(P(@"\\nas\media\inbox"), TerminalKind.CommandPrompt);

        Assert.Equal(["/K", "pushd \"\\\\nas\\media\\inbox\""], info.ArgumentList);
        Assert.Equal(string.Empty, info.WorkingDirectory);
    }

    [Fact]
    public void TerminalInfo_Wsl_UsesCdWithTheWindowsPath()
    {
        var info = WindowsFileLauncher.TerminalInfo(P(@"C:\src"), TerminalKind.Wsl);

        Assert.Equal("wsl.exe", info.FileName);
        Assert.Equal(["--cd", @"C:\src"], info.ArgumentList);
    }

    [Fact]
    public async Task LongPaths_ArePassedInTheirExtendedForm()
    {
        var deep = @"C:\" + string.Join('\\', Enumerable.Repeat("directory-name-segment", 14)) + @"\file.txt";
        var starter = new RecordingStarter();

        await new WindowsFileLauncher(starter).OpenAsync(P(deep), CancellationToken.None);

        Assert.StartsWith(@"\\?\C:\", Assert.Single(starter.Started).FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessStarter_StartsARealHiddenProgram()
    {
        var info = new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true };

        var result = await new ProcessStarter().StartAsync(info, CancellationToken.None);

        Assert.True(result.Started);
    }

    [Fact]
    public async Task ProcessStarter_AMissingProgram_ReportsAnErrorInsteadOfThrowing()
    {
        var info = new ProcessStartInfo(@"C:\definitely\not\here.exe") { UseShellExecute = false };

        var result = await new ProcessStarter().StartAsync(info, CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(result.Cancelled);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ProcessStarter_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessStarter().StartAsync(new ProcessStartInfo("cmd.exe"), cts.Token));
    }

    [Fact]
    public void Associations_TextFiles_HaveADefaultApp()
    {
        var app = new WindowsFileAssociations().GetDefaultApp("txt");

        Assert.NotNull(app);
        Assert.False(string.IsNullOrWhiteSpace(app.Name));
    }

    [Fact]
    public void Associations_AcceptTheDot_AndUnknownTypesHaveNoApp()
    {
        var associations = new WindowsFileAssociations();

        Assert.NotNull(associations.GetDefaultApp(".txt"));
        Assert.Null(associations.GetDefaultApp(".leviathan-not-a-real-type"));
        Assert.Null(associations.GetDefaultApp(""));
    }
}
