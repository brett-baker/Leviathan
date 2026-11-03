using System.IO.Abstractions.TestingHelpers;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Folders;
using Leviathan.ViewModels.Undo;
using Leviathan.Testing;

namespace Leviathan.ViewModels.Tests;

public sealed class UndoServiceTests
{
    internal sealed class MemoryStorage : IUndoStorage
    {
        public UndoState Stored { get; set; } = UndoState.Empty;

        public int Saves { get; private set; }

        public UndoState Load() => Stored;

        public void Save(UndoState state)
        {
            Stored = state;
            Saves++;
        }
    }

    /// <summary>Does what it is told and remembers what it was asked; a path in <see cref="Refuse"/> fails with a reason.</summary>
    internal sealed class FakeActions : IUndoActions
    {
        public List<IReadOnlyList<ItemMove>> Moves { get; } = [];

        public List<IReadOnlyList<string>> Deletes { get; } = [];

        public HashSet<string> Refuse { get; } = [];

        public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken)
        {
            Moves.Add(moves);
            return Task.FromResult(Result(moves, m => m.From, m => m));
        }

        public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            Deletes.Add(paths);
            return Task.FromResult(Result(paths, p => p, p => new ItemMove(p, p + ".again")));
        }

        private StepResult Result<T>(IReadOnlyList<T> items, Func<T, string> key, Func<T, ItemMove> done)
        {
            var ok = new List<ItemMove>();
            var problems = new List<string>();
            foreach (var item in items)
            {
                if (Refuse.Contains(key(item)))
                {
                    problems.Add($"'{key(item)}' is in the way.");
                }
                else
                {
                    ok.Add(done(item));
                }
            }

            return new StepResult(ok, problems);
        }
    }

    private static (UndoService Undo, FakeActions Actions, MemoryStorage Storage, ManualTimeProvider Time) Make(UndoState? stored = null)
    {
        var storage = new MemoryStorage { Stored = stored ?? UndoState.Empty };
        var actions = new FakeActions();
        var time = new ManualTimeProvider();
        return (new UndoService(storage, actions, time), actions, storage, time);
    }

    [Fact]
    public async Task Rename_UndoPutsTheOldNameBack_AndRedoRenamesAgain()
    {
        var (undo, actions, _, _) = Make();
        undo.RecordRename(@"C:\a\one.txt", @"C:\a\two.txt");

        var undone = await undo.UndoAsync(CancellationToken.None);
        Assert.True(undone.Done);
        Assert.Equal(new ItemMove(@"C:\a\two.txt", @"C:\a\one.txt"), Assert.Single(actions.Moves[0]));
        Assert.Equal([@"C:\a\one.txt"], undone.Touched);
        Assert.False(undo.CanUndo);
        Assert.True(undo.CanRedo);

        var redone = await undo.RedoAsync(CancellationToken.None);
        Assert.True(redone.Done);
        Assert.Equal(new ItemMove(@"C:\a\one.txt", @"C:\a\two.txt"), Assert.Single(actions.Moves[1]));
        Assert.True(undo.CanUndo);
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public async Task Delete_UndoRestoresFromTheBin_AndRedoDeletesAgainIntoANewPlace()
    {
        var (undo, actions, _, _) = Make();
        undo.RecordDelete([new ItemMove(@"C:\a\x.txt", @"C:\$Recycle.Bin\S\$R1.txt")]);

        await undo.UndoAsync(CancellationToken.None);
        Assert.Equal(new ItemMove(@"C:\$Recycle.Bin\S\$R1.txt", @"C:\a\x.txt"), Assert.Single(actions.Moves[0]));

        var redone = await undo.RedoAsync(CancellationToken.None);
        Assert.Equal([@"C:\a\x.txt"], actions.Deletes[0]);
        Assert.Equal([@"C:\a\x.txt"], redone.Touched);

        // The next undo restores from where the second delete put the item.
        await undo.UndoAsync(CancellationToken.None);
        Assert.Equal(new ItemMove(@"C:\a\x.txt.again", @"C:\a\x.txt"), Assert.Single(actions.Moves[1]));
    }

    [Fact]
    public async Task UndoGoesBackOneActionAtATime_NewestFirst()
    {
        var (undo, actions, _, _) = Make();
        undo.RecordRename(@"C:\a\1", @"C:\a\2");
        undo.RecordRename(@"C:\a\3", @"C:\a\4");
        Assert.Equal("Rename 3 to 4", undo.UndoTitle);

        await undo.UndoAsync(CancellationToken.None);
        await undo.UndoAsync(CancellationToken.None);

        Assert.Equal(@"C:\a\4", actions.Moves[0][0].From);
        Assert.Equal(@"C:\a\2", actions.Moves[1][0].From);
        Assert.Equal("Rename 1 to 2", undo.RedoTitle);
    }

    [Fact]
    public async Task ANewAction_EndsTheRedoHistory()
    {
        var (undo, _, _, _) = Make();
        undo.RecordRename(@"C:\a\1", @"C:\a\2");
        await undo.UndoAsync(CancellationToken.None);
        Assert.True(undo.CanRedo);

        undo.RecordRename(@"C:\a\5", @"C:\a\6");

        Assert.False(undo.CanRedo);
    }

    [Fact]
    public async Task WhenNothingCouldBeRestored_TheEntryStays_SoTheUserCanFixTheCauseAndTryAgain()
    {
        var (undo, actions, _, _) = Make();
        undo.RecordRename(@"C:\a\1", @"C:\a\2");
        actions.Refuse.Add(@"C:\a\2");

        var outcome = await undo.UndoAsync(CancellationToken.None);

        Assert.False(outcome.Done);
        Assert.Contains("in the way", outcome.Message, StringComparison.Ordinal);
        Assert.True(undo.CanUndo);
        Assert.False(undo.CanRedo);

        actions.Refuse.Clear();
        Assert.True((await undo.UndoAsync(CancellationToken.None)).Done);
    }

    [Fact]
    public async Task WhenOnlySomeItemsCouldBeRestored_ThoseAreRedoable_AndTheRestAreDroppedWithAnExplanation()
    {
        var (undo, actions, _, _) = Make();
        undo.RecordDelete([new ItemMove(@"C:\a\1", @"C:\bin\1"), new ItemMove(@"C:\a\2", @"C:\bin\2")]);
        actions.Refuse.Add(@"C:\bin\2");

        var outcome = await undo.UndoAsync(CancellationToken.None);

        Assert.True(outcome.Done);
        Assert.Contains("in the way", outcome.Message, StringComparison.Ordinal);
        Assert.False(undo.CanUndo);
        await undo.RedoAsync(CancellationToken.None);
        Assert.Equal([@"C:\a\1"], actions.Deletes[0]);
    }

    [Fact]
    public async Task NothingToUndoOrRedo_SaysSo()
    {
        var (undo, actions, _, _) = Make();

        Assert.False((await undo.UndoAsync(CancellationToken.None)).Done);
        Assert.False((await undo.RedoAsync(CancellationToken.None)).Done);
        Assert.Empty(actions.Moves);
        Assert.Null(undo.UndoTitle);
    }

    [Fact]
    public async Task DeleteTitle_NamesTheItem_OrCountsThem()
    {
        var (undo, _, _, _) = Make();

        undo.RecordDelete([new ItemMove(@"C:\a\x.txt", @"C:\bin\x")]);
        Assert.Equal("Delete x.txt", undo.UndoTitle);
        undo.RecordDelete([new ItemMove(@"C:\a\1", @"C:\bin\1"), new ItemMove(@"C:\a\2", @"C:\bin\2"), new ItemMove(@"\\nas\media\3", @"\\nas\media\.t\3")]);
        Assert.Equal("Delete 3 items", undo.UndoTitle);
        undo.RecordDelete([]);
        Assert.Equal("Delete 3 items", undo.UndoTitle);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheHistory_IsSavedOnEveryChange_AndSurvivesARestart()
    {
        var (undo, _, storage, time) = Make();
        undo.RecordRename(@"C:\a\1", @"C:\a\2");
        await undo.Saved;
        Assert.Single(storage.Stored.Undo);

        var again = new UndoService(storage, new FakeActions(), time);
        Assert.True(again.CanUndo);
        Assert.Equal("Rename 1 to 2", again.UndoTitle);

        await again.UndoAsync(CancellationToken.None);
        await again.Saved;
        Assert.Empty(storage.Stored.Undo);
        Assert.Single(storage.Stored.Redo);
        Assert.True(new UndoService(storage, new FakeActions(), time).CanRedo);
    }

    [Fact]
    public void EntriesOlderThanAWeek_AreDroppedWhenLoaded()
    {
        var time = new ManualTimeProvider();
        var old = new UndoEntry(Guid.NewGuid(), UndoKind.Rename, "Rename a to b", time.GetUtcNow() - UndoService.Lifetime - TimeSpan.FromMinutes(1), [new ItemMove("a", "b")]);
        var fresh = new UndoEntry(Guid.NewGuid(), UndoKind.Rename, "Rename c to d", time.GetUtcNow() - TimeSpan.FromDays(1), [new ItemMove("c", "d")]);

        var undo = new UndoService(new MemoryStorage { Stored = new UndoState([old, fresh], [old]) }, new FakeActions(), time);

        Assert.Equal("Rename c to d", undo.UndoTitle);
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void TheHistory_IsCapped()
    {
        var (undo, _, _, _) = Make();
        for (var i = 0; i < UndoService.Capacity + 20; i++)
        {
            undo.RecordRename($@"C:\a\{i}", $@"C:\a\x{i}");
        }

        Assert.Equal($"Rename {UndoService.Capacity + 19} to x{UndoService.Capacity + 19}", undo.UndoTitle);
    }

    [Fact]
    public async Task ADamagedStorage_DoesNotStopTheAction()
    {
        var storage = new ThrowingStorage();
        using var undo = new UndoService(storage, new FakeActions());

        undo.RecordRename(@"C:\a\1", @"C:\a\2");
        await undo.Saved;

        Assert.True(undo.CanUndo);
    }

    [Fact]
    public async Task Changed_IsRaised_ByRecordUndoAndRedo()
    {
        var (undo, _, _, _) = Make();
        var raised = 0;
        undo.Changed += (_, _) => raised++;

        undo.RecordRename(@"C:\a\1", @"C:\a\2");
        await undo.UndoAsync(CancellationToken.None);
        await undo.RedoAsync(CancellationToken.None);

        Assert.Equal(3, raised);
    }

    [Fact]
    public async Task TwoUndosAtOnce_RunOneAfterTheOther_SoAnEntryIsNeverUndoneTwice()
    {
        var (undo, actions, _, _) = Make();
        undo.RecordRename(@"C:\a\1", @"C:\a\2");

        var results = await Task.WhenAll(undo.UndoAsync(CancellationToken.None), undo.UndoAsync(CancellationToken.None));

        Assert.Single(actions.Moves);
        Assert.Single(results, r => r.Done);
    }

    private sealed class ThrowingStorage : IUndoStorage
    {
        public UndoState Load() => UndoState.Empty;

        public void Save(UndoState state) => throw new IOException("disk full");
    }
}

public sealed class UndoCommandTests
{
    private static (WindowCommandsTests.Harness Harness, UndoService Undo, UndoServiceTests.FakeActions Actions, MockFileSystem Disk) Make()
    {
        var disk = new MockFileSystem();
        disk.AddFile(@"C:\a\1.txt", new MockFileData("x"));
        disk.AddDirectory(@"C:\a\b");
        var actions = new UndoServiceTests.FakeActions();
        var undo = new UndoService(new UndoServiceTests.MemoryStorage(), actions);
        return (new WindowCommandsTests.Harness(renamer: new RenameService(disk), undo: undo), undo, actions, disk);
    }

    [Fact]
    public async Task UndoAndRedo_AreDisabled_UntilThereIsSomethingToUndo()
    {
        var (h, undo, _, _) = Make();
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditUndo));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditRedo));

        undo.RecordRename(@"C:\a\x", @"C:\a\y");
        Assert.True(h.Commands.CanExecute(CommandIds.EditUndo));
        Assert.False(h.Commands.CanExecute(CommandIds.EditRedo));
    }

    [Fact]
    public async Task ARename_IsRecorded_AndCtrlZUndoesIt()
    {
        var (h, undo, actions, _) = Make();
        await h.Ready;
        h.Tab.Selection.Click(h.Tab.Items.Rows.First(r => r.Name == "1.txt"));
        await h.Run(CommandIds.EditRename);
        await h.Run(CommandIds.RenameCommit, "renamed.txt");

        Assert.Equal("Rename 1.txt to renamed.txt", undo.UndoTitle);

        var result = await h.Run(CommandIds.EditUndo);

        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal(new ItemMove(@"C:\a\renamed.txt", @"C:\a\1.txt"), Assert.Single(actions.Moves[0]));
        Assert.Equal("Undone: Rename 1.txt to renamed.txt", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task ARenameThatFailed_IsNotRecorded()
    {
        var (h, undo, _, _) = Make();
        await h.Ready;
        h.Tab.Selection.Click(h.Tab.Items.Rows.First(r => r.Name == "1.txt"));
        await h.Run(CommandIds.EditRename);

        await h.Run(CommandIds.RenameCommit, "b");

        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void TheKeys_AreBound()
    {
        var keys = WindowCommands.DefaultKeyMap();

        Assert.Equal(CommandIds.EditUndo, keys.Resolve(KeyGesture.Parse("Ctrl+Z"), isSplit: false));
        Assert.Equal(CommandIds.EditRedo, keys.Resolve(KeyGesture.Parse("Ctrl+Y"), isSplit: false));
        Assert.Equal(CommandIds.EditRedo, keys.Resolve(KeyGesture.Parse("Ctrl+Shift+Z"), isSplit: false));
    }

    [Fact]
    public async Task WithoutAnUndoService_TheCommandsAreDisabled()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditUndo));
    }
}
