using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Compare;
using Leviathan.Testing;
using Leviathan.ViewModels.Sync;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

/// <summary>Checking saved sync pairs on their schedule (spec §15.5).</summary>
public sealed class SyncPairCheckerTests
{
    private static readonly DateTimeOffset Old = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeScanner(CompareResult result) : ICompareScanner
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public async Task<CompareResult> CompareAsync(string left, string right, CompareOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return Failure is { } failure ? throw failure : result;
        }
    }

    private sealed class FakeTransfers : ITransferService, IDeleteService
    {
        public int Copies { get; private set; }

        public int Deletes { get; private set; }

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Copies++;
            return Task.FromResult(new TransferOutcome(true, "queued"));
        }

        public Task<TransferOutcome> DeleteAsync(IReadOnlyList<Leviathan.Core.Paths.LocationPath> items, bool permanent, CancellationToken cancellationToken)
        {
            Deletes++;
            return Task.FromResult(new TransferOutcome(true, "queued"));
        }
    }

    private static CompareResult OneNewFile() => new([new CompareEntry("a.txt", false, new CompareSide(5, Old), null, CompareStatus.LeftOnly)], []);

    private static (SyncPairChecker Checker, SyncPairStore Store, FakeScanner Scanner, FakeTransfers Transfers, ManualTimeProvider Clock) Make(CompareResult? result = null)
    {
        var clock = new ManualTimeProvider();
        var store = new SyncPairStore(new MockFileSystem(), @"C:\Data\pairs.json");
        var scanner = new FakeScanner(result ?? OneNewFile());
        var transfers = new FakeTransfers();
        return (new SyncPairChecker(store, scanner, new SyncRunner(transfers, transfers), clock), store, scanner, transfers, clock);
    }

    private static SyncPair Pair(SyncMode mode, int every = 60, bool auto = false, DateTimeOffset? created = null) =>
        new() { Name = "P", Left = @"D:\L", Right = @"E:\R", Mode = mode, CheckEveryMinutes = every, AutoRun = auto, Created = created ?? Old.AddDays(-1) };

    [Fact]
    public async Task ADuePair_IsChecked_AndWhatIsWaitingIsRecorded_WithoutChangingAnyFile()
    {
        var (checker, store, scanner, transfers, _) = Make();
        store.Save(Pair(SyncMode.TwoWay));

        var checkedCount = await checker.CheckDueAsync(CancellationToken.None);

        Assert.Equal(1, checkedCount);
        Assert.Equal(1, scanner.Calls);
        Assert.Equal("1 change waiting", store.Items[0].LastResult);
        Assert.NotNull(store.Items[0].LastChecked);
        Assert.Equal(0, transfers.Copies + transfers.Deletes);
    }

    [Fact]
    public async Task APairNotYetDue_IsLeftAlone()
    {
        var (checker, store, scanner, _, clock) = Make();
        store.Save(Pair(SyncMode.TwoWay, created: clock.GetUtcNow()));

        Assert.Equal(0, await checker.CheckDueAsync(CancellationToken.None));
        Assert.Equal(0, scanner.Calls);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(1, await checker.CheckDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUpdatePairSetToRunOnItsOwn_QueuesItsCopies()
    {
        var (checker, store, _, transfers, _) = Make();
        store.Save(Pair(SyncMode.UpdateLeftToRight, auto: true));

        await checker.CheckDueAsync(CancellationToken.None);

        Assert.Equal(1, transfers.Copies);
        Assert.Contains("synced on its own", store.Items[0].LastResult);
    }

    [Theory]
    [InlineData(SyncMode.MirrorLeftToRight)]
    [InlineData(SyncMode.MirrorRightToLeft)]
    [InlineData(SyncMode.TwoWay)]
    public async Task AMirrorOrTwoWayPair_IsOnlyEverChecked_EvenWhenAskedToAutoRun(SyncMode mode)
    {
        var (checker, store, _, transfers, _) = Make();
        store.Save(Pair(mode, auto: true));

        await checker.CheckDueAsync(CancellationToken.None);

        Assert.Equal(0, transfers.Copies + transfers.Deletes);
        Assert.Contains("waiting", store.Items[0].LastResult);
    }

    [Fact]
    public async Task AFolderThatCannotBeCompared_IsRecordedAsTheReason_NotACrash()
    {
        var (checker, store, scanner, _, _) = Make();
        scanner.Failure = new IOException("The network path was not found.");
        store.Save(Pair(SyncMode.TwoWay));

        await checker.CheckDueAsync(CancellationToken.None);

        Assert.Equal("could not check: The network path was not found.", store.Items[0].LastResult);
    }

    [Fact]
    public async Task ASlowCheck_IsNotStartedAgainByTheNextTick()
    {
        var (checker, store, scanner, _, _) = Make();
        scanner.Gate = new TaskCompletionSource();
        store.Save(Pair(SyncMode.TwoWay));

        var first = checker.CheckDueAsync(CancellationToken.None);
        var second = await checker.CheckDueAsync(CancellationToken.None);
        scanner.Gate.SetResult();
        await first;

        Assert.Equal(0, second);
        Assert.Equal(1, scanner.Calls);
    }

    [Fact]
    public async Task CheckNow_IgnoresTheSchedule_AndReturnsWhatWasFound()
    {
        var (checker, store, _, _, _) = Make(new CompareResult([], []));
        var pair = Pair(SyncMode.TwoWay, every: 0);
        store.Save(pair);

        var found = await checker.CheckNowAsync(pair, CancellationToken.None);

        Assert.Equal("up to date", found);
        Assert.Equal("up to date", store.Items[0].LastResult);
    }

    [Fact]
    public async Task ACancelledCheck_SaysSo_AndRethrows()
    {
        var (checker, store, scanner, _, _) = Make();
        scanner.Failure = new OperationCanceledException();
        store.Save(Pair(SyncMode.TwoWay));

        await Assert.ThrowsAsync<OperationCanceledException>(() => checker.CheckDueAsync(CancellationToken.None));

        Assert.Equal("check stopped", store.Items[0].LastResult);
    }
}
