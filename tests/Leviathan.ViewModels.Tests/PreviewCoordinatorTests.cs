using Leviathan.Core.Model;
using Leviathan.Testing;
using Leviathan.ViewModels.Preview;

namespace Leviathan.ViewModels.Tests;

public sealed class PreviewClassifierTests
{
    private static FileEntry File(string name) =>
        new(name, TabTestSupport.P($@"C:\a\{name}"), FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static FileEntry Folder(string name) =>
        new(name, TabTestSupport.P($@"C:\a\{name}"), FileEntryKind.Folder, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Directory);

    [Theory]
    [InlineData("a.mkv", PreviewKind.Video)]
    [InlineData("A.MP4", PreviewKind.Video)]
    [InlineData("a.flac", PreviewKind.Audio)]
    [InlineData("a.jpg", PreviewKind.Image)]
    [InlineData("a.HEIC", PreviewKind.Image)]
    [InlineData("a.cr2", PreviewKind.Image)]
    [InlineData("a.pdf", PreviewKind.Pdf)]
    [InlineData("a.md", PreviewKind.Markdown)]
    [InlineData("a.html", PreviewKind.Html)]
    [InlineData("a.docx", PreviewKind.Office)]
    [InlineData("a.xlsx", PreviewKind.Office)]
    [InlineData("a.zip", PreviewKind.Archive)]
    [InlineData("a.7z", PreviewKind.Archive)]
    [InlineData("a.ttf", PreviewKind.Font)]
    [InlineData("a.cs", PreviewKind.Text)]
    [InlineData("a.json", PreviewKind.Text)]
    [InlineData("a.txt", PreviewKind.Text)]
    [InlineData("a.unknownext", PreviewKind.Other)]
    [InlineData("noextension", PreviewKind.Other)]
    public void Classify_OfOneFile_GoesByExtension_IgnoringCase(string name, PreviewKind expected) =>
        Assert.Equal(expected, PreviewClassifier.Classify([File(name)]));

    [Fact]
    public void Classify_OfAFolder_IsFolder_WhateverItsName() =>
        Assert.Equal(PreviewKind.Folder, PreviewClassifier.Classify([Folder("movie.mkv")]));

    [Fact]
    public void Classify_OfNothing_IsNone() => Assert.Equal(PreviewKind.None, PreviewClassifier.Classify([]));

    [Fact]
    public void Classify_OfSeveralItems_IsMultiple_EvenWhenTheyAreAllVideos() =>
        Assert.Equal(PreviewKind.Multiple, PreviewClassifier.Classify([File("a.mkv"), File("b.mkv")]));

    [Fact]
    public void Classify_OfNull_Throws() => Assert.Throws<ArgumentNullException>(() => PreviewClassifier.Classify((IReadOnlyList<FileEntry>)null!));
}

public sealed class PreviewCoordinatorTests
{
    private static FileEntry File(string name) =>
        new(name, TabTestSupport.P($@"C:\a\{name}"), FileEntryKind.File, 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private sealed class Probe
    {
        private readonly List<PreviewRequest> _seen = [];
        private TaskCompletionSource<PreviewRequest> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Probe(PreviewCoordinator coordinator) => coordinator.Requested += (_, r) =>
        {
            lock (_seen)
            {
                _seen.Add(r);
            }

            _next.TrySetResult(r);
        };

        public IReadOnlyList<PreviewRequest> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public Task<PreviewRequest> Next => _next.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Rearm() => _next = new TaskCompletionSource<PreviewRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    public async Task Select_AfterTheRestPeriod_AsksForThePreview()
    {
        var time = new ManualTimeProvider();
        using var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);

        coordinator.Select([File("a.mkv")]);
        Assert.Empty(probe.Seen);
        time.Advance(TimeSpan.FromMilliseconds(150));
        var request = await probe.Next;

        Assert.Equal(PreviewKind.Video, request.Kind);
        Assert.Equal("a.mkv", request.Entries[0].Name);
        Assert.False(request.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Select_BeforeTheRestPeriodEnds_AsksForNothing()
    {
        var time = new ManualTimeProvider();
        using var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);

        coordinator.Select([File("a.mkv")]);
        time.Advance(TimeSpan.FromMilliseconds(149));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(probe.Seen);
    }

    [Fact]
    public async Task AFastRunOfSelections_LoadsOnlyTheLastOne()
    {
        var time = new ManualTimeProvider();
        using var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);

        coordinator.Select([File("a.mkv")]);
        time.Advance(TimeSpan.FromMilliseconds(100));
        coordinator.Select([File("b.jpg")]);
        time.Advance(TimeSpan.FromMilliseconds(100));
        coordinator.Select([File("c.txt")]);
        time.Advance(TimeSpan.FromMilliseconds(150));
        var request = await probe.Next;

        Assert.Equal("c.txt", request.Entries[0].Name);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(probe.Seen);
    }

    [Fact]
    public async Task ANewSelection_CancelsTheTokenOfThePreviousRequest()
    {
        var time = new ManualTimeProvider();
        using var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);
        coordinator.Select([File("a.mkv")]);
        time.Advance(TimeSpan.FromMilliseconds(150));
        var first = await probe.Next;
        probe.Rearm();

        coordinator.Select([File("b.mkv")]);

        Assert.True(first.Cancellation.IsCancellationRequested);
        time.Advance(TimeSpan.FromMilliseconds(150));
        var second = await probe.Next;
        Assert.False(second.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void Select_TellsThePaneAtOnce_ThatTheSelectionMoved()
    {
        using var coordinator = new PreviewCoordinator(new ManualTimeProvider());
        var moved = 0;
        coordinator.Moved += (_, _) => moved++;

        coordinator.Select([File("a.mkv")]);
        coordinator.Select([]);

        Assert.Equal(2, moved);
    }

    [Fact]
    public async Task Cancel_StopsAPendingPreview_AndCancelsTheOneShowing()
    {
        var time = new ManualTimeProvider();
        using var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);
        coordinator.Select([File("a.mkv")]);
        time.Advance(TimeSpan.FromMilliseconds(150));
        var shown = await probe.Next;

        coordinator.Cancel();
        coordinator.Select([File("b.mkv")]);
        coordinator.Cancel();
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(shown.Cancellation.IsCancellationRequested);
        Assert.Single(probe.Seen);
    }

    [Fact]
    public async Task AfterDispose_NothingIsRequested()
    {
        var time = new ManualTimeProvider();
        var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);
        coordinator.Select([File("a.mkv")]);

        coordinator.Dispose();
        coordinator.Select([File("b.mkv")]);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(probe.Seen);
    }

    [Fact]
    public async Task ASelectionOfSeveralItems_IsRequestedAsMultiple()
    {
        var time = new ManualTimeProvider();
        using var coordinator = new PreviewCoordinator(time);
        var probe = new Probe(coordinator);

        coordinator.Select([File("a.mkv"), File("b.mkv")]);
        time.Advance(TimeSpan.FromMilliseconds(150));
        var request = await probe.Next;

        Assert.Equal(PreviewKind.Multiple, request.Kind);
        Assert.Equal(2, request.Entries.Count);
    }
}
