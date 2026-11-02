using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

public sealed class DropRulesTests
{
    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public void Decide_SameVolume_MovesByDefault()
    {
        var decision = DropRules.Decide([P(@"C:\a\1.txt")], P(@"C:\b"), DropModifiers.None);

        Assert.True(decision.Allowed);
        Assert.Equal(TransferMode.Move, decision.Mode);
        Assert.Equal("Move 1.txt to b", decision.Tooltip);
    }

    [Fact]
    public void Decide_OtherVolume_CopiesByDefault()
    {
        var decision = DropRules.Decide([P(@"C:\a\1.txt"), P(@"C:\a\2.txt")], P(@"D:\b"), DropModifiers.None);

        Assert.Equal(TransferMode.Copy, decision.Mode);
        Assert.Equal("Copy 2 items to b", decision.Tooltip);
    }

    [Fact]
    public void Decide_FromAShareToTheSameShare_Moves_ButToAnotherShareCopies()
    {
        var onShare = P(@"\\nas\media\x.mkv");

        Assert.Equal(TransferMode.Move, DropRules.Decide([onShare], P(@"\\nas\media\old"), DropModifiers.None).Mode);
        Assert.Equal(TransferMode.Copy, DropRules.Decide([onShare], P(@"\\nas\backup\old"), DropModifiers.None).Mode);
    }

    [Fact]
    public void Decide_ShiftForcesMove_AcrossVolumes_AndSaysTheOriginalIsRemovedOnlyAfterTheCheck()
    {
        var decision = DropRules.Decide([P(@"C:\a\1.txt")], P(@"D:\b"), DropModifiers.Shift);

        Assert.Equal(TransferMode.Move, decision.Mode);
        Assert.Contains("checked", decision.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_CtrlForcesCopy_OnTheSameVolume()
    {
        var decision = DropRules.Decide([P(@"C:\a\1.txt")], P(@"C:\b"), DropModifiers.Ctrl);

        Assert.Equal(TransferMode.Copy, decision.Mode);
    }

    [Fact]
    public void Decide_AltCreatesShortcuts_AndBeatsTheOtherModifiers()
    {
        var one = DropRules.Decide([P(@"C:\a\1.txt")], P(@"C:\b"), DropModifiers.Alt | DropModifiers.Ctrl);
        var many = DropRules.Decide([P(@"C:\a\1.txt"), P(@"C:\a\2.txt")], P(@"C:\b"), DropModifiers.Alt);

        Assert.Equal(TransferMode.Link, one.Mode);
        Assert.Equal("Create shortcut to 1.txt in b", one.Tooltip);
        Assert.Equal("Create shortcuts to 2 items in b", many.Tooltip);
    }

    [Fact]
    public void Decide_AFolderOntoItself_IsRefused()
    {
        var decision = DropRules.Decide([P(@"C:\a")], P(@"C:\a"), DropModifiers.None);

        Assert.False(decision.Allowed);
        Assert.Contains("onto itself", decision.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_AFolderIntoItsOwnSubfolder_IsRefused_EvenForACopy()
    {
        var decision = DropRules.Decide([P(@"C:\a")], P(@"C:\A\b\c"), DropModifiers.Ctrl);

        Assert.False(decision.Allowed);
        Assert.Contains("inside it", decision.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_AFolderWithASimilarName_IsNotItsAncestor()
    {
        var decision = DropRules.Decide([P(@"C:\a")], P(@"C:\ab"), DropModifiers.None);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Decide_MovingIntoTheFolderTheyAreAlreadyIn_IsRefused_ButCopyingIsFine()
    {
        var items = new[] { P(@"C:\a\1.txt") };

        Assert.False(DropRules.Decide(items, P(@"C:\a"), DropModifiers.None).Allowed);
        Assert.True(DropRules.Decide(items, P(@"C:\a"), DropModifiers.Ctrl).Allowed);
    }

    [Fact]
    public void Decide_UnknownItems_AreCopied_NotMoved()
    {
        var decision = DropRules.Decide(null, P(@"C:\b"), DropModifiers.None);

        Assert.True(decision.Allowed);
        Assert.Equal(TransferMode.Copy, decision.Mode);
        Assert.Equal("Copy to b", decision.Tooltip);
    }

    [Fact]
    public void Decide_AServerThatIsNotAShare_IsRefused()
    {
        var decision = DropRules.Decide([P(@"C:\a\1.txt")], P(@"\\nas"), DropModifiers.None);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Decide_ADriveRoot_IsAFineDestination()
    {
        var decision = DropRules.Decide([P(@"D:\a\1.txt")], P(@"C:\"), DropModifiers.None);

        Assert.True(decision.Allowed);
        Assert.Equal("Copy 1.txt to C:", decision.Tooltip);
    }
}

public sealed class DropCommandTests
{
    private sealed class RecordingTransfers : ITransferService
    {
        public List<TransferRequest> Requests { get; } = [];

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TransferOutcome(true, "Started"));
        }
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    [Fact]
    public async Task DropItems_HandsTheDecidedModeToTheTransferService()
    {
        var transfers = new RecordingTransfers();
        var h = new WindowCommandsTests.Harness(transfers: transfers);
        await h.Ready;

        var result = await h.Run(CommandIds.DropItems, @"D:\b", new ItemDrop([P(@"C:\a\1.txt")], DropModifiers.None));

        Assert.Equal(CommandResult.Executed, result);
        var request = Assert.Single(transfers.Requests);
        Assert.Equal(TransferMode.Copy, request.Mode);
        Assert.Equal(P(@"D:\b"), request.Destination);
        Assert.Equal("Started", h.Shell.Messages[^1]);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "item.drop");
    }

    [Fact]
    public async Task DropItems_RefusedDrop_ReachesNoOne_AndSaysWhy()
    {
        var transfers = new RecordingTransfers();
        var h = new WindowCommandsTests.Harness(transfers: transfers);
        await h.Ready;

        await h.Run(CommandIds.DropItems, @"C:\a", new ItemDrop([P(@"C:\a")], DropModifiers.None));

        Assert.Empty(transfers.Requests);
        Assert.Contains("onto itself", h.Shell.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropItems_WithoutATransferService_ChangesNothing_AndSaysSo()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        await h.Run(CommandIds.DropItems, @"C:\v", new ItemDrop([P(@"C:\a\1.txt")], DropModifiers.None));

        Assert.Contains("Nothing was changed", h.Shell.Messages[^1], StringComparison.Ordinal);
    }
}
