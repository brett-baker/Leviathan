using System.IO.Abstractions.TestingHelpers;
using Leviathan.Diagnostics.Crash;
using Leviathan.Testing;
using Leviathan.ViewModels.Diagnostics;

namespace Leviathan.ViewModels.Tests;

/// <summary>The crash list of the Diagnostics view (spec §20.4): grouped by signature, Copy for Claude, Open folder.</summary>
public sealed class CrashListTests
{
    private const string Root = @"C:\crashes";

    private static CrashReport Report(string id, string signature, DateTimeOffset when, string summary = "Worker crashed: IOException", IncidentKind kind = IncidentKind.Crash) => new()
    {
        Id = id,
        Kind = kind,
        Process = "Worker",
        Timestamp = when,
        Summary = summary,
        Signature = signature,
        Build = new BuildInfo("1.0", "abc", null),
    };

    private static (CrashList List, CrashBundleStore Store, MockFileSystem Fs, ManualTimeProvider Time) Setup()
    {
        var fs = new MockFileSystem();
        var time = new ManualTimeProvider();
        var store = new CrashBundleStore(fs, Root);
        return (new CrashList(store, time), store, fs, time);
    }

    [Fact]
    public void RepeatsOfOneBug_AreOneRow_WithACount()
    {
        var (list, store, _, time) = Setup();
        var now = time.GetUtcNow();
        store.Write(Report("a1", "sigA", now.AddDays(-4)));
        store.Write(Report("a2", "sigA", now.AddHours(-2)));
        store.Write(Report("b1", "sigB", now.AddMinutes(-10), "UI hung", IncidentKind.Hang));

        var rows = list.Rows();

        Assert.Equal(["sigB", "sigA"], rows.Select(r => r.Signature));
        Assert.Equal("once · last 10 min ago · Worker · Hang", rows[0].Detail);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal("2 times · last 2 h ago · first 4 d ago · Worker · Crash", rows[1].Detail);
    }

    [Fact]
    public void NoBundles_GiveNoRows()
    {
        Assert.Empty(Setup().List.Rows());
    }

    [Fact]
    public void CopyForClaude_IsTheReportOfTheLatestOccurrence()
    {
        var (list, store, _, time) = Setup();
        store.Write(Report("old", "sigA", time.GetUtcNow().AddDays(-1), "older"));
        store.Write(Report("new", "sigA", time.GetUtcNow(), "newer"));

        var text = list.ReportFor("sigA");

        Assert.NotNull(text);
        Assert.Contains("newer", text);
        Assert.DoesNotContain("older", text);
        Assert.Null(list.ReportFor("nope"));
    }

    [Fact]
    public void AMissingMarkdownFile_IsRebuiltFromTheJson()
    {
        var (list, store, fs, time) = Setup();
        var folder = store.Write(Report("x1", "sigA", time.GetUtcNow(), "needs rebuilding"));
        fs.File.Delete(fs.Path.Combine(folder, CrashBundleStore.Files.ReportMarkdown));

        Assert.Contains("needs rebuilding", list.ReportFor("sigA"));
    }

    [Fact]
    public void OpenFolder_IsTheBundleOfTheLatestOccurrence()
    {
        var (list, store, _, time) = Setup();
        store.Write(Report("old", "sigA", time.GetUtcNow().AddDays(-1)));
        var newest = store.Write(Report("new", "sigA", time.GetUtcNow()));

        Assert.Equal(newest, list.FolderFor("sigA"));
        Assert.Null(list.FolderFor("nope"));
        Assert.Equal(Root, list.Root);
    }
}
