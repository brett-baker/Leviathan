using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

public sealed class AppKeyBindingsTests
{
    [Fact]
    public void SignatureOf_SameBindingsInAnyOrder_AreEqual()
    {
        var a = new Dictionary<string, string> { ["x"] = "Ctrl+1", ["y"] = "Ctrl+2" };
        var b = new Dictionary<string, string> { ["y"] = "Ctrl+2", ["x"] = "Ctrl+1" };
        Assert.Equal(AppKeyBindings.SignatureOf(a), AppKeyBindings.SignatureOf(b));
    }

    [Fact]
    public void SignatureOf_ChangedKey_Differs()
    {
        Assert.NotEqual(
            AppKeyBindings.SignatureOf(new Dictionary<string, string> { ["x"] = "Ctrl+1" }),
            AppKeyBindings.SignatureOf(new Dictionary<string, string> { ["x"] = "Ctrl+2" }));
    }

    [Fact]
    public void Build_NoBindings_GivesTheDefaultsAndNoProblems()
    {
        var result = AppKeyBindings.Build(new Dictionary<string, string>());
        Assert.Empty(result.Problems);
        Assert.NotEmpty(result.Keys.Bindings);
    }

    [Fact]
    public void Build_UnknownCommand_IsReportedAsAProblem()
    {
        var result = AppKeyBindings.Build(new Dictionary<string, string> { ["no.such.command"] = "Ctrl+9" });
        Assert.NotEmpty(result.Problems);
    }
}
