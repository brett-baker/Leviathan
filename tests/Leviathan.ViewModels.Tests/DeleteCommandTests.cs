using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

public sealed class DeleteCommandTests
{
    private sealed class RecordingDeleter : IDeleteService
    {
        public List<(IReadOnlyList<LocationPath> Items, bool Permanent)> Calls { get; } = [];

        public Task<TransferOutcome> DeleteAsync(IReadOnlyList<LocationPath> items, bool permanent, CancellationToken cancellationToken)
        {
            Calls.Add((items, permanent));
            return Task.FromResult(new TransferOutcome(true, "Deleting…"));
        }
    }

    private static async Task<WindowCommandsTests.Harness> SelectedAsync(RecordingDeleter? deleter)
    {
        var h = new WindowCommandsTests.Harness(deleter: deleter);
        await h.Ready;
        h.Tab.Selection.Click(h.Tab.Items.Rows[0]);
        return h;
    }

    [Fact]
    public async Task Delete_SendsTheSelection_WithoutAskingFirst()
    {
        var deleter = new RecordingDeleter();
        var h = await SelectedAsync(deleter);

        Assert.Equal(CommandResult.Executed, await h.Run(CommandIds.EditDelete));

        var call = Assert.Single(deleter.Calls);
        Assert.False(call.Permanent);
        Assert.Equal(h.Tab.Items.Rows[0].Location, Assert.Single(call.Items));
        Assert.Empty(h.Shell.Confirmations);
        Assert.Equal("Deleting…", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task DeletePermanent_AsksFirst_AndDoesNothingWhenDeclined()
    {
        var deleter = new RecordingDeleter();
        var h = await SelectedAsync(deleter);
        h.Shell.Confirm = false;

        await h.Run(CommandIds.EditDeletePermanent);

        Assert.Single(h.Shell.Confirmations);
        Assert.Empty(deleter.Calls);
    }

    [Fact]
    public async Task DeletePermanent_WhenConfirmed_DeletesForGood()
    {
        var deleter = new RecordingDeleter();
        var h = await SelectedAsync(deleter);

        await h.Run(CommandIds.EditDeletePermanent);

        Assert.True(Assert.Single(deleter.Calls).Permanent);
        Assert.Contains("cannot be restored", h.Shell.Confirmations[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_IsDisabled_WithNothingSelected_OrWithoutAService()
    {
        var h = new WindowCommandsTests.Harness(deleter: new RecordingDeleter());
        await h.Ready;
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditDelete));

        var none = await SelectedAsync(null);
        Assert.Equal(CommandResult.Disabled, await none.Run(CommandIds.EditDelete));
    }

    [Fact]
    public void DeleteKeys_AreBound()
    {
        var map = WindowCommands.DefaultKeyMap();

        Assert.Equal(CommandIds.EditDelete, map.Resolve(KeyGesture.Parse("Del"), false));
        Assert.Equal(CommandIds.EditDeletePermanent, map.Resolve(KeyGesture.Parse("Shift+Del"), false));
    }
}
