using Leviathan.Core.Archives;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Core.Tests;

/// <summary>Opening things inside archives through the usual launcher (spec §15.4).</summary>
public sealed class StagingFileLauncherTests
{
    private sealed class RecordingLauncher : IFileLauncher
    {
        public List<string> Calls { get; } = [];

        public Task<LaunchResult> OpenAsync(LocationPath path, CancellationToken cancellationToken) => Record("open", path);

        public Task<LaunchResult> RunAsAdministratorAsync(LocationPath path, CancellationToken cancellationToken) => Record("admin", path);

        public Task<LaunchResult> OpenWithAsync(LocationPath path, string executable, CancellationToken cancellationToken) => Record("with " + executable, path);

        public Task<LaunchResult> ChooseAppAsync(LocationPath path, CancellationToken cancellationToken) => Record("choose", path);

        public Task<LaunchResult> OpenTerminalAsync(LocationPath folder, TerminalKind kind, CancellationToken cancellationToken) => Record("terminal", folder);

        public Task<LaunchResult> ShowPropertiesAsync(LocationPath path, CancellationToken cancellationToken) => Record("properties", path);

        private Task<LaunchResult> Record(string what, LocationPath path)
        {
            Calls.Add($"{what}: {path.Value}");
            return Task.FromResult(LaunchResult.Ok);
        }
    }

    private sealed class FakeStager(Func<LocationPath, string> map) : IArchiveStager
    {
        public Exception? Failure { get; set; }

        public Task<string> StageAsync(LocationPath entry, CancellationToken cancellationToken) =>
            Failure is { } failure ? Task.FromException<string>(failure) : Task.FromResult(map(entry));
    }

    private static readonly LocationPath Inside = LocationPath.Parse(@"D:\x\pack.zip\docs\a.txt");

    private static (StagingFileLauncher Launcher, RecordingLauncher Inner, FakeStager Stager) Make()
    {
        var inner = new RecordingLauncher();
        var stager = new FakeStager(_ => @"C:\Scratch\abc\docs\a.txt");
        return (new StagingFileLauncher(inner, stager), inner, stager);
    }

    [Fact]
    public async Task Open_InsideAnArchive_LaunchesTheUnpackedCopy()
    {
        var (launcher, inner, _) = Make();

        var result = await launcher.OpenAsync(Inside, CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal([@"open: C:\Scratch\abc\docs\a.txt"], inner.Calls);
    }

    [Fact]
    public async Task OpenWithAndChooseAndAdmin_AlsoUseTheUnpackedCopy()
    {
        var (launcher, inner, _) = Make();

        await launcher.OpenWithAsync(Inside, "notepad.exe", CancellationToken.None);
        await launcher.ChooseAppAsync(Inside, CancellationToken.None);
        await launcher.RunAsAdministratorAsync(Inside, CancellationToken.None);

        Assert.Equal([@"with notepad.exe: C:\Scratch\abc\docs\a.txt", @"choose: C:\Scratch\abc\docs\a.txt", @"admin: C:\Scratch\abc\docs\a.txt"], inner.Calls);
    }

    [Fact]
    public async Task Open_AnOrdinaryPath_PassesStraightThrough()
    {
        var (launcher, inner, _) = Make();
        var plain = LocationPath.Parse(@"D:\x\notes.txt");

        await launcher.OpenAsync(plain, CancellationToken.None);
        await launcher.ShowPropertiesAsync(plain, CancellationToken.None);
        await launcher.OpenTerminalAsync(LocationPath.Parse(@"D:\x"), TerminalKind.PowerShell, CancellationToken.None);

        Assert.Equal([@"open: D:\x\notes.txt", @"properties: D:\x\notes.txt", @"terminal: D:\x"], inner.Calls);
    }

    [Fact]
    public async Task Open_TheArchiveItself_IsNotUnpacked()
    {
        var (launcher, inner, _) = Make();

        await launcher.OpenAsync(LocationPath.Parse(@"D:\x\pack.zip"), CancellationToken.None);

        Assert.Equal([@"open: D:\x\pack.zip"], inner.Calls);
    }

    [Fact]
    public async Task PropertiesAndTerminal_InsideAnArchive_ExplainWhyNot()
    {
        var (launcher, inner, _) = Make();

        var properties = await launcher.ShowPropertiesAsync(Inside, CancellationToken.None);
        var terminal = await launcher.OpenTerminalAsync(Inside.Parent!, TerminalKind.PowerShell, CancellationToken.None);

        Assert.False(properties.Started);
        Assert.False(terminal.Started);
        Assert.NotNull(properties.Error);
        Assert.NotNull(terminal.Error);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Open_WhenUnpackingFails_SaysSoAndLaunchesNothing()
    {
        var (launcher, inner, stager) = Make();
        stager.Failure = new ArchiveException(ArchiveError.Unreadable, "bad data");

        var result = await launcher.OpenAsync(Inside, CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(result.Cancelled);
        Assert.Contains("a.txt could not be unpacked", result.Error);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Open_WhenCancelledWhileUnpacking_IsACancelNotAnError()
    {
        var (launcher, inner, stager) = Make();
        stager.Failure = new OperationCanceledException();

        var result = await launcher.OpenAsync(Inside, CancellationToken.None);

        Assert.True(result.Cancelled);
        Assert.Empty(inner.Calls);
    }
}
