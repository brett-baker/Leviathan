using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Sessions;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>Saved inboxes, new arrivals and sessions of N (spec §10.1 "Working at scale").</summary>
public sealed class TriageInboxTests
{
    private const string Store = @"C:\data\inboxes.json";

    private static FileEntry Video(string path, long size = 1000, int day = 1)
    {
        var location = LocationPath.Parse(path);
        var when = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero);
        return new FileEntry(location.Name, location, FileEntryKind.File, size, when, when, FileAttributes.Normal);
    }

    private static InboxCatalog NewCatalog(MockFileSystem? disk = null) =>
        new(new JsonFileStore<InboxesFile>(disk ?? new MockFileSystem(), Store));

    private static FakeLocationProvider Folder(params string[] names)
    {
        var provider = new FakeLocationProvider();
        foreach (var name in names)
        {
            provider.AddFile($@"Z:\inbox\{name}", 1000);
        }

        return provider;
    }

    [Fact]
    public async Task Scan_FirstRead_IsTheBaseline_NothingIsNew()
    {
        var catalog = NewCatalog();
        var inbox = await catalog.AddAsync("NAS inbox", @"Z:\inbox", true, TriageOrder.Oldest, 1000, TestContext.Current.CancellationToken);

        var scan = await catalog.ScanAsync(inbox, Folder("a.mkv", "b.mkv"), TestContext.Current.CancellationToken);

        Assert.Equal(2, scan.All.Count);
        Assert.Equal(0, scan.Arrived);
        Assert.Empty(inbox.New);
    }

    [Fact]
    public async Task Scan_LaterRead_AppendsArrivalsAndMarksThemNew()
    {
        var catalog = NewCatalog();
        var inbox = await catalog.AddAsync("NAS inbox", @"Z:\inbox", true, TriageOrder.Oldest, 1000, TestContext.Current.CancellationToken);
        await catalog.ScanAsync(inbox, Folder("a.mkv"), TestContext.Current.CancellationToken);

        var scan = await catalog.ScanAsync(inbox, Folder("a.mkv", "b.mkv", "c.mp4"), TestContext.Current.CancellationToken);

        Assert.Equal(2, scan.Arrived);
        Assert.Equal([@"Z:\inbox\b.mkv", @"Z:\inbox\c.mp4"], inbox.New.Order());
    }

    [Fact]
    public async Task Scan_IgnoresFilesThatAreNotVideos()
    {
        var catalog = NewCatalog();
        var inbox = await catalog.AddAsync("x", @"Z:\inbox", true, TriageOrder.Oldest, 10, TestContext.Current.CancellationToken);

        var scan = await catalog.ScanAsync(inbox, Folder("a.mkv", "notes.txt", "cover.jpg"), TestContext.Current.CancellationToken);

        Assert.Single(scan.All);
    }

    [Fact]
    public void NextSession_SkipsDecidedFiles_AndTakesOnlyN()
    {
        var inbox = new TriageInbox { BatchSize = 2, Order = TriageOrder.Oldest };
        inbox.Decided.Add(@"Z:\inbox\a.mkv");
        var scan = new InboxScan([Video(@"Z:\inbox\a.mkv", day: 1), Video(@"Z:\inbox\b.mkv", day: 2), Video(@"Z:\inbox\c.mkv", day: 3), Video(@"Z:\inbox\d.mkv", day: 4)], 0);

        var session = InboxCatalog.NextSession(inbox, scan);

        Assert.Equal(["b.mkv", "c.mkv"], session.Queue.Select(f => f.Name));
        Assert.Equal(4, session.Total);
        Assert.Equal(1, session.Triaged);
    }

    [Theory]
    [InlineData(TriageOrder.Oldest, "a,b,c")]
    [InlineData(TriageOrder.Newest, "c,b,a")]
    [InlineData(TriageOrder.Largest, "b,c,a")]
    public void NextSession_FollowsTheOrder(TriageOrder order, string expected)
    {
        var inbox = new TriageInbox { Order = order };
        var scan = new InboxScan([Video(@"Z:\i\c.mkv", 500, 3), Video(@"Z:\i\a.mkv", 100, 1), Video(@"Z:\i\b.mkv", 900, 2)], 0);

        var session = InboxCatalog.NextSession(inbox, scan);

        Assert.Equal(expected, string.Join(",", session.Queue.Select(f => Path.GetFileNameWithoutExtension(f.Name))));
    }

    [Fact]
    public void NextSession_Random_ShufflesButKeepsEveryFile()
    {
        var inbox = new TriageInbox { Order = TriageOrder.Random };
        var files = Enumerable.Range(0, 50).Select(i => Video($@"Z:\i\{i:D2}.mkv", day: 1 + i % 28)).ToList();

        var first = InboxCatalog.NextSession(inbox, new InboxScan(files, 0), new Random(1)).Queue;
        var second = InboxCatalog.NextSession(inbox, new InboxScan(files, 0), new Random(2)).Queue;

        Assert.Equal(50, first.Count);
        Assert.Equal(files.Select(f => f.Name).Order(), first.Select(f => f.Name).Order());
        Assert.NotEqual(first.Select(f => f.Name), second.Select(f => f.Name));
    }

    [Fact]
    public async Task Record_RemembersKeptAndDiscarded_NotSkipped_AndSurvivesARestart()
    {
        var disk = new MockFileSystem();
        var catalog = NewCatalog(disk);
        var inbox = await catalog.AddAsync("x", @"Z:\inbox", true, TriageOrder.Oldest, 10, TestContext.Current.CancellationToken);
        await catalog.ScanAsync(inbox, Folder("a.mkv"), TestContext.Current.CancellationToken);
        await catalog.ScanAsync(inbox, Folder("a.mkv", "b.mkv", "c.mkv", "d.mkv"), TestContext.Current.CancellationToken);
        var session = new TriageSession([Video(@"Z:\inbox\a.mkv"), Video(@"Z:\inbox\b.mkv"), Video(@"Z:\inbox\c.mkv")], new FakeVideoEngine(), new NoActions(), new TriageOptions { StartOffset = 0 });
        await session.StartAsync(0, TestContext.Current.CancellationToken);
        await session.KeepAsync();
        await session.DiscardAsync();
        await session.SkipAsync();

        await catalog.RecordAsync(inbox, session, TestContext.Current.CancellationToken);

        var reopened = (await NewCatalog(disk).ListAsync(TestContext.Current.CancellationToken)).Single();
        Assert.Equal([@"Z:\inbox\a.mkv", @"Z:\inbox\b.mkv"], reopened.Decided.Order());
        Assert.Equal([@"Z:\inbox\d.mkv"], reopened.New.Order());
        Assert.Equal(4, reopened.Known.Count);
    }

    [Fact]
    public async Task Remove_DeletesTheInbox()
    {
        var catalog = NewCatalog();
        var inbox = await catalog.AddAsync("x", @"Z:\inbox", true, TriageOrder.Oldest, 10, TestContext.Current.CancellationToken);

        await catalog.RemoveAsync(inbox, TestContext.Current.CancellationToken);

        Assert.Empty(await catalog.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Add_ClampsTheBatchSize()
    {
        var inbox = await NewCatalog().AddAsync("x", @"Z:\inbox", true, TriageOrder.Oldest, 0, TestContext.Current.CancellationToken);

        Assert.Equal(1, inbox.BatchSize);
    }

    [Fact]
    public void Progress_ShowsTheBatchAndTheWholeInbox()
    {
        Assert.Equal("612 / 1,000 this session · 4,212 of 31,880 triaged", InboxSession.Progress(612, 1000, 4212, 31880));
    }

    private sealed class NoActions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
