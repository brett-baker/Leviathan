using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Settings;
using Leviathan.ViewModels.Transfers;
using static Leviathan.ViewModels.Tests.WindowCommandsTests;

namespace Leviathan.ViewModels.Tests;

/// <summary>Quick move and copy targets (spec §15.6).</summary>
public sealed class QuickTargetTests
{
    private sealed class ListTargets(params string[] slots) : IQuickTargets
    {
        public List<string> Items { get; } = [.. QuickTargets.Normalize(slots)];

        public IReadOnlyList<string> Slots => Items;

        public Task SetAsync(int slot, string folder, CancellationToken cancellationToken)
        {
            Items[slot - 1] = folder;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTransfers : ITransferService
    {
        public List<TransferRequest> Requests { get; } = [];

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TransferOutcome(true, "Started"));
        }
    }

    private static FileEntry File(string path) =>
        new(Path.GetFileName(path), LocationPath.Parse(path), FileEntryKind.File, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    [Fact]
    public void Normalize_GivesNineTrimmedSlots_PaddedOrCut()
    {
        Assert.Equal(["D:\a", "", "", "", "", "", "", "", ""], QuickTargets.Normalize([" D:\a ", null]).Take(9));
        Assert.Equal(9, QuickTargets.Normalize(Enumerable.Repeat("x", 20)).Count);
        Assert.Equal(9, QuickTargets.Normalize(null).Count);
    }

    [Fact]
    public void FolderOf_IsNull_ForAnEmptySlot_AnArchive_OrANumberOutOfRange()
    {
        var slots = QuickTargets.Normalize([@"D:\Photos", "", @"C:\a\pack.zip\inside"]);

        Assert.Equal(@"D:\Photos", QuickTargets.FolderOf(slots, 1)!.Value);
        Assert.Null(QuickTargets.FolderOf(slots, 2));
        Assert.Null(QuickTargets.FolderOf(slots, 3));
        Assert.Null(QuickTargets.FolderOf(slots, 0));
        Assert.Null(QuickTargets.FolderOf(slots, 10));
    }

    [Fact]
    public async Task Move_SendsTheSelectionToTheTarget_ThroughTheQueue()
    {
        var transfers = new RecordingTransfers();
        var h = new Harness(transfers: transfers, quickTargets: new ListTargets(@"D:\Photos", @"D:\Videos"));
        await h.Ready;
        var file = File(@"C:\a\1.txt");

        await h.Run(QuickTargets.MoveId(2), file.Location.Value, file);

        var request = Assert.Single(transfers.Requests);
        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.Equal(@"D:\Videos", request.Destination.Value);
        Assert.Equal([file.Location], request.Sources);
    }

    [Fact]
    public async Task Copy_UsesTheCopyMode()
    {
        var transfers = new RecordingTransfers();
        var h = new Harness(transfers: transfers, quickTargets: new ListTargets(@"D:\Photos"));
        await h.Ready;
        var file = File(@"C:\a\1.txt");

        await h.Run(QuickTargets.CopyId(1), file.Location.Value, file);

        Assert.Equal(TransferMode.Copy, Assert.Single(transfers.Requests).Mode);
    }

    [Fact]
    public async Task AnUnsetSlot_StartsNothing_AndSaysHowToSetIt()
    {
        var transfers = new RecordingTransfers();
        var h = new Harness(transfers: transfers, quickTargets: new ListTargets());
        await h.Ready;
        var file = File(@"C:\a\1.txt");

        await h.Run(QuickTargets.MoveId(4), file.Location.Value, file);

        Assert.Empty(transfers.Requests);
        Assert.Contains("Quick target 4 is not set", Assert.Single(h.Shell.Messages));
    }

    [Fact]
    public async Task MakeThisFolderTarget_StoresTheFolderShown()
    {
        var targets = new ListTargets();
        var h = new Harness(quickTargets: targets);
        await h.Ready;

        await h.Run(QuickTargets.SetId(3));

        Assert.Equal(@"C:\a", targets.Items[2]);
        Assert.Contains("Quick target 3 is now", Assert.Single(h.Shell.Messages));
    }

    [Fact]
    public async Task TargetsAreSaved_AndComeBackAfterARestart()
    {
        var disk = new MockFileSystem();
        using var store = new SettingsStore(disk, @"C:\data\settings.json");
        await store.LoadAsync(CancellationToken.None);
        await new SettingsQuickTargets(store).SetAsync(5, @"D:\Archive", CancellationToken.None);

        using var reopened = new SettingsStore(disk, @"C:\data\settings.json");
        await reopened.LoadAsync(CancellationToken.None);

        Assert.Equal(@"D:\Archive", new SettingsQuickTargets(reopened).Slots[4]);
        Assert.Equal(9, new SettingsQuickTargets(reopened).Slots.Count);
    }

    [Theory]
    [InlineData("Ctrl+Alt+1", 1, true)]
    [InlineData("Ctrl+Alt+9", 9, true)]
    [InlineData("Ctrl+Alt+Shift+3", 3, false)]
    public void TheKeys_AreCtrlAltDigit_ToMove_AndWithShiftToCopy(string gesture, int slot, bool move)
    {
        var expected = move ? QuickTargets.MoveId(slot) : QuickTargets.CopyId(slot);

        Assert.Equal(expected, WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse(gesture), false));
    }
}
