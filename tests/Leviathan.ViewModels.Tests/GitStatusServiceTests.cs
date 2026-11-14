using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Git;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Git;

namespace Leviathan.ViewModels.Tests;

/// <summary>The Git status service (spec §15.6): when it asks Git, what it does when Git fails, and what it answers.</summary>
public sealed class GitStatusServiceTests
{
    private sealed class FakeGit(GitRunResult? answer = null) : IGitRunner
    {
        public GitRunResult Answer { get; set; } = answer ?? new GitRunResult(0, "# branch.head main\0# branch.ab +1 -0\01 .M N... 100644 100644 100644 a b src/a.cs\0? new.txt\0");

        public List<string> Folders { get; } = [];

        public Task<GitRunResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Folders.Add(workingDirectory);
            return Task.FromResult(Answer);
        }
    }

    private static MockFileSystem Disk()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"D:\Work\repo\.git");
        fs.AddDirectory(@"D:\Work\repo\src");
        fs.AddDirectory(@"D:\Plain");
        return fs;
    }

    private static FileEntry File(string path, bool folder = false) =>
        new(Path.GetFileName(path), LocationPath.Parse(path), folder ? FileEntryKind.Folder : FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, folder ? FileAttributes.Directory : FileAttributes.Normal);

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task InARepository_StatusIsReadOnce_AndItemsShowTheirState()
    {
        var git = new FakeGit();
        var service = new GitStatusService(git, Disk(), () => true, new ManualTimeProvider());
        var raised = new List<string>();
        service.Changed += (_, root) => raised.Add(root);

        await service.RefreshAsync(P(@"D:\Work\repo\src"), force: false, CancellationToken.None);

        Assert.Equal([@"D:\Work\repo"], git.Folders);
        Assert.Equal([@"D:\Work\repo"], raised);
        Assert.Equal(GitState.Modified, service.StateOf(File(@"D:\Work\repo\src\a.cs")));
        Assert.Equal(GitState.Modified, service.StateOf(File(@"D:\Work\repo\src", folder: true)));
        Assert.Equal(GitState.Untracked, service.StateOf(File(@"D:\Work\repo\new.txt")));
        Assert.Equal("main ↑1 · 2 changed", service.SummaryFor(P(@"D:\Work\repo\src")));
    }

    [Fact]
    public async Task OutsideARepository_Git_IsNeverAsked()
    {
        var git = new FakeGit();
        var service = new GitStatusService(git, Disk(), () => true);

        await service.RefreshAsync(P(@"D:\Plain"), force: false, CancellationToken.None);

        Assert.Empty(git.Folders);
        Assert.Null(service.SummaryFor(P(@"D:\Plain")));
        Assert.Equal(GitState.Clean, service.StateOf(File(@"D:\Plain\x.txt")));
    }

    [Fact]
    public async Task ASharesFolder_IsSkipped_BecauseStatusOverTheNetworkIsSlow()
    {
        var git = new FakeGit();
        var service = new GitStatusService(git, Disk(), () => true);

        await service.RefreshAsync(P(@"\\nas\code\repo"), force: false, CancellationToken.None);

        Assert.Empty(git.Folders);
    }

    [Fact]
    public async Task WhenTheSettingIsOff_NothingRuns()
    {
        var git = new FakeGit();
        var service = new GitStatusService(git, Disk(), () => false);

        await service.RefreshAsync(P(@"D:\Work\repo"), force: false, CancellationToken.None);

        Assert.Empty(git.Folders);
        Assert.False(service.Active);
    }

    [Fact]
    public async Task AskingAgainAtOnce_ReusesTheAnswer_UntilTheIntervalPasses_OrItIsForced()
    {
        var time = new ManualTimeProvider();
        var git = new FakeGit();
        var service = new GitStatusService(git, Disk(), () => true, time);

        await service.RefreshAsync(P(@"D:\Work\repo"), false, CancellationToken.None);
        await service.RefreshAsync(P(@"D:\Work\repo\src"), false, CancellationToken.None);
        Assert.Single(git.Folders);

        await service.RefreshAsync(P(@"D:\Work\repo"), force: true, CancellationToken.None);
        Assert.Equal(2, git.Folders.Count);

        time.Advance(GitStatusService.MinInterval + TimeSpan.FromSeconds(1));
        await service.RefreshAsync(P(@"D:\Work\repo"), false, CancellationToken.None);
        Assert.Equal(3, git.Folders.Count);
    }

    [Fact]
    public async Task WithoutGitInstalled_TheFeatureTurnsItselfOff_AndStopsAsking()
    {
        var git = new FakeGit(new GitRunResult(-1, string.Empty, NotInstalled: true));
        var service = new GitStatusService(git, Disk(), () => true, new ManualTimeProvider());

        await service.RefreshAsync(P(@"D:\Work\repo"), force: true, CancellationToken.None);
        await service.RefreshAsync(P(@"D:\Work\repo"), force: true, CancellationToken.None);

        Assert.Single(git.Folders);
        Assert.False(service.Active);
    }

    [Fact]
    public async Task ATimeoutOrAFailure_KeepsTheLastAnswer()
    {
        var git = new FakeGit();
        var service = new GitStatusService(git, Disk(), () => true, new ManualTimeProvider());
        await service.RefreshAsync(P(@"D:\Work\repo"), force: true, CancellationToken.None);

        git.Answer = new GitRunResult(-1, string.Empty, TimedOut: true);
        await service.RefreshAsync(P(@"D:\Work\repo"), force: true, CancellationToken.None);
        git.Answer = new GitRunResult(128, "fatal");
        await service.RefreshAsync(P(@"D:\Work\repo"), force: true, CancellationToken.None);

        Assert.Equal(GitState.Modified, service.StateOf(File(@"D:\Work\repo\src\a.cs")));
        Assert.True(service.Active);
    }

    [Fact]
    public async Task ARepositoryInsideAnother_AnswersWithTheNearestOne()
    {
        var fs = Disk();
        fs.AddDirectory(@"D:\Work\repo\vendor\lib\.git");
        var git = new FakeGit();
        var service = new GitStatusService(git, fs, () => true, new ManualTimeProvider());
        await service.RefreshAsync(P(@"D:\Work\repo"), true, CancellationToken.None);
        git.Answer = new GitRunResult(0, "# branch.head topic\0");
        await service.RefreshAsync(P(@"D:\Work\repo\vendor\lib"), true, CancellationToken.None);

        Assert.StartsWith("topic", service.SummaryFor(P(@"D:\Work\repo\vendor\lib\x")));
        Assert.StartsWith("main", service.SummaryFor(P(@"D:\Work\repo\src")));
    }
}
