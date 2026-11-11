using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

/// <summary>Opening the space map from the keyboard, the palette and the context menu (spec §14).</summary>
public sealed class SpaceMapCommandTests
{
    [Fact]
    public async Task SpaceMapCommand_OpensTheFolderShown_AndHasCtrlShiftM()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SpaceMap);

        Assert.Equal([LocationPath.Parse(@"C:\a")], h.Shell.SpaceMaps);
        Assert.True(h.Commands.TryGet(CommandIds.SpaceMap, out var command));
        Assert.Equal("Ctrl+Shift+M", command.Definition.DefaultShortcut);
    }

    [Fact]
    public async Task SpaceMapCommand_WithATarget_OpensThatFolder()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SpaceMap, @"\\nas\media\inbox");

        Assert.Equal([LocationPath.Parse(@"\\nas\media\inbox")], h.Shell.SpaceMaps);
    }

    [Fact]
    public async Task SpaceMapCommand_OnAServerWithNoShareOrASearchPage_SaysWhatToDo()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.SpaceMap, @"\\nas");
        await h.Run(CommandIds.SearchRun, "holiday");
        await h.Run(CommandIds.SpaceMap);

        Assert.Empty(h.Shell.SpaceMaps);
        Assert.All(h.Shell.Messages.TakeLast(2), m => Assert.Contains("space map", m, StringComparison.Ordinal));
    }
}
