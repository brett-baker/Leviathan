using Leviathan.Core.Paths;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

public sealed class CutMarksTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void Of_ACut_MarksItsItems_IgnoringCase()
    {
        var marks = CutMarks.Of(new ClipboardItems([P(@"C:\in\a.mkv")], ClipboardOperation.Cut));

        Assert.True(marks.IsCut(P(@"c:\IN\A.MKV")));
        Assert.False(marks.IsCut(P(@"C:\in\b.mkv")));
        Assert.False(marks.IsEmpty);
    }

    [Fact]
    public void Of_ACopy_MarksNothing() =>
        Assert.True(CutMarks.Of(new ClipboardItems([P(@"C:\in\a.mkv")], ClipboardOperation.Copy)).IsEmpty);

    [Fact]
    public void Of_NoClipboard_MarksNothing()
    {
        Assert.True(CutMarks.Of(null).IsEmpty);
        Assert.False(CutMarks.None.IsCut(P(@"C:\in\a.mkv")));
    }

    [Fact]
    public void IsCut_AFolderWithATrailingSlash_StillMatches() =>
        Assert.True(CutMarks.Of(new ClipboardItems([P(@"C:\in\sub\")], ClipboardOperation.Cut)).IsCut(P(@"C:\in\sub")));
}
