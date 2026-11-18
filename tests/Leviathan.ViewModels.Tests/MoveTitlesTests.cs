using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Tests;

public sealed class MoveTitlesTests
{
    private static ItemMove M(string from, string to) => new(from, to);

    [Fact]
    public void Move_OneItem_NamesTheItem() => Assert.Equal("Move a.mkv", MoveTitles.Move([M(@"C:\in\a.mkv", @"C:\keep\a.mkv")]));

    [Fact]
    public void Move_ManyItems_CountsThem() => Assert.Equal("Move 2 items", MoveTitles.Move([M(@"C:\a", @"C:\b"), M(@"C:\c", @"C:\d")]));

    [Fact]
    public void Undo_SaysUndo_NotRestore() => Assert.Equal("Undo: move a.mkv back", MoveTitles.Undo([M(@"C:\keep\a.mkv", @"C:\in\a.mkv")]));

    [Fact]
    public void Redo_SaysRedo() => Assert.Equal("Redo: move 2 items", MoveTitles.Redo([M(@"C:\a", @"C:\b"), M(@"C:\c", @"C:\d")]));

    [Fact]
    public void Rename_NamesTheNewName() => Assert.Equal("Rename new.txt", MoveTitles.Rename([M(@"C:\x\old.txt", @"C:\x\new.txt")]));
}
