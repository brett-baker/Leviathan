using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Triage;
using Leviathan.ViewModels.Undo;

namespace Leviathan.Integration.Tests;

/// <summary>Triage decisions and tags on real files in a real temp folder (NTFS alternate data streams, real renames).</summary>
public sealed class TriageOnDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-triage-" + Guid.NewGuid().ToString("N"));

    public TriageOnDiskTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string MakeFile(string relative, string content = "video bytes")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileEntry Entry(string path)
    {
        var info = new FileInfo(path);
        return new FileEntry(info.Name, LocationPath.Parse(path), FileEntryKind.File, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes);
    }

    /// <summary>Does what the worker's journaled jobs do, with plain moves into a bin folder of the test.</summary>
    private sealed class DiskFiles(string bin) : IUndoActions
    {
        public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken)
        {
            var done = new List<ItemMove>();
            var problems = new List<string>();
            foreach (var move in moves)
            {
                if (File.Exists(move.To))
                {
                    problems.Add($"Something named '{Path.GetFileName(move.To)}' already exists.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                File.Move(move.From, move.To);
                done.Add(move);
            }

            return Task.FromResult(new StepResult(done, problems));
        }

        public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(bin);
            var done = new List<ItemMove>();
            foreach (var path in paths)
            {
                var target = Path.Combine(bin, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
                File.Move(path, target);
                done.Add(new ItemMove(path, target));
            }

            return Task.FromResult(new StepResult(done, []));
        }
    }

    // Hand-off at the end of triage: keeping only tags, then the kept files move together and take their tags with them.
    [Fact]
    public async Task KeptFiles_MoveTogetherAtTheEnd_AndTheirTagsGoWithThem()
    {
        var a = MakeFile(@"inbox\a.mkv", "A");
        var b = MakeFile(@"inbox\b.mkv", "B");
        var c = MakeFile(@"inbox\c.mkv", "C");
        var library = Path.Combine(_root, "library");
        var actions = new TriageFileActions(new DiskFiles(Path.Combine(_root, "bin")), new AlternateStreamTagStore(), () => new TriageKeepOptions(library, MoveOnKeep: false));
        var session = new TriageSession([Entry(a), Entry(b), Entry(c)], new FakeVideoEngine(), actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync(0, TestContext.Current.CancellationToken);
        await session.KeepAsync();
        await session.DiscardAsync();
        await session.KeepAsync();
        Assert.True(File.Exists(a));

        var plan = TriageHandOff.Plan(session, new Leviathan.ViewModels.Settings.MediaSettings(), library);
        var result = await actions.MoveKeptAsync(plan!.Files, plan.Folder, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Moved);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(c));
        Assert.Equal("A", File.ReadAllText(Path.Combine(library, "a.mkv")));
        Assert.Equal([TagIds.Green], await new AlternateStreamTagStore().GetAsync(Path.Combine(library, "c.mkv"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tags_RoundTripThroughTheStream_AndLeaveTheModifiedTimeAlone()
    {
        var path = MakeFile("a.mkv");
        var stamp = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        var store = new AlternateStreamTagStore();

        await store.SetAsync(path, new HashSet<int> { 4, 2 }, TestContext.Current.CancellationToken);

        Assert.Equal([2, 4], (await store.GetAsync(path, TestContext.Current.CancellationToken)).Order());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.True(File.Exists(path + ":leviathan.tags"));
        Assert.Equal("video bytes", File.ReadAllText(path));
    }

    [Fact]
    public async Task AFileWithNoStream_HasNoTags()
    {
        var path = MakeFile("a.mkv");

        Assert.Empty(await new AlternateStreamTagStore().GetAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SettingNoTags_RemovesTheStream()
    {
        var path = MakeFile("a.mkv");
        var store = new AlternateStreamTagStore();
        await store.SetAsync(path, new HashSet<int> { 1 }, TestContext.Current.CancellationToken);

        await store.SetAsync(path, new HashSet<int>(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(path + ":leviathan.tags"));
        Assert.Empty(await store.GetAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASetOnAReadOnlyFile_Works_AndKeepsItReadOnly()
    {
        var path = MakeFile("a.mkv");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var store = new AlternateStreamTagStore();

        await store.SetAsync(path, new HashSet<int> { 4 }, TestContext.Current.CancellationToken);

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal([4], await store.GetAsync(path, TestContext.Current.CancellationToken));
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public async Task ADamagedStream_ReadsAsNoTags_AndTheNextWriteReplacesIt()
    {
        var path = MakeFile("a.mkv");
        await File.WriteAllTextAsync(path + ":leviathan.tags", "{ not json", TestContext.Current.CancellationToken);
        var store = new AlternateStreamTagStore();

        Assert.Empty(await store.GetAsync(path, TestContext.Current.CancellationToken));
        await store.SetAsync(path, new HashSet<int> { 3 }, TestContext.Current.CancellationToken);

        Assert.Equal([3], await store.GetAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AMissingFile_IsAFileNotFound_ForGetAndSet()
    {
        var store = new AlternateStreamTagStore();
        var missing = Path.Combine(_root, "nope.mkv");

        Assert.True(TagFiles.IsGone(await store.GetAsync(missing, TestContext.Current.CancellationToken)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.SetAsync(missing, new HashSet<int> { 1 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATagTravelsWithARenameOfTheFile()
    {
        var path = MakeFile("a.mkv");
        var store = new AlternateStreamTagStore();
        await store.SetAsync(path, new HashSet<int> { 4 }, TestContext.Current.CancellationToken);
        var renamed = Path.Combine(_root, "moved", "a.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);

        File.Move(path, renamed);

        Assert.Equal([4], await store.GetAsync(renamed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWholeSession_KeepsWithAMove_Discards_AndUndoesBoth_OnRealFiles()
    {
        var keepFolder = Path.Combine(_root, "keep");
        var bin = Path.Combine(_root, "bin");
        var a = MakeFile(@"inbox\a.mkv", "aaa");
        var b = MakeFile(@"inbox\b.mkv", "bbb");
        var store = new AlternateStreamTagStore();
        var actions = new TriageFileActions(new DiskFiles(bin), store, () => new TriageKeepOptions(keepFolder));
        var engine = new FakeVideoEngine();
        var session = new TriageSession([Entry(a), Entry(b)], engine, actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync(0, TestContext.Current.CancellationToken);

        await session.KeepAsync();
        await session.DiscardAsync();

        var keptPath = Path.Combine(keepFolder, "a.mkv");
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(keptPath));
        Assert.Equal([TagIds.Green], await store.GetAsync(keptPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(b));
        Assert.Single(Directory.GetFiles(bin));
        Assert.True(session.IsFinished);
        Assert.Equal(3, session.ReclaimedBytes);

        await session.UndoAsync();
        Assert.True(File.Exists(b));
        Assert.Empty(Directory.GetFiles(bin));

        await session.UndoAsync();
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(keptPath));
        Assert.Empty(await store.GetAsync(a, TestContext.Current.CancellationToken));
        Assert.Equal("aaa", File.ReadAllText(a));
    }

    [Fact]
    public async Task AKeepWhoseTargetNameIsTaken_FailsCleanly_LeavingTheFileAndNoTag()
    {
        var keepFolder = Path.Combine(_root, "keep");
        MakeFile(@"keep\a.mkv", "already here");
        var a = MakeFile(@"inbox\a.mkv", "aaa");
        var store = new AlternateStreamTagStore();
        var actions = new TriageFileActions(new DiskFiles(Path.Combine(_root, "bin")), store, () => new TriageKeepOptions(keepFolder));
        var session = new TriageSession([Entry(a)], new FakeVideoEngine(), actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync(0, TestContext.Current.CancellationToken);

        await session.KeepAsync();

        Assert.True(File.Exists(a));
        Assert.Contains("already exists", session.LastError);
        Assert.Empty(await store.GetAsync(a, TestContext.Current.CancellationToken));
        Assert.Equal(TriageDecision.None, session.Items[0].Decision);
    }
}
