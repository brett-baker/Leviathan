using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

/// <summary>Rows follow a delete, move or copy as soon as it is asked for, and settle to the disk when the job ends (spec §3.2, §12.5).</summary>
public sealed class PendingChangesTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly LocationPath Source = LocationPath.Parse(@"C:\src");
    private static readonly LocationPath Target = LocationPath.Parse(@"C:\dst");

    private static LocationPath P(string folder, string name) => LocationPath.Parse($@"{folder}\{name}");

    private static FileEntry File(string folder, string name, long size = 1) =>
        new(name, P(folder, name), FileEntryKind.File, size, Stamp, Stamp, FileAttributes.Normal);

    private sealed class MutableProvider : ILocationProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<LocationPath, FileEntry[]> _folders = new();

        public void Set(LocationPath folder, params FileEntry[] items) => _folders[folder] = items;

        public void Add(LocationPath folder, FileEntry item) => _folders.AddOrUpdate(folder, [item], (_, old) => [.. old, item]);

        public string Name => "mutable";

        public bool CanHandle(LocationPath location) => true;

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ListingBatch([.. _folders.GetValueOrDefault(location) ?? []], true);
        }
    }

    // The inline UI context runs a refresh on the thread that finished it, so a poll can catch the list mid-change: try again.
    private static async Task UntilAsync(Func<bool> condition)
    {
        bool Check()
        {
            try
            {
                return condition();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Check())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Apply_LeavesOutHiddenItems_AndAddsPendingOnes()
    {
        var pending = new PendingChanges();
        pending.Begin(Guid.NewGuid(), [P(@"C:\src", "a")], Target, [File(@"C:\dst", "a")]);

        var inSource = pending.Apply(Source, [File(@"C:\src", "a"), File(@"C:\src", "b")]);
        var inTarget = pending.Apply(Target, [File(@"C:\dst", "x")]);

        Assert.Equal(["b"], inSource.Select(e => e.Name));
        Assert.Equal(["x", "a"], inTarget.Select(e => e.Name));
        Assert.True(inTarget[1].IsPending);
        Assert.False(inTarget[0].IsPending);
    }

    [Fact]
    public void Apply_WhenTheRealItemHasArrived_ShowsTheRealOne()
    {
        var pending = new PendingChanges();
        pending.Begin(Guid.NewGuid(), [], Target, [File(@"C:\dst", "a")]);

        var shown = pending.Apply(Target, [File(@"C:\dst", "a", size: 7)]);

        var only = Assert.Single(shown);
        Assert.False(only.IsPending);
        Assert.Equal(7, only.Size);
    }

    [Fact]
    public void Settle_ForgetsTheJob_AndTellsWhichFoldersToRead()
    {
        var pending = new PendingChanges();
        var job = Guid.NewGuid();
        var changes = new List<PendingChange>();
        pending.Changed += (_, c) => changes.Add(c);
        pending.Begin(job, [P(@"C:\src", "a")], Target, [File(@"C:\dst", "a")]);

        pending.Settle(job);

        Assert.True(pending.IsEmpty);
        Assert.Equal([false, true], changes.Select(c => c.Settled));
        Assert.All(changes, c => Assert.Equal(new[] { Source, Target }.ToHashSet(), c.Folders.ToHashSet()));
        Assert.Empty(pending.Apply(Source, []));
        Assert.Single(pending.Apply(Source, [File(@"C:\src", "a")]));
    }

    // A fast job can end before the window records it; recorded late, its rows would stay hidden or dimmed for good.
    [Fact]
    public void Begin_ForAJobThatAlreadyEnded_ChangesNothing()
    {
        var pending = new PendingChanges();
        var job = Guid.NewGuid();
        pending.Settle(job);

        pending.Begin(job, [P(@"C:\src", "a")], Target, [File(@"C:\dst", "a")]);

        Assert.True(pending.IsEmpty);
        Assert.True(pending.Knows(job));
        Assert.Single(pending.Apply(Source, [File(@"C:\src", "a")]));
    }

    [Fact]
    public void FilesSettings_ShowChangesAtOnceByDefault() =>
        Assert.Equal(Leviathan.ViewModels.Settings.UiUpdateMode.Immediate, new Leviathan.ViewModels.Settings.FilesSettings().UiUpdates);

    [Fact]
    public void Settle_OfAnUnknownJob_DoesNothing()
    {
        var pending = new PendingChanges();
        var raised = false;
        pending.Changed += (_, _) => raised = true;

        pending.Settle(Guid.NewGuid());

        Assert.False(raised);
    }

    private static async Task<(FolderViewModel Folder, MutableProvider Provider)> OpenAsync(PendingChanges pending, LocationPath at, params FileEntry[] items)
    {
        var provider = new MutableProvider();
        provider.Set(at, items);
        var folder = new FolderViewModel(new LocationProviderRegistry([provider]), new InlineUiContext(), new ManualTimeProvider(), cache: null, watcher: null, pending);
        await folder.LoadAsync(at, CancellationToken.None);
        await UntilAsync(() => folder.State is FolderLoadState.Loaded or FolderLoadState.Empty);
        return (folder, provider);
    }

    [Fact]
    public async Task Delete_RemovesTheRowAtOnce_AndAFailedJobBringsItBack()
    {
        var pending = new PendingChanges();
        var (folder, _) = await OpenAsync(pending, Source, File(@"C:\src", "a"), File(@"C:\src", "b"));
        var job = Guid.NewGuid();

        pending.Begin(job, [P(@"C:\src", "a")], null, []);

        Assert.Equal(["b"], folder.Entries.Select(e => e.Name));

        // The job failed: nothing was deleted, so the settled read puts the row back.
        pending.Settle(job);
        await UntilAsync(() => folder.Entries.Count == 2);
        folder.Dispose();
    }

    [Fact]
    public async Task Delete_WhenTheFolderIsListedAgainMidJob_TheRowStaysGone()
    {
        var pending = new PendingChanges();
        var (folder, _) = await OpenAsync(pending, Source, File(@"C:\src", "a"), File(@"C:\src", "b"));
        pending.Begin(Guid.NewGuid(), [P(@"C:\src", "a")], null, []);

        folder.RefreshInPlace();
        await Task.Delay(150);

        Assert.Equal(["b"], folder.Entries.Select(e => e.Name));
        folder.Dispose();
    }

    [Fact]
    public async Task Move_ShowsADimmedRowInTheDestination_ThenTheRealOneWhenTheJobEnds()
    {
        var pending = new PendingChanges();
        var (destination, provider) = await OpenAsync(pending, Target, File(@"C:\dst", "x"));
        var job = Guid.NewGuid();

        pending.Begin(job, [P(@"C:\src", "a")], Target, [File(@"C:\dst", "a")]);

        Assert.Equal(["x", "a"], destination.Entries.Select(e => e.Name));
        Assert.True(destination.Entries.Single(e => e.Name == "a").IsPending);

        // The job puts the real file there and ends: the dimmed row becomes the real one.
        provider.Add(Target, File(@"C:\dst", "a", size: 9));
        pending.Settle(job);
        await UntilAsync(() => destination.Entries.Any(e => e.Name == "a" && !e.IsPending));
        Assert.Equal(2, destination.Entries.Count);
        destination.Dispose();
    }

    [Fact]
    public async Task Copy_WhenTheJobFails_TheDimmedRowGoesAway()
    {
        var pending = new PendingChanges();
        var (destination, _) = await OpenAsync(pending, Target);
        var job = Guid.NewGuid();
        pending.Begin(job, [], Target, [File(@"C:\dst", "a")]);
        Assert.Single(destination.Entries);

        pending.Settle(job);

        await UntilAsync(() => destination.Entries.Count == 0);
        destination.Dispose();
    }

    [Fact]
    public async Task Navigating_IntoTheDestinationMidJob_ShowsTheDimmedRows()
    {
        var pending = new PendingChanges();
        pending.Begin(Guid.NewGuid(), [], Target, [File(@"C:\dst", "a")]);

        var (destination, _) = await OpenAsync(pending, Target, File(@"C:\dst", "x"));

        await UntilAsync(() => destination.Entries.Any(e => e.IsPending));
        Assert.Equal(2, destination.Entries.Count);
        destination.Dispose();
    }

    [Fact]
    public async Task AFolderThatIsNotAffected_IsLeftAlone()
    {
        var pending = new PendingChanges();
        var (folder, _) = await OpenAsync(pending, Source, File(@"C:\src", "a"));
        var changes = 0;
        folder.Entries.CollectionChanged += (_, _) => changes++;

        pending.Begin(Guid.NewGuid(), [P(@"C:\other", "z")], LocationPath.Parse(@"C:\elsewhere"), [File(@"C:\elsewhere", "z")]);

        Assert.Equal(0, changes);
        folder.Dispose();
    }
}
