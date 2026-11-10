using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>Putting the triage queue in order by name, age or size without losing the place (spec §10.1).</summary>
public sealed class TriageSortTests
{
    private static FileEntry Video(string name, long size, int day) =>
        new(name, LocationPath.Parse($@"C:\inbox\{name}"), FileEntryKind.File, size, DateTimeOffset.UnixEpoch.AddDays(day), DateTimeOffset.UnixEpoch.AddDays(day), FileAttributes.Normal);

    private sealed class Actions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // As opened: clip10 (500 B, day 3), clip2 (100 B, day 5), Alpha (300 B, day 1), clip1 (200 B, day 4), beta (400 B, day 2).
    private static TriageSession Make() => new(
        [Video("clip10.mkv", 500, 3), Video("clip2.mkv", 100, 5), Video("Alpha.mkv", 300, 1), Video("clip1.mkv", 200, 4), Video("beta.mkv", 400, 2)],
        new FakeVideoEngine(),
        new Actions(),
        new TriageOptions { StartOffset = 0 });

    private static string[] Names(TriageSession session) => [.. session.Items.Select(i => i.File.Name)];

    [Theory]
    [InlineData(TriageListOrder.AsOpened, new[] { "clip10.mkv", "clip2.mkv", "Alpha.mkv", "clip1.mkv", "beta.mkv" })]
    [InlineData(TriageListOrder.NameAscending, new[] { "Alpha.mkv", "beta.mkv", "clip1.mkv", "clip2.mkv", "clip10.mkv" })]
    [InlineData(TriageListOrder.NameDescending, new[] { "clip10.mkv", "clip2.mkv", "clip1.mkv", "beta.mkv", "Alpha.mkv" })]
    [InlineData(TriageListOrder.OldestFirst, new[] { "Alpha.mkv", "beta.mkv", "clip10.mkv", "clip1.mkv", "clip2.mkv" })]
    [InlineData(TriageListOrder.NewestFirst, new[] { "clip2.mkv", "clip1.mkv", "clip10.mkv", "beta.mkv", "Alpha.mkv" })]
    [InlineData(TriageListOrder.LargestFirst, new[] { "clip10.mkv", "beta.mkv", "Alpha.mkv", "clip1.mkv", "clip2.mkv" })]
    [InlineData(TriageListOrder.SmallestFirst, new[] { "clip2.mkv", "clip1.mkv", "Alpha.mkv", "beta.mkv", "clip10.mkv" })]
    public async Task BeforeTheStart_EveryOrder_SortsTheWholeQueue(TriageListOrder order, string[] expected)
    {
        var session = Make();

        await session.SortAsync(order);

        Assert.Equal(expected, Names(session));
        Assert.Equal(order, session.Order);
    }

    [Fact]
    public async Task Sorting_KeepsTheFileOnScreen_AndPutsTheOthersAfterIt()
    {
        var session = Make();
        await session.StartAsync(startIndex: 2); // Alpha.mkv

        await session.SortAsync(TriageListOrder.LargestFirst);

        Assert.Equal("Alpha.mkv", session.Current!.File.Name);
        Assert.Equal(0, session.Index);
        Assert.Equal(["Alpha.mkv", "clip10.mkv", "beta.mkv", "clip1.mkv", "clip2.mkv"], Names(session));
    }

    [Fact]
    public async Task WhatWasDecided_KeepsItsPlaceAtTheTop_InItsOwnOrder()
    {
        var session = Make();
        await session.StartAsync();
        await session.KeepAsync();      // clip10 kept (no auto-advance delay configured: moves on at once)
        await session.SkipAsync();      // clip2 skipped, now on Alpha
        Assert.Equal("Alpha.mkv", session.Current!.File.Name);

        await session.SortAsync(TriageListOrder.NameAscending);

        Assert.Equal(["clip10.mkv", "clip2.mkv", "Alpha.mkv", "beta.mkv", "clip1.mkv"], Names(session));
        Assert.Equal(2, session.Index);
        Assert.Equal("Alpha.mkv", session.Current!.File.Name);
    }

    [Fact]
    public async Task AfterSorting_NextIsTheNextOfTheNewOrder()
    {
        var session = Make();
        await session.StartAsync();                        // clip10
        await session.SortAsync(TriageListOrder.SmallestFirst);

        await session.SkipAsync();

        Assert.Equal("clip10.mkv", session.Items[0].File.Name);
        Assert.Equal("clip2.mkv", session.Current!.File.Name); // the smallest of the rest, not the file after clip10 in the old order
    }

    [Fact]
    public async Task GoingBackToTheAsOpenedOrder_PutsTheUndecidedFilesBackAsTheyWere()
    {
        var session = Make();
        await session.StartAsync();
        await session.SortAsync(TriageListOrder.NameAscending);

        await session.SortAsync(TriageListOrder.AsOpened);

        Assert.Equal(["clip10.mkv", "clip2.mkv", "Alpha.mkv", "clip1.mkv", "beta.mkv"], Names(session));
    }

    [Fact]
    public async Task Sorting_RaisesOrderChanged_OncePerSort()
    {
        var session = Make();
        var raised = 0;
        session.OrderChanged += (_, _) => raised++;

        await session.SortAsync(TriageListOrder.NameAscending);
        await session.SortAsync(TriageListOrder.LargestFirst);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task SortingAFinishedOrEmptyQueue_IsHarmless()
    {
        var empty = new TriageSession([], new FakeVideoEngine(), new Actions(), new TriageOptions { StartOffset = 0 });
        await empty.StartAsync();

        await empty.SortAsync(TriageListOrder.NameAscending);

        Assert.True(empty.IsFinished);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task EveryOrder_HasAName_AndTheSettingIsCleanedToARealOrder()
    {
        Assert.All(TriageListOrdering.All, order => Assert.False(string.IsNullOrWhiteSpace(TriageListOrdering.Title(order))));
        Assert.Equal(TriageListOrder.AsOpened, new MediaSettings { TriageOrder = (TriageListOrder)99 }.Clamped().TriageOrder);
        Assert.Equal(TriageListOrder.LargestFirst, new MediaSettings { TriageOrder = TriageListOrder.LargestFirst }.Clamped().TriageOrder);
        await Task.CompletedTask;
    }
}
