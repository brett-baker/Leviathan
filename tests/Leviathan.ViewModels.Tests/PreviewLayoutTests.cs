using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Tests;

public sealed class PreviewLayoutTests
{
    [Theory]
    [InlineData(100, 1600, 220)]
    [InlineData(300, 1600, 300)]
    [InlineData(2000, 1600, 900)]
    [InlineData(800, 1000, 600)]
    [InlineData(800, 300, 220)]
    public void Clamp_KeepsTheWidthBetweenTheMinimumAndTheSmallerOfTheMaximumAndSixtyPercentOfTheWindow(double width, double window, double expected) =>
        Assert.Equal(expected, PreviewLayout.Clamp(width, window));

    [Fact]
    public void Clamp_OfNotANumber_IsTheDefault() => Assert.Equal(PreviewLayout.DefaultWidth, PreviewLayout.Clamp(double.NaN, 1600));

    [Fact]
    public void HeightFor_LargeFillsThePaneWidthAtSixteenByNine_AndMediumAndSmallAreShares_OfIt()
    {
        var full = (500 - PreviewLayout.HorizontalChrome) * 9 / 16;

        Assert.Equal(full, PreviewLayout.HeightFor(VideoSize.Large, 500), 6);
        Assert.Equal(full * PreviewLayout.MediumShare, PreviewLayout.HeightFor(VideoSize.Medium, 500), 6);
        Assert.Equal(full * PreviewLayout.SmallShare, PreviewLayout.HeightFor(VideoSize.Small, 500), 6);
    }

    [Theory]
    [InlineData(220)]
    [InlineData(340)]
    [InlineData(600)]
    public void HeightFor_ThePresetsAreInOrder_SmallThenMediumThenLarge(double width)
    {
        var small = PreviewLayout.HeightFor(VideoSize.Small, width);
        var medium = PreviewLayout.HeightFor(VideoSize.Medium, width);
        var large = PreviewLayout.HeightFor(VideoSize.Large, width);

        Assert.True(small <= medium && medium <= large);
        Assert.True(large > small);
    }

    [Fact]
    public void HeightFor_Large_StaysInsideTheHeightLimits()
    {
        Assert.Equal(PreviewLayout.MinVideoHeight, PreviewLayout.HeightFor(VideoSize.Large, 20));
        Assert.Equal(PreviewLayout.MaxVideoHeight, PreviewLayout.HeightFor(VideoSize.Large, 100_000));
    }

    [Theory]
    [InlineData(10, 80)]
    [InlineData(240, 240)]
    [InlineData(5000, 900)]
    public void ClampHeight_KeepsADraggedHeightInRange(double height, double expected) => Assert.Equal(expected, PreviewLayout.ClampHeight(height));

    [Fact]
    public void ClampHeight_OfNotANumber_IsTheMediumHeight() => Assert.Equal(PreviewLayout.HeightFor(VideoSize.Medium, PreviewLayout.DefaultWidth), PreviewLayout.ClampHeight(double.NaN));

    [Fact]
    public void ANewWindow_HasTheDefaultPane()
    {
        var window = new WindowManager(TabTestSupport.Services()).OpenWindow(LocationPath.Parse(@"C:\a"));

        Assert.Equal(PreviewLayout.DefaultWidth, window.PreviewWidth);
        Assert.Equal(VideoSize.Medium, window.VideoSize);
        Assert.Equal(PreviewLayout.HeightFor(VideoSize.Medium, PreviewLayout.DefaultWidth), window.CurrentVideoHeight);
        Assert.False(window.PreviewCollapsed);
        Assert.False(window.DetailsCollapsed);
    }

    [Fact]
    public void ChoosingAPreset_SetsTheHeight_AndDraggingClearsThePreset()
    {
        var window = new WindowManager(TabTestSupport.Services()).OpenWindow(LocationPath.Parse(@"C:\a"));

        window.SetVideoSize(VideoSize.Small);
        Assert.Equal(PreviewLayout.HeightFor(VideoSize.Small, window.PreviewWidth), window.CurrentVideoHeight);

        window.DragVideoHeight(333);
        Assert.Null(window.VideoSize);
        Assert.Equal(333, window.CurrentVideoHeight);

        window.DragVideoHeight(5);
        Assert.Equal(PreviewLayout.MinVideoHeight, window.CurrentVideoHeight);
    }

    [Fact]
    public void TheLargePreset_FollowsThePaneWidthAsItIsResized()
    {
        var window = new WindowManager(TabTestSupport.Services()).OpenWindow(LocationPath.Parse(@"C:\a"));
        window.SetVideoSize(VideoSize.Large);
        var narrow = window.CurrentVideoHeight;

        window.PreviewWidth = 600;

        Assert.True(window.CurrentVideoHeight > narrow);
        Assert.Equal((600 - PreviewLayout.HorizontalChrome) * 9 / 16, window.CurrentVideoHeight, 6);
    }

    [Fact]
    public void TheCurrentVideoHeight_RaisesChangeNotifications()
    {
        var window = new WindowManager(TabTestSupport.Services()).OpenWindow(LocationPath.Parse(@"C:\a"));
        var seen = new List<string?>();
        window.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        window.SetVideoSize(VideoSize.Large);
        window.PreviewWidth = 500;

        Assert.Contains(nameof(WindowViewModel.CurrentVideoHeight), seen);
    }

    [Fact]
    public async Task ThePaneLayout_IsSavedInTheSession_AndComesBack()
    {
        var source = new WindowManager(TabTestSupport.Services());
        var window = source.OpenWindow(LocationPath.Parse(@"C:\a"));
        window.PreviewWidth = 410;
        window.DragVideoHeight(250);
        window.PreviewCollapsed = true;
        window.DetailsCollapsed = true;
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        var restored = target.Windows[0];
        Assert.Equal(410, restored.PreviewWidth);
        Assert.Null(restored.VideoSize);
        Assert.Equal(250, restored.CurrentVideoHeight);
        Assert.True(restored.PreviewCollapsed);
        Assert.True(restored.DetailsCollapsed);
    }

    [Fact]
    public async Task APresetIsSavedAndComesBack()
    {
        var source = new WindowManager(TabTestSupport.Services());
        source.OpenWindow(LocationPath.Parse(@"C:\a")).SetVideoSize(VideoSize.Large);
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        Assert.Equal(VideoSize.Large, target.Windows[0].VideoSize);
    }

    [Fact]
    public async Task ASessionSavedBeforeThePaneHadSizes_OpensWithTheDefaults()
    {
        var fs = new MockFileSystem();
        using var store = new JsonFileStore<SessionFile>(fs, @"C:\data\session.json");
        var source = new WindowManager(TabTestSupport.Services());
        source.OpenWindow(LocationPath.Parse(@"C:\a"));
        await store.SaveAsync(new SessionFile(1, DateTimeOffset.UnixEpoch, SessionCapture.Capture(source)), TestContext.Current.CancellationToken);
        var text = fs.File.ReadAllText(@"C:\data\session.json");
        var start = text.IndexOf("\"Pane\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var old = text[..start].TrimEnd().TrimEnd(',') + "}]}}";
        fs.File.WriteAllText(@"C:\data\session.json", old);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, loaded!.State);

        Assert.Equal(PreviewLayout.DefaultWidth, target.Windows[0].PreviewWidth);
        Assert.Equal(VideoSize.Medium, target.Windows[0].VideoSize);
    }

    [Fact]
    public void ASavedPaneOutsideTheLimits_IsBroughtBackInside()
    {
        var state = new SessionState([new SessionWindow(new WindowBounds(WindowManager.DefaultRect), true, 0,
            [new Tabs.TabState(new Navigation.NavigationSnapshot([], LocationPath.Parse(@"C:\a"), []), Views.FolderViewSettings.Default, null, null, false, false, false, [])],
            Pane: new PreviewPaneState(99999, (VideoSize)42, double.NaN, false, false))]);

        var target = new WindowManager(TabTestSupport.Services());
        SessionCapture.Restore(target, state);

        var window = target.Windows[0];
        Assert.Equal(PreviewLayout.MaxWidth, window.PreviewWidth);
        Assert.Null(window.VideoSize);
        Assert.Equal(PreviewLayout.HeightFor(VideoSize.Medium, PreviewLayout.DefaultWidth), window.CurrentVideoHeight);
    }

    [Fact]
    public async Task TheSizeCommands_ChooseThePresets()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.PreviewVideoLarge);
        Assert.Equal(VideoSize.Large, h.Window.VideoSize);
        await h.Run(CommandIds.PreviewVideoSmall);
        Assert.Equal(VideoSize.Small, h.Window.VideoSize);
        await h.Run(CommandIds.PreviewVideoMedium);
        Assert.Equal(VideoSize.Medium, h.Window.VideoSize);
    }

    [Fact]
    public async Task TheQuickPreviewCommand_AsksTheShell()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.PreviewQuick);

        Assert.Equal(1, h.Shell.QuickPreviewToggles);
    }
}

public sealed class PreviewTextTests
{
    private static Leviathan.Core.Model.FileEntry File(string name) =>
        new(name, TabTestSupport.P($@"C:\a\{name}"), Leviathan.Core.Model.FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Theory]
    [InlineData(Tabs.PaneLayout.Single, 0, "Preview")]
    [InlineData(Tabs.PaneLayout.Single, 1, "Preview")]
    [InlineData(Tabs.PaneLayout.SideBySide, 0, "Preview · left pane")]
    [InlineData(Tabs.PaneLayout.SideBySide, 1, "Preview · right pane")]
    [InlineData(Tabs.PaneLayout.TopBottom, 0, "Preview · top pane")]
    [InlineData(Tabs.PaneLayout.TopBottom, 1, "Preview · bottom pane")]
    public void Title_SaysWhichPaneItFollows(Tabs.PaneLayout layout, int focused, string expected) =>
        Assert.Equal(expected, PreviewText.Title(layout, focused));

    [Fact]
    public void Placeholder_ForNothingSelected_AsksForASelection() =>
        Assert.Equal("Select a file to preview it.", PreviewText.Placeholder([]));

    [Fact]
    public void Placeholder_ForSeveralItems_CountsThem() =>
        Assert.Equal("3 items selected", PreviewText.Placeholder([File("a.txt"), File("b.txt"), File("c.txt")]));

    [Fact]
    public void Placeholder_ForAVideo_NamesItAndSaysWhatIsComing() =>
        Assert.Equal("clip.mkv\nVideo. The player arrives in the next slice.", PreviewText.Placeholder([File("clip.mkv")]));

    [Fact]
    public void Placeholder_ForAnAudioFile_NamesIt() =>
        Assert.StartsWith("song.flac\nAudio.", PreviewText.Placeholder([File("song.flac")]), StringComparison.Ordinal);

    [Fact]
    public void Placeholder_ForAFolder_NamesItAsAFolder()
    {
        var folder = new Leviathan.Core.Model.FileEntry("Pics", TabTestSupport.P(@"C:\a\Pics"), Leviathan.Core.Model.FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

        Assert.Equal("Pics\nFolder", PreviewText.Placeholder([folder]));
    }

    [Fact]
    public void Placeholder_ForAnOtherFile_IsItsName() => Assert.Equal("notes.txt", PreviewText.Placeholder([File("notes.txt")]));
}
