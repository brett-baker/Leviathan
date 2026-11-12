using Leviathan.Core.Duplicates;
using Leviathan.ViewModels.Duplicates;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Tests;

/// <summary>What the duplicate results window does to the extra copies (spec §15.2).</summary>
public sealed class DuplicateCleanupTests
{
    private static ReviewFile F(string path, long size = 100) => new(path, size, DateTime.UnixEpoch);

    private static CleanupGroup Group(bool exact, ReviewFile keep, params ReviewFile[] extras) => new([keep, .. extras], keep, exact);

    private sealed class Files : IUndoActions
    {
        public List<string> Log { get; } = [];

        public HashSet<string> Fail { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken)
        {
            Log.Add("move " + string.Join(";", moves.Select(m => $"{m.From}>{m.To}")));
            return Task.FromResult(new StepResult([.. moves.Where(m => !Fail.Contains(m.From))], [.. moves.Where(m => Fail.Contains(m.From)).Select(m => $"'{m.From}' failed")]));
        }

        public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            Log.Add("delete " + string.Join(";", paths));
            return Task.FromResult(new StepResult([.. paths.Where(p => !Fail.Contains(p)).Select(p => new ItemMove(p, @"C:\bin\" + Path.GetFileName(p)))], [.. paths.Where(Fail.Contains).Select(p => $"'{p}' failed")]));
        }
    }

    private sealed class Tags : ITagStore
    {
        public Dictionary<string, HashSet<int>> By { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlySet<int>> GetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<int>>(Missing.Contains(path) ? TagFiles.Gone : By.TryGetValue(path, out var t) ? new HashSet<int>(t) : new HashSet<int>());

        public Task SetAsync(string path, IReadOnlySet<int> tags, CancellationToken cancellationToken)
        {
            By[path] = [.. tags];
            return Task.CompletedTask;
        }
    }

    private sealed class Links : IHardLinker
    {
        public List<string> Log { get; } = [];

        public HashSet<string> Fail { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> ReplaceWithLinkAsync(string keepPath, string extraPath, CancellationToken cancellationToken)
        {
            Log.Add($"link {extraPath}->{keepPath}");
            return Task.FromResult<string?>(Fail.Contains(extraPath) ? $"'{extraPath}' is in use" : null);
        }

        public Task<string?> MakeIndependentAsync(string path, CancellationToken cancellationToken)
        {
            Log.Add("unlink " + path);
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public void Plan_NeverIncludesAKeptCopy_NorOneKeptByAnotherGroup_NorTheSameFileTwice()
    {
        var keepA = F(@"C:\a\1");
        var keepB = F(@"C:\b\1");
        var shared = F(@"C:\s\1");
        var groups = new[] { Group(true, keepA, keepB, shared), Group(true, keepB, shared, F(@"C:\b\2")) };

        var plan = DuplicateCleanup.Plan(groups);

        Assert.Equal([@"C:\s\1", @"C:\b\2"], plan.Select(p => p.Extra.Path));
    }

    [Fact]
    public async Task Delete_SoftDeletesTheExtras_AndUndoPutsThemBack()
    {
        var files = new Files();
        var cleanup = new DuplicateCleanup(files, new Tags());
        var result = await cleanup.RunAsync([Group(true, F(@"C:\k\a", 50), F(@"C:\x\a", 50), F(@"C:\y\a", 50))], CleanupKind.Delete, null, 0, CancellationToken.None);

        Assert.Equal(2, result.Done);
        Assert.Equal(100, result.Bytes);
        Assert.Equal(@"delete C:\x\a;C:\y\a", files.Log[0]);

        await result.Undo!();
        Assert.Equal(@"move C:\bin\a>C:\x\a;C:\bin\a>C:\y\a", files.Log[1]);
    }

    [Fact]
    public async Task Delete_TellsTheAppWhatWentToTheBin_SoCtrlZCanUndoIt()
    {
        var recorded = new List<ItemMove>();
        var cleanup = new DuplicateCleanup(new Files(), new Tags(), recordDelete: moves => recorded.AddRange(moves));

        await cleanup.RunAsync([Group(true, F(@"C:\k\a"), F(@"C:\x\a"))], CleanupKind.Delete, null, 0, CancellationToken.None);

        Assert.Equal([new ItemMove(@"C:\x\a", @"C:\bin\a")], recorded);
    }

    [Fact]
    public async Task Delete_ReportsTheOnesThatFailed_AndCountsOnlyTheOthers()
    {
        var files = new Files();
        files.Fail.Add(@"C:\x\a");
        var result = await new DuplicateCleanup(files, new Tags()).RunAsync([Group(true, F(@"C:\k\a", 10), F(@"C:\x\a", 10), F(@"C:\y\a", 10))], CleanupKind.Delete, null, 0, CancellationToken.None);

        Assert.Equal(1, result.Done);
        Assert.Equal(10, result.Bytes);
        Assert.Single(result.Problems);
    }

    [Fact]
    public async Task Move_GivesEveryCopyAUniqueNameInTheFolder()
    {
        var files = new Files();
        var result = await new DuplicateCleanup(files, new Tags()).RunAsync(
            [Group(true, F(@"C:\k\a.jpg"), F(@"C:\x\a.jpg"), F(@"C:\y\a.jpg"))], CleanupKind.Move, @"D:\dupes", 0, CancellationToken.None);

        Assert.Equal(2, result.Done);
        Assert.Equal(@"move C:\x\a.jpg>D:\dupes\a.jpg;C:\y\a.jpg>D:\dupes\a (2).jpg", files.Log[0]);
    }

    [Fact]
    public async Task Move_WithoutAFolder_DoesNothingAndSaysWhy()
    {
        var files = new Files();
        var result = await new DuplicateCleanup(files, new Tags()).RunAsync([Group(true, F(@"C:\k\a"), F(@"C:\x\a"))], CleanupKind.Move, " ", 0, CancellationToken.None);

        Assert.Equal(0, result.Done);
        Assert.Empty(files.Log);
        Assert.Single(result.Problems);
        Assert.Null(result.Undo);
    }

    [Fact]
    public async Task HardLink_ReplacesExactCopiesOnTheSameVolume_AndUndoMakesThemIndependent()
    {
        var links = new Links();
        var result = await new DuplicateCleanup(new Files(), new Tags(), links).RunAsync(
            [Group(true, F(@"C:\k\a", 70), F(@"C:\x\a", 70))], CleanupKind.HardLink, null, 0, CancellationToken.None);

        Assert.Equal(1, result.Done);
        Assert.Equal(70, result.Bytes);
        Assert.Equal([@"link C:\x\a->C:\k\a"], links.Log);

        await result.Undo!();
        Assert.Equal(@"unlink C:\x\a", links.Log[1]);
    }

    [Fact]
    public async Task HardLink_SkipsSimilarCopies_AndCopiesOnAnotherVolume()
    {
        var links = new Links();
        var cleanup = new DuplicateCleanup(new Files(), new Tags(), links);

        var similar = await cleanup.RunAsync([Group(false, F(@"C:\k\a"), F(@"C:\x\a"))], CleanupKind.HardLink, null, 0, CancellationToken.None);
        var other = await cleanup.RunAsync([Group(true, F(@"C:\k\a"), F(@"D:\x\a"))], CleanupKind.HardLink, null, 0, CancellationToken.None);

        Assert.Equal(0, similar.Done + other.Done);
        Assert.Single(similar.Problems);
        Assert.Single(other.Problems);
        Assert.Empty(links.Log);
    }

    [Fact]
    public async Task HardLink_ALinkThatFails_IsReported_AndTheRestGoOn()
    {
        var links = new Links();
        links.Fail.Add(@"C:\x\a");
        var result = await new DuplicateCleanup(new Files(), new Tags(), links).RunAsync(
            [Group(true, F(@"C:\k\a"), F(@"C:\x\a"), F(@"C:\y\a"))], CleanupKind.HardLink, null, 0, CancellationToken.None);

        Assert.Equal(1, result.Done);
        Assert.Single(result.Problems);
    }

    [Fact]
    public async Task HardLink_WithoutALinker_SaysSo()
    {
        var result = await new DuplicateCleanup(new Files(), new Tags()).RunAsync([Group(true, F(@"C:\k\a"), F(@"C:\x\a"))], CleanupKind.HardLink, null, 0, CancellationToken.None);

        Assert.Equal(0, result.Done);
        Assert.Single(result.Problems);
    }

    [Fact]
    public async Task Tag_AddsTheTagToTheExtras_KeepsTheirOthers_AndUndoRestoresThem()
    {
        var tags = new Tags();
        tags.By[@"C:\x\a"] = [1];
        var result = await new DuplicateCleanup(new Files(), tags).RunAsync([Group(true, F(@"C:\k\a"), F(@"C:\x\a"), F(@"C:\y\a"))], CleanupKind.Tag, null, 7, CancellationToken.None);

        Assert.Equal(2, result.Done);
        Assert.Equal([1, 7], tags.By[@"C:\x\a"].Order());
        Assert.Equal([7], tags.By[@"C:\y\a"].Order());
        Assert.False(tags.By.ContainsKey(@"C:\k\a"));

        await result.Undo!();
        Assert.Equal([1], tags.By[@"C:\x\a"].Order());
        Assert.Empty(tags.By[@"C:\y\a"]);
    }

    [Fact]
    public async Task Tag_AFileThatIsGone_IsReported()
    {
        var tags = new Tags();
        tags.Missing.Add(@"C:\x\a");
        var result = await new DuplicateCleanup(new Files(), tags).RunAsync([Group(true, F(@"C:\k\a"), F(@"C:\x\a"))], CleanupKind.Tag, null, 7, CancellationToken.None);

        Assert.Equal(0, result.Done);
        Assert.Single(result.Problems);
        Assert.Null(result.Undo);
    }

    [Fact]
    public async Task AnEmptyPlan_DoesNothing()
    {
        var files = new Files();
        var result = await new DuplicateCleanup(files, new Tags()).RunAsync([Group(true, F(@"C:\k\a"))], CleanupKind.Delete, null, 0, CancellationToken.None);

        Assert.Equal(0, result.Done);
        Assert.Empty(files.Log);
    }
}
