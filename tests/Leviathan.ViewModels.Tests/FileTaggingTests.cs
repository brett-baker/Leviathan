using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Tags;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>Colour tags on the selection in the file view, and how the settings keep the tag set (spec §11.1).</summary>
public sealed class FileTaggingTests
{
    private sealed class Store : ITagStore
    {
        public Dictionary<string, HashSet<int>> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Cannot { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(Tags.TryGetValue(path, out var set) ? new HashSet<int>(set) : new HashSet<int>());

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken)
        {
            if (Cannot.Contains(path))
            {
                throw new IOException("no streams here");
            }

            Tags[path] = [.. tags];
            return Task.CompletedTask;
        }
    }

    private static FileEntry File(string path, FileEntryKind kind = FileEntryKind.File) =>
        new(System.IO.Path.GetFileName(path), LocationPath.Parse(path), kind, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private static FileTagging Make(Store store, TagCatalog? catalog = null) => new(store, () => catalog ?? TagCatalog.Default);

    [Fact]
    public async Task Toggle_PutsTheTagOnEveryFile_ThenTakesItOffWhenAllHaveIt()
    {
        var store = new Store();
        var tagging = Make(store);
        var files = new[] { File(@"C:\a\one.jpg"), File(@"C:\a\two.jpg") };

        var on = await tagging.ToggleAsync(files, 1, CancellationToken.None);
        var off = await tagging.ToggleAsync(files, 1, CancellationToken.None);

        Assert.Equal(2, on.Changed);
        Assert.Contains("Red on", on.Message, StringComparison.Ordinal);
        Assert.Equal(2, off.Changed);
        Assert.All(store.Tags.Values, set => Assert.DoesNotContain(1, set));
    }

    [Fact]
    public async Task Toggle_WhenOnlySomeHaveIt_PutsItOnTheRest_AndKeepsOtherTags()
    {
        var store = new Store();
        store.Tags[@"C:\a\one.jpg"] = [1, 5];
        var tagging = Make(store);

        var change = await tagging.ToggleAsync([File(@"C:\a\one.jpg"), File(@"C:\a\two.jpg")], 1, CancellationToken.None);

        Assert.Equal(1, change.Changed);
        Assert.Equal([1, 5], store.Tags[@"C:\a\one.jpg"].Order().ToArray());
        Assert.Equal([1], store.Tags[@"C:\a\two.jpg"].ToArray());
    }

    [Fact]
    public async Task Toggle_UsesTheUsersOrder_NotTheIdNumber()
    {
        var store = new Store();
        var tagging = Make(store, TagCatalog.Default.Move(5, 0));

        await tagging.ToggleAsync([File(@"C:\a\one.jpg")], 1, CancellationToken.None);

        Assert.Equal([5], store.Tags[@"C:\a\one.jpg"].ToArray());
    }

    [Fact]
    public async Task Toggle_SkipsFolders_AndSaysSoWhenThatIsAllThereIs()
    {
        var store = new Store();

        var change = await Make(store).ToggleAsync([File(@"C:\a\sub", FileEntryKind.Folder)], 2, CancellationToken.None);

        Assert.Equal(0, change.Changed);
        Assert.Empty(store.Tags);
        Assert.Contains("folders", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Toggle_ForANumberWithNoTag_ChangesNothing()
    {
        var store = new Store();
        var change = await Make(store).ToggleAsync([File(@"C:\a\one.jpg")], 9, CancellationToken.None);

        Assert.Equal(0, change.Changed);
        Assert.Contains("no tag 9", change.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Tags);
    }

    [Fact]
    public async Task Toggle_WhenOneLocationCannotHoldATag_ReportsItAndStillTagsTheRest()
    {
        var store = new Store();
        store.Cannot.Add(@"C:\a\fat.jpg");

        var change = await Make(store).ToggleAsync([File(@"C:\a\fat.jpg"), File(@"C:\a\ok.jpg")], 3, CancellationToken.None);

        Assert.Equal(1, change.Changed);
        Assert.Single(change.Problems);
        Assert.Contains("fat.jpg", change.Message, StringComparison.Ordinal);
        Assert.Equal([3], store.Tags[@"C:\a\ok.jpg"].ToArray());
    }

    [Fact]
    public async Task Clear_TakesEveryTagOff_AndLeavesUntaggedFilesAlone()
    {
        var store = new Store();
        store.Tags[@"C:\a\one.jpg"] = [1, 2, 3];

        var change = await Make(store).ClearAsync([File(@"C:\a\one.jpg"), File(@"C:\a\two.jpg")], CancellationToken.None);

        Assert.Equal(1, change.Changed);
        Assert.Empty(store.Tags[@"C:\a\one.jpg"]);
        Assert.DoesNotContain(@"C:\a\two.jpg", store.Tags.Keys);
    }

    [Fact]
    public async Task Settings_KeepTheTagsAcrossAReload_AndCleanAHandEditedList()
    {
        var fs = new MockFileSystem();
        using var store = new SettingsStore(fs, @"C:\data\settings.json");
        await store.UpdateAsync(s => s with { Tags = new TagSettings { Items = [.. s.Tags.Catalog().Rename(4, "Keep").Move(4, 0).Add("Mine", "#102030").Tags] } }, CancellationToken.None);

        using var reloaded = new SettingsStore(fs, @"C:\data\settings.json");
        var catalog = (await reloaded.LoadAsync(CancellationToken.None)).Tags.Catalog();

        Assert.Equal("Keep", catalog.ForKey(1)?.Name);
        Assert.Equal("Mine", catalog.Tags[^1].Name);
        Assert.Equal(8, catalog.Tags.Count);

        fs.File.WriteAllText(@"C:\data\settings.json", """{"Tags":{"Items":[{"Id":0,"Name":"bad","Colour":"x"}]}}""");
        using var broken = new SettingsStore(fs, @"C:\data\settings.json");
        Assert.Equal(7, (await broken.LoadAsync(CancellationToken.None)).Tags.Catalog().Tags.Count);
    }

    [Fact]
    public async Task TriageToggle_FollowsTheSameOrderAsTheFileViewKeys()
    {
        var store = new Store();
        var actions = new TriageFileActions(new NoUndo(), store, () => new TriageKeepOptions(null), null, () => TagCatalog.Default.Move(6, 0));

        var on = await actions.ToggleTagAsync(File(@"C:\a\one.mkv"), 1, CancellationToken.None);

        Assert.True(on);
        Assert.Equal([6], store.Tags[@"C:\a\one.mkv"].ToArray());
    }

    private sealed class NoUndo : Leviathan.ViewModels.Undo.IUndoActions
    {
        public Task<Leviathan.ViewModels.Undo.StepResult> MoveAsync(IReadOnlyList<Leviathan.ViewModels.Undo.ItemMove> moves, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Leviathan.ViewModels.Undo.StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
