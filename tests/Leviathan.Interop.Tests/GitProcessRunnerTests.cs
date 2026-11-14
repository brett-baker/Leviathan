using System.Diagnostics;
using Leviathan.Core.Git;

namespace Leviathan.Interop.Tests;

/// <summary>The real git.exe against a real temporary repository (spec §15.6). Skipped on a PC without Git.</summary>
public sealed class GitProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-git-" + Guid.NewGuid().ToString("N"));

    public GitProcessRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static async Task<bool> GitAsync(string folder, params string[] args)
    {
        var result = await new GitProcessRunner().RunAsync(folder, args, TimeSpan.FromSeconds(30), CancellationToken.None);
        return result.Ok;
    }

    [Fact]
    public async Task ARealRepository_ShowsModifiedAddedAndUntrackedFiles()
    {
        if ((await new GitProcessRunner().RunAsync(_root, ["--version"], TimeSpan.FromSeconds(10), CancellationToken.None)).NotInstalled)
        {
            Assert.Skip("git.exe is not installed on this machine.");
        }

        Assert.True(await GitAsync(_root, "init", "-q", "-b", "main"));
        File.WriteAllText(Path.Combine(_root, "kept.txt"), "one");
        File.WriteAllText(Path.Combine(_root, "änderung mit leerzeichen.txt"), "x");
        Assert.True(await GitAsync(_root, "add", "."));
        Assert.True(await GitAsync(_root, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first"));
        File.WriteAllText(Path.Combine(_root, "kept.txt"), "two");
        File.WriteAllText(Path.Combine(_root, "fresh.txt"), "new");

        var run = await new GitProcessRunner().RunAsync(_root, GitCommands.Status, TimeSpan.FromSeconds(30), CancellationToken.None);
        var status = GitRepoStatus.Parse(_root, run.Output);

        Assert.True(run.Ok);
        Assert.Equal("main", status.Branch);
        Assert.Equal(GitState.Modified, status.StateOf(Path.Combine(_root, "kept.txt"), false));
        Assert.Equal(GitState.Untracked, status.StateOf(Path.Combine(_root, "fresh.txt"), false));
        Assert.Equal(GitState.Clean, status.StateOf(Path.Combine(_root, "änderung mit leerzeichen.txt"), false));
        Assert.Equal(2, status.ChangedCount);
    }

    [Fact]
    public async Task AFolderThatIsNotARepository_IsAFailure_NotACrash()
    {
        if ((await new GitProcessRunner().RunAsync(_root, ["--version"], TimeSpan.FromSeconds(10), CancellationToken.None)).NotInstalled)
        {
            Assert.Skip("git.exe is not installed on this machine.");
        }

        var run = await new GitProcessRunner().RunAsync(_root, GitCommands.Status, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(run.Ok);
    }
}
