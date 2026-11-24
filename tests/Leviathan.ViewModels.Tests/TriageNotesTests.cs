using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

public sealed class TriageNotesTests
{
    [Fact]
    public void WithoutADestination_TheLineWarnsThatFilesAreOnlyTagged()
    {
        var line = TriageNotes.DestinationLine(null, moveOnKeep: true);

        Assert.StartsWith("Keep has no destination", line);
        Assert.True(TriageNotes.IsWarning(null));
        Assert.True(TriageNotes.IsWarning("  "));
    }

    [Fact]
    public void WithADestination_TheLineNamesIt()
    {
        Assert.Equal(@"Keep moves files to \\nas\media\Renamed", TriageNotes.DestinationLine(@"\\nas\media\Renamed ", moveOnKeep: true));
        Assert.Equal(@"Kept files are moved to D:\keep when triage ends", TriageNotes.DestinationLine(@"D:\keep", moveOnKeep: false));
        Assert.False(TriageNotes.IsWarning(@"D:\keep"));
    }

    [Fact]
    public void AKeptFileThatMoved_SaysFromWhereToWhere() =>
        Assert.Equal(@"Triage kept a.mkv: moved from D:\in to D:\keep", TriageNotes.Kept("a.mkv", @"D:\in", @"D:\keep", @"D:\keep", moveOnKeep: true));

    [Fact]
    public void AKeptFileThatStayed_WithNoDestination_SaysWhy() =>
        Assert.Equal(@"Triage kept a.mkv: tagged green and left in D:\in, because there is no destination folder", TriageNotes.Kept("a.mkv", @"D:\in", @"D:\in", null, moveOnKeep: true));

    [Fact]
    public void AKeptFileWaitingForTheEnd_NamesWhereItWillGo() =>
        Assert.Equal(@"Triage kept a.mkv: tagged green and left in D:\in; it moves to D:\keep when triage ends", TriageNotes.Kept("a.mkv", @"D:\in", @"D:\in\", @"D:\keep", moveOnKeep: false));

    [Fact]
    public void AKeptFileOnAnotherDrive_SaysTheMoveIsQueued() =>
        Assert.Contains("queued and will show in Operations", TriageNotes.Kept("a.mkv", @"D:\in", @"D:\in", @"E:\keep", moveOnKeep: true));

    [Fact]
    public void ADiscard_SaysItWentToTheBin() =>
        Assert.Equal(@"Triage discarded a.mkv from D:\in: moved to the bin (Ctrl+Z restores it)", TriageNotes.Discarded("a.mkv", @"D:\in"));
}
