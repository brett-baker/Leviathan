using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Tests;

public sealed class MoveTitlesTests
{
    private static ItemMove M(string from, string to) => new(from, to);

    [Fact]
    public void Move_OneItem_NamesTheItemAndWhereItWent() =>
        Assert.Equal(@"Move a.mkv to C:\keep", MoveTitles.Move([M(@"C:\in\a.mkv", @"C:\keep\a.mkv")]));

    [Fact]
    public void Move_ManyItems_CountsThemAndNamesTheFolder() =>
        Assert.Equal(@"Move 2 items to C:\x", MoveTitles.Move([M(@"C:\a", @"C:\x\b"), M(@"C:\c", @"C:\x\d")]));

    [Fact]
    public void Keep_SaysWhereTheFileWentOnTheNas() =>
        Assert.Equal(@"Keep: move a.mkv to \\nas\media\Renamed", MoveTitles.Keep([M(@"\\nas\media\Renamed\Working\a.mkv", @"\\nas\media\Renamed\a.mkv")]));

    [Fact]
    public void Undo_SaysUndo_NotRestore_AndWhereItWentBack() =>
        Assert.Equal(@"Undo: move a.mkv back to C:\in", MoveTitles.Undo([M(@"C:\keep\a.mkv", @"C:\in\a.mkv")]));

    [Fact]
    public void Redo_SaysRedo() =>
        Assert.Equal(@"Redo: move 2 items to C:\x", MoveTitles.Redo([M(@"C:\a", @"C:\x\b"), M(@"C:\c", @"C:\x\d")]));

    [Fact]
    public void Rename_NamesTheNewName() => Assert.Equal("Rename new.txt", MoveTitles.Rename([M(@"C:\x\old.txt", @"C:\x\new.txt")]));
}
