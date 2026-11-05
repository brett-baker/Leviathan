using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Views;

namespace Leviathan.ViewModels.Tests;

public sealed class ListingCacheTests
{
    private sealed class MemoryStore : IListingStore
    {
        public Dictionary<ListingKey, CachedListing> Saved { get; } = [];

        public List<LocationPath> Removed { get; } = [];

        public bool Fail { get; set; }

        public Task<CachedListing?> LoadAsync(ListingKey key, CancellationToken cancellationToken) =>
            Fail ? throw new IOException("disk gone") : Task.FromResult(Saved.GetValueOrDefault(key));

        public Task SaveAsync(ListingKey key, CachedListing listing, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("disk gone");
            }

            Saved[key] = listing;
            return Task.CompletedTask;
        }

        public List<(ListingKey Key, DateTimeOffset At)> Touched { get; } = [];

        public Task TouchAsync(ListingKey key, DateTimeOffset listedAt, CancellationToken cancellationToken)
        {
            Touched.Add((key, listedAt));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(LocationPath folder, bool includeDescendants, CancellationToken cancellationToken)
        {
            Removed.Add(folder);
            foreach (var key in Saved.Keys.Where(k => k.Location == folder || (includeDescendants && folder.Contains(k.Location))).ToList())
            {
                Saved.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static ListingKey Key(string folder, bool hidden = false) => new(P(folder), hidden, false);

    private static List<FileEntry> Files(string folder, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new FileEntry($"f{i}.txt", P($@"{folder}\f{i}.txt"), FileEntryKind.File, 1, Stamp, Stamp, FileAttributes.Normal))];

    [Fact]
    public async Task APutListing_IsFoundAgain_WithItsTimeAndKind()
    {
        var cache = new ListingCache();
        cache.Put(Key(@"C:\a"), Files(@"C:\a", 3), Stamp, FolderKind.Videos);

        var found = await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None);

        Assert.Equal(3, found!.Entries.Count);
        Assert.Equal((Stamp, FolderKind.Videos, CacheSource.Memory), (found.ListedAt, found.Kind, found.Source));
        Assert.Null(await cache.TryGetAsync(Key(@"C:\b"), CancellationToken.None));
    }

    [Fact]
    public async Task TheListingOptions_AreParteOfTheKey()
    {
        var cache = new ListingCache();
        cache.Put(Key(@"C:\a", hidden: false), Files(@"C:\a", 1), Stamp, FolderKind.General);

        Assert.Null(await cache.TryGetAsync(Key(@"C:\a", hidden: true), CancellationToken.None));
    }

    [Fact]
    public async Task TheLeastRecentlyUsedFolder_GoesFirst_WhenTheBudgetIsExceeded()
    {
        var cache = new ListingCache(maxEntries: 100);
        cache.Put(Key(@"C:\a"), Files(@"C:\a", 40), Stamp, FolderKind.General);
        cache.Put(Key(@"C:\b"), Files(@"C:\b", 40), Stamp, FolderKind.General);
        await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None);

        cache.Put(Key(@"C:\c"), Files(@"C:\c", 40), Stamp, FolderKind.General);

        Assert.NotNull(await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None));
        Assert.Null(await cache.TryGetAsync(Key(@"C:\b"), CancellationToken.None));
        Assert.NotNull(await cache.TryGetAsync(Key(@"C:\c"), CancellationToken.None));
        Assert.Equal(80, cache.EntryCount);
        Assert.Equal(2, cache.FolderCount);
    }

    [Fact]
    public async Task AListingBiggerThanTheWholeBudget_IsNotKept_AndPushesNothingOut()
    {
        var cache = new ListingCache(maxEntries: 100);
        cache.Put(Key(@"C:\small"), Files(@"C:\small", 10), Stamp, FolderKind.General);

        cache.Put(Key(@"C:\huge"), Files(@"C:\huge", 101), Stamp, FolderKind.General);

        Assert.Null(await cache.TryGetAsync(Key(@"C:\huge"), CancellationToken.None));
        Assert.NotNull(await cache.TryGetAsync(Key(@"C:\small"), CancellationToken.None));
    }

    [Fact]
    public async Task PuttingAFolderAgain_ReplacesItAndCountsItOnce()
    {
        var cache = new ListingCache();
        cache.Put(Key(@"C:\a"), Files(@"C:\a", 10), Stamp, FolderKind.General);
        cache.Put(Key(@"C:\a"), Files(@"C:\a", 4), Stamp.AddMinutes(1), FolderKind.General);

        Assert.Equal(4, cache.EntryCount);
        Assert.Equal(4, (await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None))!.Entries.Count);
    }

    [Fact]
    public async Task Put_WithUnchangedContent_OnlyNotesTheNewTimeInTheStore()
    {
        var store = new MemoryStore();
        var cache = new ListingCache(store: store);
        var key = new ListingKey(P(@"C:\a"), false, false);

        cache.Put(key, [], Stamp, FolderKind.General, changed: false);
        cache.Put(new ListingKey(P(@"C:\b"), false, false), [], Stamp, FolderKind.General);

        await UntilAsync(() => store.Touched.Count == 1 && store.Saved.Count == 1);
        Assert.Equal(key, store.Touched[0].Key);
        Assert.DoesNotContain(key, store.Saved.Keys);
    }

    [Fact]
    public async Task Invalidate_ForgetsTheFolder_AndBelowItOnlyWhenAsked()
    {
        var cache = new ListingCache();
        foreach (var folder in new[] { @"C:\a", @"C:\a\b", @"C:\other" })
        {
            cache.Put(Key(folder), Files(folder, 2), Stamp, FolderKind.General);
        }

        cache.Invalidate(P(@"C:\a"));
        Assert.Null(await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None));
        Assert.NotNull(await cache.TryGetAsync(Key(@"C:\a\b"), CancellationToken.None));

        cache.Put(Key(@"C:\a"), Files(@"C:\a", 2), Stamp, FolderKind.General);
        cache.Invalidate(P(@"C:\a"), includeDescendants: true);

        Assert.Null(await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None));
        Assert.Null(await cache.TryGetAsync(Key(@"C:\a\b"), CancellationToken.None));
        Assert.NotNull(await cache.TryGetAsync(Key(@"C:\other"), CancellationToken.None));
        Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public async Task AFolderThatMemoryForgot_IsReadFromTheStore_AndKeptInMemoryAgain()
    {
        var store = new MemoryStore();
        store.Saved[Key(@"C:\a")] = new CachedListing(Files(@"C:\a", 5), Stamp, FolderKind.Pictures);
        var cache = new ListingCache(store: store);

        var first = await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None);
        store.Fail = true;
        var second = await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None);

        Assert.Equal(CacheSource.Disk, first!.Source);
        Assert.Equal(FolderKind.Pictures, first.Kind);
        Assert.Equal(5, second!.Entries.Count);
        Assert.Equal(5, cache.EntryCount);
    }

    [Fact]
    public async Task AStoreThatFails_IsAbsorbed_ItIsJustACacheMiss()
    {
        var store = new MemoryStore { Fail = true };
        var cache = new ListingCache(store: store);

        Assert.Null(await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None));
        cache.Put(Key(@"C:\a"), Files(@"C:\a", 2), Stamp, FolderKind.General);
        Assert.NotNull(await cache.TryGetAsync(Key(@"C:\a"), CancellationToken.None));
    }

    [Fact]
    public async Task AFinishedListing_IsAlsoSavedInTheStore_AndInvalidationReachesIt()
    {
        var store = new MemoryStore();
        var cache = new ListingCache(store: store);

        cache.Put(Key(@"C:\a"), Files(@"C:\a", 2), Stamp, FolderKind.General);
        await UntilAsync(() => store.Saved.ContainsKey(Key(@"C:\a")));
        cache.Invalidate(P(@"C:\a"));
        await UntilAsync(() => !store.Saved.ContainsKey(Key(@"C:\a")));

        Assert.Equal([P(@"C:\a")], store.Removed);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class ListingDiffTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FileEntry File(string name, long size = 1, int minutes = 0) =>
        new(name, LocationPath.Parse($@"C:\a\{name}"), FileEntryKind.File, size, Stamp, Stamp.AddMinutes(minutes), FileAttributes.Normal);

    [Fact]
    public void TheSameListing_IsNoDifference()
    {
        var diff = ListingDiff.Compute([File("a"), File("b")], [File("b"), File("a")]);

        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Remove);
        Assert.Empty(diff.Add);
        Assert.Equal(string.Empty, diff.Describe());
    }

    [Fact]
    public void NewAndGoneItems_AreAddedAndRemoved()
    {
        var gone = File("gone");

        var diff = ListingDiff.Compute([File("keep"), gone], [File("keep"), File("new")]);

        Assert.Equal([gone], diff.Remove);
        Assert.Equal(["new"], diff.Add.Select(e => e.Name));
        Assert.Equal((1, 1, 0), (diff.Added, diff.Removed, diff.Changed));
        Assert.Equal("1 added, 1 removed", diff.Describe());
    }

    [Fact]
    public void AChangedItem_LeavesWithItsOldVersion_AndComesBackWithTheNewOne()
    {
        var before = File("a", size: 10);
        var after = File("a", size: 99);

        var diff = ListingDiff.Compute([before], [after]);

        Assert.Equal([before], diff.Remove);
        Assert.Equal([after], diff.Add);
        Assert.Equal((0, 0, 1), (diff.Added, diff.Removed, diff.Changed));
        Assert.Equal("1 changed", diff.Describe());
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2000, 3)]
    public void ADateChange_CountsAsChanged(int sizeUnchanged, int newMinutes)
    {
        var diff = ListingDiff.Compute([File("a", sizeUnchanged)], [File("a", sizeUnchanged, newMinutes)]);

        Assert.Equal(1, diff.Changed);
    }

    [Fact]
    public void ACaseOnlyRename_CountsAsChanged()
    {
        var before = new FileEntry("Photo.jpg", LocationPath.Parse(@"C:\a\Photo.jpg"), FileEntryKind.File, 1, Stamp, Stamp, FileAttributes.Normal);
        var after = before with { Name = "photo.jpg" };

        Assert.Equal(1, ListingDiff.Compute([before], [after]).Changed);
    }

    [Fact]
    public void BigListings_AreComparedQuickly()
    {
        var shown = Enumerable.Range(0, 50000).Select(i => File($"f{i}", i)).ToList();
        var fresh = shown.Skip(100).Concat(Enumerable.Range(0, 50).Select(i => File($"n{i}"))).ToList();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var diff = ListingDiff.Compute(shown, fresh);
        watch.Stop();

        Assert.Equal((50, 100, 0), (diff.Added, diff.Removed, diff.Changed));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }
}

public sealed class CachedFolderTests
{
    /// <summary>A folder whose contents the test changes between visits, whose listing can be held back, and that can fail.</summary>
    private sealed class MutableProvider : ILocationProvider
    {
        private TaskCompletionSource _gate = new();

        public MutableProvider() => _gate.SetResult();

        public List<FileEntry> Items { get; } = [];

        public int Calls { get; private set; }

        public Exception? Fail { get; set; }

        public string Name => "mutable";

        public bool CanHandle(LocationPath location) => true;

        /// <summary>Holds back the next listings until <see cref="Release"/>.</summary>
        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await _gate.Task.WaitAsync(cancellationToken);
            if (Fail is not null)
            {
                throw Fail;
            }

            yield return new ListingBatch([.. Items], true);
        }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly LocationPath Folder = LocationPath.Parse(@"C:\a");

    private static FileEntry File(string name, long size = 1) =>
        new(name, LocationPath.Parse($@"C:\a\{name}"), FileEntryKind.File, size, Stamp, Stamp, FileAttributes.Normal);

    private static (FolderViewModel Folder, MutableProvider Provider, ManualTimeProvider Time, ListingCache Cache) Make(ListingCache? cache = null)
    {
        var provider = new MutableProvider();
        var time = new ManualTimeProvider();
        cache ??= new ListingCache();
        return (new FolderViewModel(new LocationProviderRegistry([provider]), new InlineUiContext(), time, cache), provider, time, cache);
    }

    [Fact]
    public async Task TheFirstVisit_ListsNormally_AndRemembersTheFolder()
    {
        var (folder, provider, _, cache) = Make();
        provider.Items.AddRange([File("a"), File("b")]);

        await folder.LoadAsync(Folder, CancellationToken.None);

        Assert.Equal(FolderLoadState.Loaded, folder.State);
        Assert.Null(folder.Notice);
        Assert.Equal(2, cache.EntryCount);
        folder.Dispose();
    }

    [Fact]
    public async Task ASecondVisit_ShowsTheCachedRowsAtOnce_WhileTheFolderIsChecked_ThenAppliesTheDifference()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.AddRange([File("keep"), File("gone"), File("edited", 1)]);
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        provider.Items.Clear();
        provider.Items.AddRange([File("keep"), File("edited", 500), File("new")]);
        provider.Hold();

        await folder.LoadAsync(Folder, CancellationToken.None);

        // The old rows are up and the notice says they are being checked; the real listing has not answered.
        Assert.Equal(["keep", "gone", "edited"], folder.Entries.Select(e => e.Name));
        Assert.Equal(FolderLoadState.Loaded, folder.State);
        Assert.Equal(ListingNoticeKind.Checking, folder.Notice!.Kind);
        Assert.Contains("5 minutes ago", folder.Notice.Text, StringComparison.Ordinal);
        Assert.Contains("may change", folder.Notice.Text, StringComparison.Ordinal);

        provider.Release();
        await folder.Revalidation;

        Assert.Equal(["edited", "keep", "new"], folder.Entries.Select(e => e.Name).Order());
        Assert.Equal(500, folder.Entries.Single(e => e.Name == "edited").Size);
        Assert.Equal(ListingNoticeKind.Updated, folder.Notice!.Kind);
        Assert.Equal("Updated: 1 added, 1 removed, 1 changed", folder.Notice.Text);
        folder.Dispose();
    }

    [Fact]
    public async Task WhenNothingChanged_TheNoticeJustGoesAway_AndNoRowIsTouched()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.AddRange([File("a"), File("b")]);
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        var changes = 0;
        await folder.LoadAsync(Folder, CancellationToken.None);
        folder.Entries.CollectionChanged += (_, _) => changes++;

        await folder.Revalidation;

        Assert.Null(folder.Notice);
        Assert.Equal(0, changes);
        folder.Dispose();
    }

    [Fact]
    public async Task TheUpdatedNotice_FadesAfterAFewSeconds()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Items.Add(File("b"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        await folder.Revalidation;
        Assert.NotNull(folder.Notice);

        time.Advance(ListingNotice.UpdatedShownFor + TimeSpan.FromSeconds(1));
        await UntilAsync(() => folder.Notice is null);

        folder.Dispose();
    }

    [Fact]
    public async Task ARecentlyListedFolder_IsShownWithoutBeingListedAgain()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(500));

        await folder.LoadAsync(Folder, CancellationToken.None);
        await folder.Revalidation;

        Assert.Equal(1, provider.Calls);
        Assert.Null(folder.Notice);
        Assert.Single(folder.Entries);
        folder.Dispose();
    }

    [Fact]
    public async Task ForceFresh_SkipsTheCache_AndStreamsTheRealListing()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Items.Add(File("b"));

        await folder.LoadAsync(Folder, CancellationToken.None, forceFresh: true);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, folder.Entries.Count);
        Assert.Null(folder.Notice);
        folder.Dispose();
    }

    [Fact]
    public async Task IfTheFolderCannotBeChecked_TheCachedRowsStay_AndTheNoticeSaysWhy()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.AddRange([File("a"), File("b")]);
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(3));
        provider.Fail = new LocationException(LocationError.Unknown, Folder, "The folder could not be read.");

        await folder.LoadAsync(Folder, CancellationToken.None);
        await folder.Revalidation;

        Assert.Equal(2, folder.Entries.Count);
        Assert.Equal(FolderLoadState.Loaded, folder.State);
        Assert.Equal(ListingNoticeKind.RefreshFailed, folder.Notice!.Kind);
        Assert.Contains("The folder could not be read", folder.Notice.Text, StringComparison.Ordinal);
        Assert.Contains("3 hours ago", folder.Notice.Text, StringComparison.Ordinal);
        Assert.Null(folder.OfflineListing);
        folder.Dispose();
    }

    private static readonly LocationPath Share = LocationPath.Parse(@"\\nas\media\shows");

    [Fact]
    public async Task AShareThatStopsAnswering_KeepsItsCachedRows_UnderTheOfflineBanner_NotAFooterNote()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.AddRange([File("a"), File("b")]);
        await folder.LoadAsync(Share, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(3));
        provider.Fail = new LocationException(LocationError.Unreachable, Share, "The share stopped answering.");

        await folder.LoadAsync(Share, CancellationToken.None);
        await folder.Revalidation;

        Assert.Equal(2, folder.Entries.Count);
        Assert.Null(folder.Notice);
        Assert.Equal(new OfflineInfo("nas", time.GetUtcNow().AddHours(-3), false), folder.OfflineListing);
        folder.Dispose();
    }

    [Fact]
    public async Task ReloadingADeadShare_ShowsTheLastListing_GreyedUnderTheBanner_AndRecoversWhenItAnswers()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.AddRange([File("a"), File("b")]);
        await folder.LoadAsync(Share, CancellationToken.None);
        var listedAt = time.GetUtcNow();
        time.Advance(TimeSpan.FromMinutes(20));
        provider.Fail = new LocationException(LocationError.Unreachable, Share, "nas is not responding.");

        await folder.LoadAsync(Share, CancellationToken.None, forceFresh: true);

        Assert.Equal(FolderLoadState.Loaded, folder.State);
        Assert.Equal(2, folder.Entries.Count);
        Assert.Equal("nas is not responding.", folder.Message);
        Assert.True(folder.IsRetryable);
        Assert.Equal(new OfflineInfo("nas", listedAt, false), folder.OfflineListing);

        provider.Fail = null;
        await folder.LoadAsync(Share, CancellationToken.None, forceFresh: true);

        Assert.Null(folder.OfflineListing);
        Assert.Equal(FolderLoadState.Loaded, folder.State);
        folder.Dispose();
    }

    [Fact]
    public async Task WorkingOffline_IsRemembered_SoTheBannerCanOfferGoingOnline()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Share, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        provider.Fail = new LocationException(LocationError.Unreachable, Share, "Working offline with nas.") { WorkingOffline = true };

        await folder.LoadAsync(Share, CancellationToken.None, forceFresh: true);

        Assert.True(folder.OfflineListing!.WorkingOffline);
        folder.Dispose();
    }

    [Fact]
    public async Task ADeadShareNeverSeenBefore_HasNothingToShow_SoItKeepsTheBigNotRespondingState()
    {
        var (folder, provider, _, _) = Make();
        provider.Fail = new LocationException(LocationError.Unreachable, Share, "nas is not responding.");

        await folder.LoadAsync(Share, CancellationToken.None);

        Assert.Equal(FolderLoadState.Offline, folder.State);
        Assert.Null(folder.OfflineListing);
        Assert.Empty(folder.Entries);
        folder.Dispose();
    }

    [Fact]
    public async Task AFolderThatCannotBeFound_DoesNotFallBackToTheOldListing()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Share, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        provider.Fail = new LocationException(LocationError.NotFound, Share, "The folder is gone.");

        await folder.LoadAsync(Share, CancellationToken.None, forceFresh: true);

        Assert.Equal(FolderLoadState.NotFound, folder.State);
        Assert.Null(folder.OfflineListing);
        folder.Dispose();
    }

    [Fact]
    public void TheBannerText_SaysWhenTheListingWasMade_TodayAsATime_ElsewhenWithTheDate()
    {
        var now = new DateTimeOffset(2026, 11, 5, 15, 30, 0, TimeSpan.Zero);
        var utc = TimeZoneInfo.Utc;

        Assert.Equal(@"nas is not responding — showing the listing as of 14:02", new OfflineInfo("nas", now.AddMinutes(-88), false).Text(now, utc));
        Assert.Equal("Working offline with nas — showing the listing as of 2026-11-03 09:00", new OfflineInfo("nas", new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), true).Text(now, utc));
    }

    [Fact]
    public async Task LeavingBeforeTheCheckEnds_DropsItsResult()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Items.Add(File("late"));
        provider.Hold();
        await folder.LoadAsync(Folder, CancellationToken.None);

        folder.Cancel();
        provider.Release();
        await folder.Revalidation;

        Assert.Equal(["a"], folder.Entries.Select(e => e.Name));
        Assert.Null(folder.Notice);
        folder.Dispose();
    }

    [Fact]
    public async Task AListingFromTheDisk_IsDescribedAsSaved()
    {
        var cache = new ListingCache(store: new DiskWithOneFolder());
        var (folder, provider, _, _) = Make(cache);
        provider.Items.Add(File("a"));
        provider.Hold();

        await folder.LoadAsync(Folder, CancellationToken.None);

        Assert.Contains("saved listing", folder.Notice!.Text, StringComparison.Ordinal);
        provider.Release();
        await folder.Revalidation;
        folder.Dispose();
    }

    private sealed class DiskWithOneFolder : IListingStore
    {
        public Task<CachedListing?> LoadAsync(ListingKey key, CancellationToken cancellationToken) =>
            Task.FromResult<CachedListing?>(new CachedListing([File("old")], Stamp.AddYears(-1), FolderKind.General));

        public Task SaveAsync(ListingKey key, CachedListing listing, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TouchAsync(ListingKey key, DateTimeOffset listedAt, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(LocationPath folder, bool includeDescendants, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ACachedEmptyFolder_ShowsAsEmpty()
    {
        var (folder, provider, time, _) = Make();
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Hold();

        await folder.LoadAsync(Folder, CancellationToken.None);

        Assert.Equal(FolderLoadState.Empty, folder.State);
        provider.Release();
        await folder.Revalidation;
        folder.Dispose();
    }

    [Fact]
    public async Task ABigCachedListing_ArrivesInChunks_AndTheFolderIsLoadedOnlyWhenAllRowsAreIn()
    {
        var (folder, provider, time, _) = Make();
        provider.Items.AddRange(Enumerable.Range(0, 5000).Select(i => File($"f{i:D5}")));
        await folder.LoadAsync(Folder, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Hold();
        var adds = new List<int>();
        var countWhenLoaded = -1;
        folder.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                adds.Add(e.NewItems!.Count);
            }
        };
        folder.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderViewModel.State) && folder.State == FolderLoadState.Loaded)
            {
                countWhenLoaded = folder.Entries.Count;
            }
        };

        await folder.LoadAsync(Folder, CancellationToken.None);

        Assert.Equal(5000, countWhenLoaded);
        Assert.All(adds, n => Assert.True(n <= FolderViewModel.MaxRowsPerUpdate));
        provider.Release();
        await folder.Revalidation;
        folder.Dispose();
    }

    [Fact]
    public void RemoveRange_TakesOutOnlyWhatIsThere_WithOneNotification()
    {
        var items = new RangeObservableCollection<int>();
        items.AddRange([1, 2, 3, 4]);
        var events = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, e) => events.Add(e);

        items.RemoveRange([2, 4, 9]);
        items.RemoveRange([9]);

        Assert.Equal([1, 3], items);
        var removal = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Remove, removal.Action);
        Assert.Equal([4, 2], removal.OldItems!.Cast<int>());
    }

    [Fact]
    public void TheSortedView_DropsRemovedRows_WithoutSortingAgain_AndFixesTheCounts()
    {
        var entries = new RangeObservableCollection<FileEntry>();
        var view = new FolderItemsView(entries);
        var rows = Enumerable.Range(0, 10).Select(i => File($"f{i}")).ToList();
        var folder = rows[3] with { Kind = FileEntryKind.Folder };
        rows[3] = folder;
        entries.AddRange(rows);
        Assert.Equal(1, view.FolderCount);

        entries.RemoveRange([rows[3], rows[7]]);

        Assert.Equal(8, view.Rows.Count);
        Assert.Equal(0, view.FolderCount);
        Assert.DoesNotContain(view.Rows, r => r.Name is "f3" or "f7");
        Assert.Equal(view.Rows.OrderBy(r => r.Name, Core.Text.NaturalStringComparer.Instance).Select(r => r.Name), view.Rows.Select(r => r.Name));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
