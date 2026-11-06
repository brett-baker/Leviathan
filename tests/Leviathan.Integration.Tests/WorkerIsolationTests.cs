using Leviathan.Ipc;

namespace Leviathan.Integration.Tests;

/// <summary>
/// A run with its own data folder (a UI test, a portable copy) must get its own worker pipe, mutex and shell host: otherwise it
/// would send its jobs to the worker the user is working with, and a stray key could delete the user's real files (Phase 2 G5).
/// </summary>
public sealed class WorkerIsolationTests
{
    [Fact]
    public void UserHash_WithoutADataFolder_IsTheUsersAlone()
    {
        Assert.Equal(PipeNaming.UserHash(null), PipeNaming.UserHash(""));
    }

    [Fact]
    public void UserHash_WithADataFolder_DiffersFromTheUsers_AndFromAnotherFolder()
    {
        var plain = PipeNaming.UserHash(null);
        var one = PipeNaming.UserHash(@"C:\Temp\leviathan-ui-1");
        var two = PipeNaming.UserHash(@"C:\Temp\leviathan-ui-2");

        Assert.NotEqual(plain, one);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void UserHash_SameFolderWrittenDifferently_IsTheSame()
    {
        Assert.Equal(PipeNaming.UserHash(@"C:\Temp\Data"), PipeNaming.UserHash(@"c:\temp\data\"));
    }
}
