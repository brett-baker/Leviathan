using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>Tags of the files on show, for the Tags column and the tag: filter (spec §11.1, §6.3).</summary>
public sealed class FolderTagTrackerTests
{
    private sealed class Store : ITagStore
    {
        private readonly object _gate = new();

        public Dictionary<string, HashSet<int>> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Reads { get; } = [];

        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Reads.Add(path);
            }

            if (Throws.Contains(path))
            {
                throw new IOException("refused");
            }

            return Task.FromResult<IReadOnlySet<int>>(Tags.TryGetValue(path, out var set) ? new HashSet<int>(set) : new HashSet<int>());
        }

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InlineUi : IUiContext
    {
        public void Post(Action action) => action();
    }

    private static FileEntry File(string path, FileEntryKind kind = FileEntryKind.File) =>
        new(System.IO.Path.GetFileName(path), LocationPath.Parse(path), kind, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task SetVisible_ReadsTheTagsOfTheRowsOnScreen_AndTellsTheViewOnce()
    {
        var store = new Store();
        store.Tags[@"C:\a\one.jpg"] = [1, 4];
        using var tracker = new FolderTagTracker(store, new InlineUi());
        var told = new List<string>();
        tracker.Changed += (_, paths) => told.AddRange(paths);
        var one = File(@"C:\a\one.jpg");
        var two = File(@"C:\a\two.jpg");

        tracker.SetVisible([one, two]);
        await Until(() => told.Count == 2);

        Assert.Equal(["Red", "Green"], tracker.TagsOf(one).Select(t => t.Name).ToArray());
        Assert.Empty(tracker.TagsOf(two));
        Assert.True(tracker.IsKnown(two));
    }

    [Fact]
    public async Task Folders_AreNeverRead()
    {
        var store = new Store();
        using var tracker = new FolderTagTracker(store, new InlineUi());

        tracker.SetVisible([File(@"C:\a\sub", FileEntryKind.Folder)]);
        await Task.Delay(50);

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task AnUnreadableFile_CountsAsUntagged_AndIsNotAskedAgain()
    {
        var store = new Store();
        store.Throws.Add(@"C:\a\bad.jpg");
        using var tracker = new FolderTagTracker(store, new InlineUi());
        var bad = File(@"C:\a\bad.jpg");

        tracker.SetVisible([bad]);
        await Until(() => tracker.IsKnown(bad));
        tracker.SetVisible([bad]);
        await Task.Delay(50);

        Assert.Empty(tracker.TagsOf(bad));
        Assert.Single(store.Reads);
    }

    [Fact]
    public async Task SetFolder_ForgetsEverything()
    {
        var store = new Store();
        store.Tags[@"C:\a\one.jpg"] = [2];
        using var tracker = new FolderTagTracker(store, new InlineUi());
        var one = File(@"C:\a\one.jpg");
        tracker.SetVisible([one]);
        await Until(() => tracker.IsKnown(one));

        tracker.SetFolder();

        Assert.False(tracker.IsKnown(one));
        Assert.Empty(tracker.TagsOf(one));
    }

    [Fact]
    public async Task Reload_ReadsAChangedFileAgain()
    {
        var store = new Store();
        using var tracker = new FolderTagTracker(store, new InlineUi());
        var one = File(@"C:\a\one.jpg");
        tracker.SetVisible([one]);
        await Until(() => tracker.IsKnown(one));

        store.Tags[@"C:\a\one.jpg"] = [3];
        tracker.Reload([one]);
        await Until(() => tracker.TagsOf(one).Count == 1);

        Assert.Equal("Yellow", tracker.TagsOf(one)[0].Name);
    }

    [Fact]
    public async Task TagsFollowTheUsersOrderAndNames()
    {
        var store = new Store();
        store.Tags[@"C:\a\one.jpg"] = [1, 5];
        var catalog = TagCatalog.Default.Move(5, 0).Rename(5, "Later");
        using var tracker = new FolderTagTracker(store, new InlineUi(), () => catalog);
        var one = File(@"C:\a\one.jpg");

        tracker.SetVisible([one]);
        await Until(() => tracker.IsKnown(one));

        Assert.Equal(["Later", "Red"], tracker.TagsOf(one).Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task TagFilter_NeedsTheWholeFolder_AndMatchesByNameIgnoringCase()
    {
        var store = new Store();
        store.Tags[@"C:\a\red.jpg"] = [1];
        store.Tags[@"C:\a\green.jpg"] = [4];
        using var tracker = new FolderTagTracker(store, new InlineUi());
        var entries = new[] { File(@"C:\a\red.jpg"), File(@"C:\a\green.jpg"), File(@"C:\a\none.jpg") };
        var query = FilterQuery.Parse("tag:RED");

        Assert.True(query.UsesTags);
        Assert.False(FilterQuery.Parse("report").UsesTags);

        tracker.LoadAll(entries);
        await Until(() => entries.All(tracker.IsKnown));

        Assert.Equal(["red.jpg"], entries.Where(e => query.Matches(e, tracker)).Select(e => e.Name).ToArray());
        var without = FilterQuery.Parse("-tag:red");
        Assert.Equal(["green.jpg", "none.jpg"], entries.Where(e => without.Matches(e, tracker)).Select(e => e.Name).Order().ToArray());
    }

    [Fact]
    public void WithoutAStore_NothingShowsATag()
    {
        using var tracker = new FolderTagTracker(null, new InlineUi());
        var one = File(@"C:\a\one.jpg");

        tracker.SetVisible([one]);

        Assert.Empty(tracker.TagsOf(one));
        Assert.False(tracker.IsKnown(one));
    }
}
