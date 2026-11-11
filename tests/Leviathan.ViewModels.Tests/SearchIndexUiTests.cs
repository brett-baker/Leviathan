using Leviathan.Contracts;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Search;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Hub;
using Leviathan.ViewModels.Searches;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

/// <summary>The search index as the user meets it: the command, the Background tab line, and not competing with a copy (spec §13, §12, §16.1).</summary>
public sealed class SearchIndexUiTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static JobSnapshot Job(Guid id, JobState state, string? device, JobLane lane = JobLane.Operations) => new()
    {
        Id = id,
        Kind = JobKind.Copy,
        Lane = lane,
        Title = "Copy",
        State = state,
        Priority = JobPriority.Normal,
        DeviceKey = device,
    };

    [Fact]
    public void BusyDevices_AreThoseWithARunningForegroundJob()
    {
        var busy = new BusyDevices();
        var job = Guid.NewGuid();

        busy.Apply(Job(job, JobState.Running, @"\\nas"));

        Assert.True(busy.IsBusy(P(@"\\nas\media\inbox")));
        Assert.True(busy.IsBusy(P(@"\\NAS\other")));
        Assert.False(busy.IsBusy(P(@"\\diskstation\media")));
        Assert.False(busy.IsBusy(P(@"C:\Users")));

        busy.Apply(Job(job, JobState.Completed, @"\\nas"));
        Assert.False(busy.IsBusy(P(@"\\nas\media")));
    }

    [Fact]
    public void BusyDevices_IgnoreBackgroundJobsAndQueuedOnes()
    {
        var busy = new BusyDevices();

        busy.Apply(Job(Guid.NewGuid(), JobState.Running, @"\\nas", JobLane.Background));
        busy.Apply(Job(Guid.NewGuid(), JobState.Queued, @"\\nas"));
        busy.Apply(Job(Guid.NewGuid(), JobState.Running, null));

        Assert.False(busy.IsBusy(P(@"\\nas\media")));
    }

    [Fact]
    public void BusyDevices_KnowLocalDrivesToo()
    {
        var busy = new BusyDevices();
        busy.Apply(Job(Guid.NewGuid(), JobState.Running, "D:"));

        Assert.True(busy.IsBusy(P(@"d:\videos")));
        Assert.False(busy.IsBusy(P(@"C:\videos")));
    }

    private static BackgroundView View(params IndexRootStatus[] roots) =>
        new BackgroundModel { SearchIndex = () => roots, SearchIndexPaused = () => false }.View();

    [Fact]
    public void BackgroundLine_WithNoFolders_SaysHowToStart() =>
        Assert.Contains("Index this folder for search", new BackgroundModel().View().SearchIndex, StringComparison.Ordinal);

    [Fact]
    public void BackgroundLine_SaysWhatEachFolderIsDoing()
    {
        var when = new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.Zero);

        var view = View(
            new IndexRootStatus(P(@"\\nas\media"), when, false, false, null, when.AddHours(6), null),
            new IndexRootStatus(P(@"D:\Archive"), null, true, false, new CrawlProgress(120, 54_321, 0), null, null),
            new IndexRootStatus(P(@"\\nas\home"), when, true, true, null, null, "Waiting while a job copies to the same place"),
            new IndexRootStatus(P(@"E:\x"), null, false, false, null, when, null));

        Assert.Contains(@"\\nas\media: listed 2026-11-10", view.SearchIndex, StringComparison.Ordinal);
        Assert.Contains(@"D:\Archive: listing · 120 folders, 54,321 items", view.SearchIndex, StringComparison.Ordinal);
        Assert.Contains(@"\\nas\home: held back (waiting while a job copies to the same place)", view.SearchIndex, StringComparison.Ordinal);
        Assert.Contains(@"E:\x: not listed yet", view.SearchIndex, StringComparison.Ordinal);
        Assert.True(view.SearchIndexInUse);
    }

    [Fact]
    public void BackgroundLine_WhenPaused_SaysSo()
    {
        var model = new BackgroundModel
        {
            SearchIndex = () => [new IndexRootStatus(P(@"D:\a"), null, false, false, null, null, null)],
            SearchIndexPaused = () => true,
        };

        var view = model.View();

        Assert.StartsWith("Paused · ", view.SearchIndex, StringComparison.Ordinal);
        Assert.True(view.SearchIndexPaused);
    }

    [Fact]
    public async Task IndexCommand_AsksTheWindowToToggleTheFolderShown_OnlyOnRealFolders()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        h.Shell.IndexedAfterToggle = true;
        await h.Run(CommandIds.SearchIndexToggle);
        Assert.Equal([P(@"C:\a")], h.Shell.IndexToggles);
        Assert.Contains("kept in the search index", h.Shell.Messages[^1], StringComparison.Ordinal);

        h.Shell.IndexedAfterToggle = false;
        await h.Run(CommandIds.SearchIndexToggle);
        Assert.Contains("no longer", h.Shell.Messages[^1], StringComparison.Ordinal);

        await h.Run(CommandIds.SearchRun, "holiday");
        Assert.False(h.Commands.CanExecute(CommandIds.SearchIndexToggle)); // a results page is not a folder
    }

    [Fact]
    public void ListingCache_TellsListenersWhatWasListed()
    {
        var cache = new ListingCache();
        var heard = new List<(LocationPath, int)>();
        cache.Listed += (key, entries) => heard.Add((key.Location, entries.Count));
        var now = DateTimeOffset.UtcNow;

        cache.Put(new ListingKey(P(@"C:\v"), false, false), [new FileEntry("a", P(@"C:\v\a"), FileEntryKind.File, 1, now, now, FileAttributes.Normal)], now, FolderKind.Videos);

        Assert.Equal([(P(@"C:\v"), 1)], heard);
    }
}
