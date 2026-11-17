using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

public sealed class RenameFlowTests
{
    private static readonly LocationPath B = LocationPath.Parse(@"C:\b");

    [Fact]
    public void NextRow_ItemInTheMiddle_ReturnsTheFollowingRow()
    {
        Assert.Equal("c", RenameFlow.NextRow<string>(["a", "b", "c"], s => LocationPath.Parse(@"C:\" + s), B));
    }

    [Fact]
    public void NextRow_LastItem_ReturnsNull()
    {
        Assert.Null(RenameFlow.NextRow<string>(["a", "b"], s => LocationPath.Parse(@"C:\" + s), LocationPath.Parse(@"C:\b")));
    }

    [Fact]
    public void NextRow_ItemNotListed_ReturnsNull()
    {
        Assert.Null(RenameFlow.NextRow<string>(["a"], s => LocationPath.Parse(@"C:\" + s), LocationPath.Parse(@"C:\zzz")));
    }
}
