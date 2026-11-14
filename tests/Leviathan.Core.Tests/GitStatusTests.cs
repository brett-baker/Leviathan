using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Git;

namespace Leviathan.Core.Tests;

/// <summary>Reading Git's status and finding the repository (spec §15.6).</summary>
public sealed class GitStatusTests
{
    private const string Root = @"D:\Work\repo";

    private static string Output(params string[] parts) => string.Join('\0', parts) + '\0';

    private static GitRepoStatus Sample() => GitRepoStatus.Parse(Root, Output(
        "# branch.oid abc",
        "# branch.head main",
        "# branch.upstream origin/main",
        "# branch.ab +2 -1",
        "1 .M N... 100644 100644 100644 aaa bbb src/app/Program.cs",
        "1 A. N... 000000 100644 100644 000 ccc docs/new file.md",
        "1 .D N... 100644 100644 000000 aaa bbb old.txt",
        "2 R. N... 100644 100644 100644 aaa bbb R100 renamed.txt",
        "before.txt",
        "u UU N... 100644 100644 100644 100644 a b c conflict.txt",
        "? scratch/",
        "? notes é.txt",
        "! bin/"));

    [Fact]
    public void Branch_AheadAndBehind_AreRead()
    {
        var status = Sample();

        Assert.Equal("main", status.Branch);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
    }

    [Theory]
    [InlineData(@"D:\Work\repo\src\app\Program.cs", false, GitState.Modified)]
    [InlineData(@"D:\Work\repo\docs\new file.md", false, GitState.Added)]
    [InlineData(@"D:\Work\repo\old.txt", false, GitState.Deleted)]
    [InlineData(@"D:\Work\repo\renamed.txt", false, GitState.Renamed)]
    [InlineData(@"D:\Work\repo\conflict.txt", false, GitState.Conflicted)]
    [InlineData(@"D:\Work\repo\notes é.txt", false, GitState.Untracked)]
    [InlineData(@"D:\Work\repo\README.md", false, GitState.Clean)]
    [InlineData(@"D:\Elsewhere\x.txt", false, GitState.Clean)]
    public void AnItem_ShowsItsOwnState(string path, bool folder, GitState expected) => Assert.Equal(expected, Sample().StateOf(path, folder));

    [Fact]
    public void AFolder_ShowsTheMostImportantStateInsideIt()
    {
        var status = Sample();

        Assert.Equal(GitState.Modified, status.StateOf(@"D:\Work\repo\src", isDirectory: true));
        Assert.Equal(GitState.Modified, status.StateOf(@"D:\Work\repo\src\app", isDirectory: true));
        Assert.Equal(GitState.Added, status.StateOf(@"D:\Work\repo\docs", isDirectory: true));
        Assert.Equal(GitState.Conflicted, status.StateOf(Root, isDirectory: true));
    }

    [Fact]
    public void UntrackedAndIgnoredFolders_PassTheirStateToWhatIsInside()
    {
        var status = Sample();

        Assert.Equal(GitState.Untracked, status.StateOf(@"D:\Work\repo\scratch", isDirectory: true));
        Assert.Equal(GitState.Untracked, status.StateOf(@"D:\Work\repo\scratch\deep\x.txt", isDirectory: false));
        Assert.Equal(GitState.Ignored, status.StateOf(@"D:\Work\repo\bin\Debug\x.dll", isDirectory: false));
    }

    [Fact]
    public void IgnoredItems_DoNotMakeTheirFoldersLookChanged_NorCount()
    {
        var status = GitRepoStatus.Parse(Root, Output("# branch.head dev", "! out/a.o"));

        Assert.Equal(GitState.Clean, status.StateOf(Root, isDirectory: true));
        Assert.Equal(0, status.ChangedCount);
    }

    [Fact]
    public void PathsAreCaseInsensitive_AsOnWindows() =>
        Assert.Equal(GitState.Modified, Sample().StateOf(@"d:\work\REPO\SRC\app\program.cs", false));

    [Fact]
    public void UnknownLines_AndEmptyOutput_AreHarmless()
    {
        Assert.Equal(0, GitRepoStatus.Parse(Root, string.Empty).ChangedCount);
        Assert.Equal(0, GitRepoStatus.Parse(Root, Output("# something.new x", "9 weird line", "x")).ChangedCount);
    }

    [Fact]
    public void ADetachedHead_IsNamedAsGitSaysIt() =>
        Assert.Equal("(detached)", GitRepoStatus.Parse(Root, Output("# branch.head (detached)")).Branch);

    [Fact]
    public void Letters_AreOneCharacter_AndCleanHasNone()
    {
        Assert.Equal("M", GitStates.Letter(GitState.Modified));
        Assert.Equal("?", GitStates.Letter(GitState.Untracked));
        Assert.Equal(string.Empty, GitStates.Letter(GitState.Clean));
        Assert.Equal("Not tracked", GitStates.Title(GitState.Untracked));
    }

    [Fact]
    public void TheRepositoryIsTheNearestFolderWithADotGit()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(@"D:\Work\repo\.git");
        fs.AddDirectory(@"D:\Work\repo\src\app");
        fs.AddFile(@"D:\Work\repo\vendor\lib\.git", new MockFileData("gitdir: ../../.git/modules/lib")); // a submodule: .git is a file
        fs.AddDirectory(@"D:\Work\repo\vendor\lib\x");

        Assert.Equal(@"D:\Work\repo", GitRepoLocator.FindRoot(fs, @"D:\Work\repo\src\app"));
        Assert.Equal(@"D:\Work\repo", GitRepoLocator.FindRoot(fs, @"D:\Work\repo\"));
        Assert.Equal(@"D:\Work\repo\vendor\lib", GitRepoLocator.FindRoot(fs, @"D:\Work\repo\vendor\lib\x"));
        Assert.Null(GitRepoLocator.FindRoot(fs, @"D:\Other"));
        Assert.Null(GitRepoLocator.FindRoot(fs, @"D:\"));
    }
}
