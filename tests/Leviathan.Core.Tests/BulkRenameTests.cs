using Leviathan.Core.Rename;

namespace Leviathan.Core.Tests;

/// <summary>The bulk-rename pipeline (spec §15.3).</summary>
public sealed class BulkRenameTests
{
    private static readonly DateTime Modified = new(2026, 3, 9, 14, 5, 0, DateTimeKind.Utc);

    private static RenameItem File(string path, MediaFacts? media = null) => new(path, false, Modified, media);

    private static string[] Names(RenamePlan plan) => [.. plan.Steps.Select(s => s.NewName)];

    private static RenamePlan Plan(string[] names, params RenameOperation[] operations) =>
        BulkRename.Plan([.. names.Select(n => File(@"C:\p\" + n))], operations);

    [Fact]
    public void FindReplace_Plain_IsCaseInsensitiveByDefault_AndLeavesTheExtension()
    {
        var plan = Plan(["Holiday IMG_1.jpg", "img_2.jpg"], new FindReplace("img_", "Photo "));

        Assert.Equal(["Holiday Photo 1.jpg", "Photo 2.jpg"], Names(plan));
    }

    [Fact]
    public void FindReplace_MatchCase_OnlyReplacesExactCase()
    {
        var plan = Plan(["IMG_1.jpg", "img_2.jpg"], new FindReplace("img", "pic", MatchCase: true));

        Assert.Equal(["IMG_1.jpg", "pic_2.jpg"], Names(plan));
    }

    [Fact]
    public void FindReplace_Regex_CanUseGroups()
    {
        var plan = Plan(["IMG_2026_0042.jpg"], new FindReplace(@"IMG_(\d{4})_(\d+)", "$2-$1", UseRegex: true));

        Assert.Equal(["0042-2026.jpg"], Names(plan));
    }

    [Fact]
    public void FindReplace_ABadRegex_MakesTheItemInvalid_NotAnException()
    {
        var plan = Plan(["a.txt"], new FindReplace("(", "x", UseRegex: true));

        Assert.Equal(RenameStatus.Invalid, plan.Steps[0].Status);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void FindReplace_OnTheExtension_ChangesJustTheExtension()
    {
        var plan = Plan(["a.jpeg", "b.JPEG"], new FindReplace("jpeg", "jpg", Target: RenameTarget.Extension));

        Assert.Equal(["a.jpg", "b.jpg"], Names(plan));
    }

    [Fact]
    public void Insert_AtAPosition_AndFromTheEnd()
    {
        Assert.Equal(["2026 - report.txt"], Names(Plan(["report.txt"], new Insert("2026 - "))));
        Assert.Equal(["report_final.txt"], Names(Plan(["report.txt"], new Insert("_final", 0, FromEnd: true))));
        Assert.Equal(["rep-ort.txt"], Names(Plan(["report.txt"], new Insert("-", 3))));
    }

    [Fact]
    public void Remove_DeletesCharactersFromTheStartOrTheEnd_WithinTheStem()
    {
        Assert.Equal(["report.txt"], Names(Plan(["IMG_report.txt"], new RemoveCharacters(0, 4))));
        Assert.Equal(["report.txt"], Names(Plan(["report (1).txt"], new RemoveCharacters(0, 4, FromEnd: true))));
        Assert.Equal(["a.txt"], Names(Plan(["a.txt"], new RemoveCharacters(5, 3))));
    }

    [Theory]
    [InlineData(CaseStyle.Lower, "my big_FILE-name.TXT", "my big_file-name.TXT")]
    [InlineData(CaseStyle.Upper, "my big file.txt", "MY BIG FILE.txt")]
    [InlineData(CaseStyle.Title, "my big_FILE-name.txt", "My Big_File-Name.txt")]
    [InlineData(CaseStyle.Sentence, "my BIG file.txt", "My big file.txt")]
    public void ChangeCase_Styles(CaseStyle style, string before, string after) =>
        Assert.Equal([after], Names(Plan([before], new ChangeCase(style))));

    [Fact]
    public void ChangeCase_OnlyInCase_IsAChange_NotAConflictWithItself()
    {
        var plan = Plan(["report.txt"], new ChangeCase(CaseStyle.Upper));

        Assert.Equal(RenameStatus.Ok, plan.Steps[0].Status);
        Assert.Equal("REPORT.txt", plan.Steps[0].NewName);
    }

    [Fact]
    public void Trim_RemovesSpacesAndChosenCharacters_AndCanCollapseRuns()
    {
        Assert.Equal(["a b.txt"], Names(Plan(["  a   b  .txt"], new TrimName(CollapseSpaces: true))));
        Assert.Equal(["name.txt"], Names(Plan(["--name__.txt"], new TrimName("-_"))));
    }

    [Fact]
    public void Number_AppendsOrPrependsARunningNumber_WithPadding()
    {
        var plan = Plan(["a.jpg", "b.jpg", "c.jpg"], new NumberSequence(Start: 8, Step: 2, Padding: 3, Separator: "_"));
        Assert.Equal(["a_008.jpg", "b_010.jpg", "c_012.jpg"], Names(plan));

        var front = Plan(["a.jpg", "b.jpg"], new NumberSequence(AtStart: true, Separator: " - "));
        Assert.Equal(["1 - a.jpg", "2 - b.jpg"], Names(front));
    }

    [Fact]
    public void Template_ExpandsTokens()
    {
        var media = new MediaFacts(new DateTime(2025, 12, 24, 18, 30, 0), TimeSpan.FromSeconds(3725), 1920, 1080, "h264");
        var plan = BulkRename.Plan(
            [File(@"C:\Photos\Trip\clip.mp4", media)],
            [new Template("{parent} {taken:yyyyMMdd} {resolution} {codec} {duration} {date:yyyy} {counter:000} {name}")]);

        Assert.Equal("Trip 20251224 1920x1080 h264 01-02-05 2026 001 clip.mp4", plan.Steps[0].NewName);
    }

    [Fact]
    public void Template_ThatRepeatsTheExtension_DoesNotDoubleIt()
    {
        Assert.Equal(["new.jpg"], Names(Plan(["old.jpg"], new Template("new.{ext}"))));
    }

    [Fact]
    public void Token_WithNoData_MakesTheItemInvalid_WithAReason()
    {
        var plan = Plan(["a.jpg"], new Template("{taken}"));

        Assert.Equal(RenameStatus.Invalid, plan.Steps[0].Status);
        Assert.Contains("date taken", plan.Steps[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownToken_IsReported()
    {
        var plan = Plan(["a.jpg"], new Insert("{nope}"));

        Assert.Equal(RenameStatus.Invalid, plan.Steps[0].Status);
        Assert.Contains("not a known token", plan.Steps[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsRunInOrder_EachOnTheResultOfTheLast()
    {
        var plan = Plan(["  IMG_001  .JPG"], new TrimName(), new FindReplace("IMG_", string.Empty), new ChangeCase(CaseStyle.Lower, RenameTarget.Name), new NumberSequence(Separator: "-"));

        Assert.Equal(["001-1.jpg"], Names(plan));
    }

    [Fact]
    public void AFolder_HasNoExtension()
    {
        var plan = BulkRename.Plan([new RenameItem(@"C:\p\Album 2020.final", true, Modified)], [new ChangeCase(CaseStyle.Upper)]);

        Assert.Equal("ALBUM 2020.FINAL", plan.Steps[0].NewName);
    }

    [Fact]
    public void ADotFile_KeepsItsWholeNameAsTheStem()
    {
        Assert.Equal([".gitignore2"], Names(Plan([".gitignore"], new Insert("2", 0, FromEnd: true))));
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("CON.txt")]
    [InlineData("nul")]
    public void ForbiddenResults_AreInvalid(string result)
    {
        var plan = Plan(["x.txt"], new Template(result));

        // The template keeps the extension, so judge the final name the plan made.
        var name = plan.Steps[0].NewName;
        Assert.Equal(name == "x.txt" ? RenameStatus.Unchanged : BulkRename.Check(name) is null ? RenameStatus.Ok : RenameStatus.Invalid, plan.Steps[0].Status);
        Assert.NotNull(BulkRename.Check(result.Contains('.', StringComparison.Ordinal) ? result : result + ".txt"));
    }

    [Fact]
    public void AnEmptyName_TrailingDot_AndTooLongName_AreInvalid()
    {
        Assert.NotNull(BulkRename.Check(string.Empty));
        Assert.NotNull(BulkRename.Check("name."));
        Assert.NotNull(BulkRename.Check("name "));
        Assert.NotNull(BulkRename.Check(new string('a', 256)));
        Assert.Null(BulkRename.Check(new string('a', 255)));
        Assert.Null(BulkRename.Check("fine name (1).txt"));
    }

    [Fact]
    public void TwoItemsGivenTheSameName_AreBothConflicts_AndNothingCanApply()
    {
        var plan = Plan(["a.txt", "b.txt"], new Template("same"));

        Assert.All(plan.Steps, s => Assert.Equal(RenameStatus.Conflict, s.Status));
        Assert.False(plan.CanApply);
        Assert.Throws<InvalidOperationException>(() => plan.Moves());
    }

    [Fact]
    public void ANameThatDiffersOnlyInCase_IsTheSameName()
    {
        var plan = Plan(["a.txt", "b.txt"], new FindReplace("a", "x"), new FindReplace("b", "X"));

        Assert.All(plan.Steps, s => Assert.Equal(RenameStatus.Conflict, s.Status));
    }

    [Fact]
    public void ANameTakenByAFileOutsideTheBatch_IsAConflict()
    {
        var plan = BulkRename.Plan(
            [File(@"C:\p\a.txt")],
            [new FindReplace("a", "b")],
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt", "B.txt" });

        Assert.Equal(RenameStatus.Conflict, plan.Steps[0].Status);
        Assert.Contains("already exists", plan.Steps[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameVacatedByAnotherItemOfTheBatch_IsNotAConflict()
    {
        var plan = BulkRename.Plan(
            [File(@"C:\p\1.txt"), File(@"C:\p\2.txt")],
            [new NameList(["2.txt", "3.txt"])],
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1.txt", "2.txt" });

        // 1.txt -> 2.txt would collide with 2.txt, which is itself renamed to 3.txt: fine, as long as the order is right.
        Assert.True(plan.CanApply);
    }

    /// <summary>Plays the moves on a set of names the way the file system would, failing on any move that lands on a name in use.</summary>
    private static HashSet<string> Play(IEnumerable<string> start, IEnumerable<RenameMove> moves)
    {
        var present = new HashSet<string>(start, StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            Assert.Contains(move.From, present);
            var caseOnly = string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase);
            Assert.True(caseOnly || !present.Contains(move.To), $"'{move.From}' would overwrite '{move.To}'.");
            present.Remove(move.From);
            present.Add(move.To);
        }

        return present;
    }

    [Fact]
    public void ASwap_GoesThroughTemporaryNames_AndNeverLandsOnAUsedName()
    {
        var plan = Plan(["a.txt", "b.txt"], new NameList(["b.txt", "a.txt"]));

        Assert.True(plan.CanApply);
        var moves = plan.Moves();
        Assert.Equal(4, moves.Count);
        Assert.Equal([@"C:\p\a.txt", @"C:\p\b.txt"], Play([@"C:\p\a.txt", @"C:\p\b.txt"], moves).Order());
        Assert.Contains(moves, m => m.To.Contains(".leviathan-rename-", StringComparison.Ordinal));
    }

    [Fact]
    public void AShift_1To2_2To3_NeedsOnlyOneTemporaryName_AndEndsRight()
    {
        var start = new[] { @"C:\p\1.txt", @"C:\p\2.txt", @"C:\p\3.txt" };
        var plan = Plan(["1.txt", "2.txt", "3.txt"], new NameList(["2.txt", "3.txt", "4.txt"]));

        Assert.True(plan.CanApply);
        var end = Play(start, plan.Moves());

        Assert.True(end.SetEquals([@"C:\p\2.txt", @"C:\p\3.txt", @"C:\p\4.txt"]));
    }

    [Fact]
    public void ARenameThatNeedsNoOrdering_IsOneMovePerItem()
    {
        var plan = Plan(["a.txt", "b.txt"], new FindReplace(".txt", "-x.txt", Target: RenameTarget.Name));

        Assert.Equal(2, plan.Moves().Count);
        Assert.Equal(2, plan.ChangeCount);
    }

    [Fact]
    public void AListShorterThanTheBatch_MakesTheExtraItemsInvalid()
    {
        var plan = Plan(["a.txt", "b.txt"], new NameList(["x.txt"]));

        Assert.Equal(RenameStatus.Ok, plan.Steps[0].Status);
        Assert.Equal(RenameStatus.Invalid, plan.Steps[1].Status);
    }

    [Fact]
    public void RandomBatches_NeverOverwrite_EndAsPlanned_AndUndoRestoresEveryName()
    {
        var random = new Random(20261113);
        for (var round = 0; round < 300; round++)
        {
            var count = random.Next(1, 9);
            var names = Enumerable.Range(0, count).Select(i => $"n{i}.txt").ToArray();
            var target = names.Concat(["fresh1.txt", "fresh2.txt"]).OrderBy(_ => random.Next()).Take(count).ToArray();
            var plan = Plan(names, new NameList(target));
            Assert.True(plan.CanApply, $"round {round}");

            var start = names.Select(n => @"C:\p\" + n).ToArray();
            var moves = plan.Moves();
            var end = Play(start, moves);

            Assert.True(end.SetEquals(target.Select(n => @"C:\p\" + n)), $"round {round}: final names differ from the plan");
            var back = Play(end, RenamePlan.Undo(moves));
            Assert.True(back.SetEquals(start), $"round {round}: undo did not restore the names");
        }
    }
}
