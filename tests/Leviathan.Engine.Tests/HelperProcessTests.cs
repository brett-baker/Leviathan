using Leviathan.Engine.Supervision;

namespace Leviathan.Engine.Tests;

public sealed class HelperProcessTests
{
    [Fact]
    public void CommandFor_ExePresent_RunsTheExe()
    {
        var command = HelperProcess.CommandFor("Leviathan.Media", @"C:\app", p => p == @"C:\app\Leviathan.Media.exe");
        Assert.Equal(@"C:\app\Leviathan.Media.exe", command.Path);
        Assert.Null(command.Arguments);
    }

    [Fact]
    public void CommandFor_ExeMissing_RunsTheDllThroughDotnet()
    {
        var command = HelperProcess.CommandFor("Leviathan.ShellHost", @"C:\app", _ => false);
        Assert.Equal("dotnet", command.Path);
        Assert.Equal(@"""C:\app\Leviathan.ShellHost.dll""", command.Arguments);
    }

    [Theory]
    [InlineData(SupervisedState.Running, "Running", "what it does", false)]
    [InlineData(SupervisedState.Restarting, "Restarting after a crash", "what it does", false)]
    [InlineData(SupervisedState.Stopped, "Stopped until it is needed", "what it does", false)]
    [InlineData(SupervisedState.Disabled, "Switched off", "crashed 3 times", true)]
    public void Describe_EachState_GivesTheWordsOfTheBackgroundTab(SupervisedState state, string label, string detail, bool canReenable)
    {
        var result = HelperProcess.Describe(state, "crashed 3 times", "what it does");
        Assert.Equal((label, detail, canReenable), result);
    }

    [Fact]
    public void Describe_DisabledWithoutReason_FallsBackToThePurpose()
    {
        Assert.Equal("what it does", HelperProcess.Describe(SupervisedState.Disabled, null, "what it does").Detail);
    }
}
