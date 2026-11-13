using Leviathan.Core.Rename;
using Leviathan.ViewModels.Rename;
using Leviathan.ViewModels.Undo;

namespace Leviathan.ViewModels.Tests;

/// <summary>The bulk-rename dialog's state, presets and undo (spec §15.3).</summary>
public sealed class BulkRenameModelTests
{
    private static readonly DateTime Modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RenameItem Item(string name) => new(@"C:\p\" + name, false, Modified);

    private static BulkRenameModel Model(params string[] names) =>
        new([.. names.Select(Item)], _ => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));

    private static RenameView View(BulkRenameModel model) => model.Compute(model.Snapshot());

    /// <summary>A disk in memory: a move needs its source to exist and its target not to (apart from a change of case), like the restore job.</summary>
    private sealed class MemoryDisk(IEnumerable<string> files) : IUndoActions
    {
        public HashSet<string> Files { get; } = new(files, StringComparer.OrdinalIgnoreCase);

        public Task<StepResult> MoveAsync(IReadOnlyList<ItemMove> moves, CancellationToken cancellationToken)
        {
            var done = new List<ItemMove>();
            var problems = new List<string>();
            foreach (var move in moves)
            {
                var onlyCase = string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase);
                if (!Files.Contains(move.From))
                {
                    problems.Add($"{move.From} is gone.");
                }
                else if (!onlyCase && Files.Contains(move.To))
                {
                    problems.Add($"{move.To} exists.");
                }
                else
                {
                    Files.Remove(move.From);
                    Files.Add(move.To);
                    done.Add(move);
                }
            }

            return Task.FromResult(new StepResult(done, problems));
        }

        public Task<StepResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryStorage : IUndoStorage
    {
        public UndoState Load() => UndoState.Empty;

        public void Save(UndoState state)
        {
        }
    }

    [Fact]
    public void Compute_ShowsTheNewNames_AndSkipsStepsThatDoNothing()
    {
        var model = Model("a.txt", "b.txt");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.FindReplace, Text = string.Empty });
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.FindReplace, Text = ".", Replace = "-", Target = RenameTarget.Name });

        var view = View(model);

        Assert.Equal(["a-txt", "b-txt"], view.Rows.Select(r => r.NewName));
        Assert.True(view.CanApply);
        Assert.Equal(2, view.ChangeCount);
    }

    [Fact]
    public void AConflictWithAFileThatStays_BlocksApplying_AndNamesTheProblem()
    {
        var model = new BulkRenameModel([Item("a.txt")], _ => new HashSet<string>(["a.txt", "b.txt"], StringComparer.OrdinalIgnoreCase));
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.FindReplace, Text = "a", Replace = "b" });

        var view = View(model);

        Assert.False(view.CanApply);
        Assert.Equal(1, view.ProblemCount);
        Assert.Contains("already exists", view.Rows[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntickedItem_KeepsItsName_IsSkippedByNumbering_AndItsNameStaysTaken()
    {
        var model = Model("a.txt", "b.txt", "c.txt");
        model.Drafts.Add(RenameDraft.Create(DraftKind.Number));
        model.SetIncluded(@"C:\p\b.txt", false);

        var view = View(model);

        Assert.Equal(["a 01.txt", "b.txt", "c 02.txt"], view.Rows.Select(r => r.NewName));
        Assert.False(view.Rows[1].Included);

        // A name the left-out item holds is still in the way.
        var clash = Model("a.txt", "x.txt");
        clash.Drafts.Add(new RenameDraft { Kind = DraftKind.Template, Text = "x" });
        clash.SetIncluded(@"C:\p\x.txt", false);
        Assert.False(View(clash).CanApply);
    }

    [Fact]
    public void NothingTicked_CannotApply()
    {
        var model = Model("a.txt");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.Case, Style = CaseStyle.Upper });
        model.SetIncluded(@"C:\p\a.txt", false);

        Assert.False(View(model).CanApply);
    }

    [Fact]
    public void UsesMediaTokens_IsTrueOnlyWhenAnActiveStepNeedsThem()
    {
        var model = Model("a.mp4");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.Template, Text = "{name}" });
        Assert.False(model.UsesMediaTokens);

        model.Drafts.Add(new RenameDraft { Kind = DraftKind.Insert, Text = "{resolution} " });
        Assert.True(model.UsesMediaTokens);
    }

    [Fact]
    public void SetMedia_LetsTheMediaTokensWork()
    {
        var model = Model("a.mp4");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.Insert, Text = "{resolution} " });
        Assert.Equal(RenameStatus.Invalid, View(model).Rows[0].Status);

        model.SetMedia(new Dictionary<string, MediaFacts> { [@"C:\p\a.mp4"] = new(Width: 1920, Height: 1080) });

        Assert.Equal("1920x1080 a.mp4", View(model).Rows[0].NewName);
    }

    [Fact]
    public async Task Apply_RenamesOnTheDisk_AndRecordsOneUndoForTheBatch()
    {
        var disk = new MemoryDisk([@"C:\p\a.txt", @"C:\p\b.txt", @"C:\p\c.txt"]);
        using var undo = new UndoService(new MemoryStorage(), disk);
        var model = Model("a.txt", "b.txt", "c.txt");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.Insert, Text = "x-" });

        var result = await BulkRenameModel.ApplyAsync(View(model), disk, undo.RecordBulkRename, CancellationToken.None);

        Assert.Equal(3, result.Renamed);
        Assert.Empty(result.Problems);
        Assert.True(disk.Files.SetEquals([@"C:\p\x-a.txt", @"C:\p\x-b.txt", @"C:\p\x-c.txt"]));
        Assert.Equal("Rename 3 items", undo.UndoTitle);

        var undone = await undo.UndoAsync(CancellationToken.None);

        Assert.True(undone.Done);
        Assert.True(disk.Files.SetEquals([@"C:\p\a.txt", @"C:\p\b.txt", @"C:\p\c.txt"]));

        var redone = await undo.RedoAsync(CancellationToken.None);

        Assert.True(redone.Done);
        Assert.True(disk.Files.SetEquals([@"C:\p\x-a.txt", @"C:\p\x-b.txt", @"C:\p\x-c.txt"]));
    }

    [Fact]
    public async Task ASwap_CanBeAppliedUndoneAndRedone_ThroughTemporaryNames()
    {
        var disk = new MemoryDisk([@"C:\p\a.txt", @"C:\p\b.txt", @"C:\p\c.txt"]);
        using var undo = new UndoService(new MemoryStorage(), disk);
        var model = Model("a.txt", "b.txt", "c.txt");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.NameList, Text = "b.txt\nc.txt\na.txt" });

        var result = await BulkRenameModel.ApplyAsync(View(model), disk, undo.RecordBulkRename, CancellationToken.None);

        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Renamed);
        Assert.True(disk.Files.SetEquals([@"C:\p\a.txt", @"C:\p\b.txt", @"C:\p\c.txt"]));
        Assert.DoesNotContain(disk.Files, f => f.Contains(".leviathan-rename-", StringComparison.Ordinal));

        Assert.True((await undo.UndoAsync(CancellationToken.None)).Done);
        Assert.True(disk.Files.SetEquals([@"C:\p\a.txt", @"C:\p\b.txt", @"C:\p\c.txt"]));
        Assert.DoesNotContain(disk.Files, f => f.Contains(".leviathan-rename-", StringComparison.Ordinal));

        Assert.True((await undo.RedoAsync(CancellationToken.None)).Done);
        Assert.DoesNotContain(disk.Files, f => f.Contains(".leviathan-rename-", StringComparison.Ordinal));
        Assert.True((await undo.UndoAsync(CancellationToken.None)).Done);
        Assert.True(disk.Files.SetEquals([@"C:\p\a.txt", @"C:\p\b.txt", @"C:\p\c.txt"]));
    }

    [Fact]
    public async Task Apply_WithAConflict_DoesNothing()
    {
        var disk = new MemoryDisk([@"C:\p\a.txt", @"C:\p\b.txt"]);
        var model = Model("a.txt");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.FindReplace, Text = "a", Replace = "b" });
        var recorded = 0;

        var result = await BulkRenameModel.ApplyAsync(View(model), disk, _ => recorded++, CancellationToken.None);

        Assert.Equal(0, result.Renamed);
        Assert.NotEmpty(result.Problems);
        Assert.Equal(0, recorded);
        Assert.Contains(@"C:\p\a.txt", disk.Files);
    }

    [Fact]
    public async Task Apply_WhenOneFileWentMissing_RenamesTheRest_AndSaysWhichFailed()
    {
        var disk = new MemoryDisk([@"C:\p\a.txt", @"C:\p\c.txt"]);
        using var undo = new UndoService(new MemoryStorage(), disk);
        var model = Model("a.txt", "b.txt", "c.txt");
        model.Drafts.Add(new RenameDraft { Kind = DraftKind.Insert, Text = "n-" });

        var result = await BulkRenameModel.ApplyAsync(View(model), disk, undo.RecordBulkRename, CancellationToken.None);

        Assert.Equal(2, result.Renamed);
        Assert.Single(result.Problems);
        Assert.Equal("Rename 2 items", undo.UndoTitle);
    }

    [Fact]
    public void Presets_SaveACopy_ReplaceByName_AndComeBackAsCopies()
    {
        var store = new MemoryPresetStore();
        var presets = new RenamePresets(store);
        var step = new RenameDraft { Kind = DraftKind.Insert, Text = "x" };

        Assert.True(presets.Save("Prefix", [step]));
        step.Text = "changed after saving";
        Assert.False(presets.Save("  ", [step]));
        Assert.Equal("x", presets.Steps("prefix")![0].Text);

        presets.Save("PREFIX", [new RenameDraft { Kind = DraftKind.Case }]);

        Assert.Single(presets.All);
        Assert.Equal(DraftKind.Case, presets.Steps("Prefix")![0].Kind);
        Assert.True(presets.Delete("prefix"));
        Assert.Empty(presets.All);
        Assert.Null(presets.Steps("prefix"));
    }

    [Fact]
    public async Task Presets_SaveAsync_WritesWhatIsHeld_AndANewInstanceReadsItBack()
    {
        var store = new MemoryPresetStore();
        var presets = new RenamePresets(store);
        presets.Save("Tidy", [new RenameDraft { Kind = DraftKind.Trim, Flag = true }, new RenameDraft { Kind = DraftKind.Case, Style = CaseStyle.Title }]);

        await presets.SaveAsync();

        var again = new RenamePresets(store);
        Assert.Equal(["Trim", "Case"], again.Steps("Tidy")!.Select(s => s.Kind.ToString()));
        Assert.Equal(CaseStyle.Title, again.Steps("Tidy")![1].Style);
    }

    [Fact]
    public void ADraftBecomesTheMatchingOperation()
    {
        Assert.IsType<NameList>(new RenameDraft { Kind = DraftKind.NameList, Text = "a\r\nb\n\nc" }.ToOperation());
        Assert.Equal(["a", "b", "c"], ((NameList)new RenameDraft { Kind = DraftKind.NameList, Text = "a\r\nb\n\nc" }.ToOperation()).Names);
        var number = (NumberSequence)RenameDraft.Create(DraftKind.Number).ToOperation();
        Assert.Equal((1, 1, 2), (number.Start, number.Step, number.Padding));
        Assert.False(new RenameDraft { Kind = DraftKind.Insert }.IsActive);
    }

    private sealed class MemoryPresetStore : IRenamePresetStore
    {
        private IReadOnlyList<RenamePreset> _saved = [];

        public IReadOnlyList<RenamePreset> Load() => _saved;

        public void Save(IReadOnlyList<RenamePreset> presets) => _saved = presets;
    }
}
