using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Testing;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

/// <summary>A folder on screen follows the disk when its watcher reports a change (spec §3.1).</summary>
public sealed class LiveRefreshTests
{
    private sealed class ChangingProvider : ILocationProvider
    {
        private TaskCompletionSource _gate = new();

        public ChangingProvider() => _gate.SetResult();

        public List<FileEntry> Items { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        private int _calls;

        public Exception? Fail { get; set; }

        public string Name => "changing";

        public bool CanHandle(LocationPath location) => true;

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<ListingBatch> ListAsync(LocationPath location, ListingOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
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

    private static (FolderViewModel Folder, ChangingProvider Provider, FakeFolderWatcher Watcher, ListingCache Cache) Make()
    {
        var provider = new ChangingProvider();
        var watcher = new FakeFolderWatcher();
        var cache = new ListingCache();
        var folder = new FolderViewModel(new LocationProviderRegistry([provider]), new InlineUiContext(), new ManualTimeProvider(), cache, watcher);
        return (folder, provider, watcher, cache);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task LoadAsync_WatchesTheFolder_AndMovesTheWatchWhenNavigating()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.Add(File("a"));

        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.Watched.SequenceEqual([Folder]));

        var other = LocationPath.Parse(@"C:\b");
        await folder.LoadAsync(other, CancellationToken.None);
        await UntilAsync(() => watcher.Watched.SequenceEqual([other]));

        folder.Dispose();
        await UntilAsync(() => watcher.ActiveCount == 0);
    }

    [Fact]
    public async Task Reloading_TheSameFolder_KeepsTheWatch()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);

        await folder.ReloadAsync(CancellationToken.None, forceFresh: true);
        await Task.Delay(50);

        Assert.Equal(1, watcher.ActiveCount);
        folder.Dispose();
    }

    [Fact]
    public async Task AChangeOnDisk_AddsAndRemovesRows_WithoutClearingTheList()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.AddRange([File("keep"), File("gone")]);
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);
        var resets = 0;
        folder.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        provider.Items.Clear();
        provider.Items.AddRange([File("keep"), File("new")]);
        watcher.Raise(Folder);

        await UntilAsync(() => folder.Entries.Select(e => e.Name).Order().SequenceEqual(["keep", "new"]));
        Assert.Equal(0, resets);
        Assert.Equal(FolderLoadState.Loaded, folder.State);
        Assert.Null(folder.Notice);
        folder.Dispose();
    }

    [Fact]
    public async Task AChangeOnDisk_UpdatesTheCache_SoTheNextVisitStartsFromTheNewRows()
    {
        var (folder, provider, watcher, cache) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);

        provider.Items.Add(File("b"));
        watcher.Raise(Folder);
        await UntilAsync(() => folder.Entries.Count == 2);

        var cached = await cache.TryGetAsync(new ListingKey(Folder, false, false), CancellationToken.None);
        Assert.Equal(2, cached!.Entries.Count);
        folder.Dispose();
    }

    [Fact]
    public async Task ChangesDuringACheck_AreCheckedAgainWhenItEnds_OneAtATime()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);
        var loads = provider.Calls;

        provider.Hold();
        provider.Items.Add(File("b"));
        watcher.Raise(Folder);
        await UntilAsync(() => provider.Calls == loads + 1);
        watcher.Raise(Folder);
        watcher.Raise(Folder);
        Assert.Equal(loads + 1, provider.Calls);

        provider.Items.Add(File("c"));
        provider.Release();

        await UntilAsync(() => folder.Entries.Count == 3);
        await UntilAsync(() => provider.Calls == loads + 2);
        folder.Dispose();
    }

    [Fact]
    public async Task AChangeWhileTheFolderIsLoading_IsAppliedWhenTheLoadEnds()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);

        provider.Hold();
        var reload = folder.ReloadAsync(CancellationToken.None, forceFresh: true);
        await UntilAsync(() => folder.State == FolderLoadState.Loading);
        watcher.Raise(Folder);
        provider.Items.Add(File("b"));
        provider.Release();
        await reload;

        await UntilAsync(() => folder.Entries.Count == 2 && folder.State == FolderLoadState.Loaded);
        folder.Dispose();
    }

    [Fact]
    public async Task WhenTheCheckFails_TheRowsStay()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.AddRange([File("a"), File("b")]);
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);
        var loads = provider.Calls;

        provider.Fail = new IOException("share dropped");
        watcher.Raise(Folder);
        await UntilAsync(() => provider.Calls == loads + 1);
        await Task.Delay(50);

        Assert.Equal(2, folder.Entries.Count);
        Assert.Equal(FolderLoadState.Loaded, folder.State);

        provider.Fail = null;
        provider.Items.Add(File("c"));
        watcher.Raise(Folder);
        await UntilAsync(() => folder.Entries.Count == 3);
        folder.Dispose();
    }

    [Fact]
    public async Task AChangeInAnotherFolder_IsIgnored()
    {
        var (folder, provider, watcher, _) = Make();
        provider.Items.Add(File("a"));
        await folder.LoadAsync(Folder, CancellationToken.None);
        await UntilAsync(() => watcher.ActiveCount == 1);
        var loads = provider.Calls;

        watcher.Raise(LocationPath.Parse(@"C:\elsewhere"));
        await Task.Delay(50);

        Assert.Equal(loads, provider.Calls);
        folder.Dispose();
    }
}
