using Leviathan.Core.Model;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>Where colour tags live (spec §11.2): the stream on the file, the index beside it, and the index alone where there are no streams.</summary>
public sealed class IndexedTagStoreTests
{
    private sealed class Streams : ITagStore
    {
        public Dictionary<string, HashSet<int>> Held { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Supported { get; set; } = true;

        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken)
        {
            if (!Supported)
            {
                throw new IOException("no streams");
            }

            return Task.FromResult<IReadOnlySet<int>>(Held.TryGetValue(path, out var set) ? new HashSet<int>(set) : new HashSet<int>());
        }

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken)
        {
            if (!Supported)
            {
                throw new IOException("no streams");
            }

            if (tags.Count == 0)
            {
                Held.Remove(path);
            }
            else
            {
                Held[path] = [.. tags];
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Index : ITagIndex
    {
        public Dictionary<string, TagIndexEntry> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TagIndexEntry? FindByPath(string path) => Rows.GetValueOrDefault(path);

        public TagIndexEntry? FindByIdentity(FileIdentity identity) => Rows.Values.FirstOrDefault(r => r.Identity == identity);

        public void Put(TagIndexEntry entry)
        {
            if (entry.Tags.Count == 0)
            {
                Rows.Remove(entry.Path);
                return;
            }

            foreach (var other in Rows.Values.Where(r => entry.Identity is not null && r.Identity == entry.Identity && r.Path != entry.Path).ToList())
            {
                Rows.Remove(other.Path);
            }

            Rows[entry.Path] = entry;
        }

        public void Remove(string path) => Rows.Remove(path);

        public IReadOnlyList<string> PathsWithTag(int tagId) => [.. Rows.Values.Where(r => r.Tags.Contains(tagId)).Select(r => r.Path)];
    }

    private sealed class Identities : IFileIdentitySource
    {
        public Dictionary<string, FileIdentity> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileIdentity? Get(string path) => Known.TryGetValue(path, out var id) ? id : null;
    }

    private static readonly DateTimeOffset Stamp = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Streams _streams = new();
    private readonly Index _index = new();
    private readonly Identities _identities = new();
    private (long, DateTimeOffset) _stat = (100, Stamp);

    private IndexedTagStore Make() => new(_streams, new Lazy<ITagIndex>(_index), _identities, _ => _stat);

    [Fact]
    public async Task Set_WritesTheStreamAndTheIndex_AndFindsThePathByTag()
    {
        _identities.Known[@"C:\a\x.jpg"] = new FileIdentity(1, 2, 3);

        await Make().SetAsync(@"C:\a\x.jpg", new HashSet<int> { 4, 1 }, CancellationToken.None);

        Assert.Equal([1, 4], _streams.Held[@"C:\a\x.jpg"].Order().ToArray());
        Assert.Equal(new FileIdentity(1, 2, 3), _index.Rows[@"C:\a\x.jpg"].Identity);
        Assert.Equal([@"C:\a\x.jpg"], _index.PathsWithTag(4));
    }

    [Fact]
    public async Task Set_WithNoTags_RemovesBoth()
    {
        var store = Make();
        await store.SetAsync(@"C:\a\x.jpg", new HashSet<int> { 1 }, CancellationToken.None);

        await store.SetAsync(@"C:\a\x.jpg", new HashSet<int>(), CancellationToken.None);

        Assert.Empty(_streams.Held);
        Assert.Empty(_index.Rows);
    }

    [Fact]
    public async Task Set_WhereThereAreNoStreams_KeepsTheTagsInTheIndexAlone()
    {
        _streams.Supported = false;
        var store = Make();

        await store.SetAsync(@"E:\fat\x.mp4", new HashSet<int> { 2 }, CancellationToken.None);

        var tags = await store.GetAsync(@"E:\fat\x.mp4", CancellationToken.None);
        Assert.Equal(new[] { 2 }, tags.ToArray());
        Assert.Null(_index.Rows[@"E:\fat\x.mp4"].Identity);
    }

    [Fact]
    public async Task Get_WhereThereAreNoStreams_DropsTagsOfAFileThatChanged()
    {
        _streams.Supported = false;
        var store = Make();
        await store.SetAsync(@"E:\fat\x.mp4", new HashSet<int> { 2 }, CancellationToken.None);

        _stat = (999, Stamp); // replaced by another file of the same name

        Assert.Empty(await store.GetAsync(@"E:\fat\x.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task Get_AllowsTheTwoSecondTimeRoundingOfFat()
    {
        _streams.Supported = false;
        var store = Make();
        await store.SetAsync(@"E:\fat\x.mp4", new HashSet<int> { 2 }, CancellationToken.None);

        _stat = (100, Stamp.AddMilliseconds(400));

        var tags = await store.GetAsync(@"E:\fat\x.mp4", CancellationToken.None);
        Assert.Equal(new[] { 2 }, tags.ToArray());
    }

    [Fact]
    public async Task Get_AFileMovedByAnotherProgram_KeepsItsTags_AndTheIndexFollowsIt()
    {
        _identities.Known[@"C:\old\x.jpg"] = new FileIdentity(1, 2, 3);
        var store = Make();
        await store.SetAsync(@"C:\old\x.jpg", new HashSet<int> { 3 }, CancellationToken.None);

        // Explorer moved it: the stream went along, the index still says the old place.
        _streams.Held[@"C:\new\x.jpg"] = [3];
        _identities.Known[@"C:\new\x.jpg"] = new FileIdentity(1, 2, 3);
        var tags = await store.GetAsync(@"C:\new\x.jpg", CancellationToken.None);

        Assert.Equal([3], tags.ToArray());
        Assert.DoesNotContain(@"C:\old\x.jpg", _index.Rows.Keys);
        Assert.Contains(@"C:\new\x.jpg", _index.Rows.Keys);
    }

    [Fact]
    public async Task Get_AStrippedStream_IsAnsweredFromTheIndexByIdentity_EvenAtANewPath()
    {
        _identities.Known[@"C:\old\x.jpg"] = new FileIdentity(1, 2, 3);
        var store = Make();
        await store.SetAsync(@"C:\old\x.jpg", new HashSet<int> { 5 }, CancellationToken.None);

        _streams.Held.Clear();
        _identities.Known[@"C:\new\x.jpg"] = new FileIdentity(1, 2, 3);

        var tags = await store.GetAsync(@"C:\new\x.jpg", CancellationToken.None);
        Assert.Equal(new[] { 5 }, tags.ToArray());
        Assert.Contains(@"C:\new\x.jpg", _index.Rows.Keys);
    }

    [Fact]
    public async Task Get_ANewFileAtTheOldPath_DoesNotInheritTags_WhenIdentitiesDiffer()
    {
        _identities.Known[@"C:\a\x.jpg"] = new FileIdentity(1, 2, 3);
        var store = Make();
        await store.SetAsync(@"C:\a\x.jpg", new HashSet<int> { 1 }, CancellationToken.None);

        _streams.Held.Clear();
        _identities.Known[@"C:\a\x.jpg"] = new FileIdentity(1, 9, 9);

        Assert.Empty(await store.GetAsync(@"C:\a\x.jpg", CancellationToken.None));
    }

    [Fact]
    public async Task Get_OfAMissingFile_StillThrows()
    {
        var failing = new IndexedTagStore(new Missing(), new Lazy<ITagIndex>(_index), _identities, _ => null);

        await Assert.ThrowsAsync<FileNotFoundException>(() => failing.GetAsync(@"C:\gone.jpg", CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => failing.SetAsync(@"C:\gone.jpg", new HashSet<int> { 1 }, CancellationToken.None));
    }

    private sealed class Missing : ITagStore
    {
        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) => throw new FileNotFoundException("gone", path);

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken) => throw new FileNotFoundException("gone", path);
    }
}
