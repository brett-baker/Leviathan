using Leviathan.Core.Compare;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Sync;
using Leviathan.ViewModels.Transfers;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>Starting a confirmed sync with the worker's ordinary jobs (spec §15.5).</summary>
public sealed class SyncRunnerTests
{
    private sealed class FakeTransfers : ITransferService
    {
        public List<TransferRequest> Started { get; } = [];

        public bool Refuse { get; set; }

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Started.Add(request);
            return Task.FromResult(Refuse ? new TransferOutcome(false, "The worker is not running.") : new TransferOutcome(true, "queued"));
        }
    }

    private sealed class FakeDeleter : IDeleteService
    {
        public List<(IReadOnlyList<LocationPath> Items, bool Permanent)> Deleted { get; } = [];

        public Task<TransferOutcome> DeleteAsync(IReadOnlyList<LocationPath> items, bool permanent, CancellationToken cancellationToken)
        {
            Deleted.Add((items, permanent));
            return Task.FromResult(new TransferOutcome(true, "queued"));
        }
    }

    private static SyncAction Copy(string path, bool toRight = true, bool isDirectory = false) =>
        new(path, toRight ? SyncActionKind.CopyToRight : SyncActionKind.CopyToLeft, isDirectory, 10, false, false, "test");

    private static SyncAction Delete(string path, bool right = true) =>
        new(path, right ? SyncActionKind.DeleteRight : SyncActionKind.DeleteLeft, false, 0, false, false, "test");

    private static SyncPlan Plan(params SyncAction[] actions) => new(SyncMode.MirrorLeftToRight, actions, [], []);

    private static (SyncRunner Runner, FakeTransfers Transfers, FakeDeleter Deleter) Make()
    {
        var transfers = new FakeTransfers();
        var deleter = new FakeDeleter();
        return (new SyncRunner(transfers, deleter), transfers, deleter);
    }

    [Fact]
    public async Task Copies_AreGroupedByTargetFolder_AndReplaceWithoutAsking()
    {
        var (runner, transfers, _) = Make();

        var outcome = await runner.RunAsync(Plan(Copy("a.txt"), Copy(@"sub\b.txt"), Copy(@"sub\c.txt"), Copy(@"sub\deep\d.txt")), @"D:\Left", @"E:\Right", CancellationToken.None);

        Assert.Equal(3, outcome.CopyJobs);
        Assert.All(transfers.Started, t => Assert.True(t.Replace));
        Assert.All(transfers.Started, t => Assert.Equal(TransferMode.Copy, t.Mode));
        var sub = transfers.Started.Single(t => t.Destination.Value == @"E:\Right\sub");
        Assert.Equal([@"D:\Left\sub\b.txt", @"D:\Left\sub\c.txt"], sub.Sources.Select(s => s.Value));
        Assert.Contains(transfers.Started, t => t.Destination.Value == @"E:\Right" && t.Sources[0].Value == @"D:\Left\a.txt");
        Assert.Contains(transfers.Started, t => t.Destination.Value == @"E:\Right\sub\deep");
    }

    [Fact]
    public async Task CopiesToTheLeft_GoTheOtherWay()
    {
        var (runner, transfers, _) = Make();

        await runner.RunAsync(Plan(Copy(@"x\y.txt", toRight: false)), @"D:\Left", @"E:\Right", CancellationToken.None);

        var job = Assert.Single(transfers.Started);
        Assert.Equal(@"E:\Right\x\y.txt", job.Sources[0].Value);
        Assert.Equal(@"D:\Left\x", job.Destination.Value);
    }

    [Fact]
    public async Task Deletions_AreSoftDeletes_ForTheRightAndTheLeftSeparately_AndQueuedBeforeTheCopies()
    {
        var (runner, transfers, deleter) = Make();

        var outcome = await runner.RunAsync(Plan(Delete("old.txt"), Delete(@"sub\older.txt"), Delete("left-extra.txt", right: false), Copy("a.txt")), @"D:\Left", @"E:\Right", CancellationToken.None);

        Assert.Equal(3, outcome.Deleted);
        Assert.Equal(2, deleter.Deleted.Count);
        Assert.All(deleter.Deleted, d => Assert.False(d.Permanent));
        Assert.Equal([@"E:\Right\old.txt", @"E:\Right\sub\older.txt"], deleter.Deleted[0].Items.Select(i => i.Value));
        Assert.Equal([@"D:\Left\left-extra.txt"], deleter.Deleted[1].Items.Select(i => i.Value));
        Assert.Single(transfers.Started);
    }

    [Fact]
    public async Task ATypeChange_IsNotStarted_ButListedForThePerson()
    {
        var (runner, transfers, deleter) = Make();

        var outcome = await runner.RunAsync(Plan(Delete("thing", right: true), Copy("thing"), Copy("fine.txt")), @"D:\Left", @"E:\Right", CancellationToken.None);

        Assert.Single(transfers.Started);
        Assert.Empty(deleter.Deleted);
        Assert.Contains(outcome.Left, l => l.StartsWith("thing:", StringComparison.Ordinal) && l.Contains("by hand", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedJob_IsReported_NotCounted()
    {
        var (runner, transfers, _) = Make();
        transfers.Refuse = true;

        var outcome = await runner.RunAsync(Plan(Copy("a.txt")), @"D:\Left", @"E:\Right", CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Equal(["The worker is not running."], outcome.Left);
    }

    [Fact]
    public async Task AnEmptyPlan_StartsNothing()
    {
        var (runner, transfers, deleter) = Make();

        var outcome = await runner.RunAsync(Plan(), @"D:\Left", @"E:\Right", CancellationToken.None);

        Assert.False(outcome.Started);
        Assert.Empty(transfers.Started);
        Assert.Empty(deleter.Deleted);
    }

    [Fact]
    public async Task TheCommand_PassesBothPanesOfASplitTab_AndTheOnlyFolderOtherwise()
    {
        var h = new Harness();
        await h.Ready;

        await h.Run(CommandIds.SyncFolders);

        var opened = Assert.Single(h.Shell.SyncWindows);
        Assert.Equal(TabTestSupport.P(@"C:\a"), opened.Left);
        Assert.Null(opened.Right);
    }
}
