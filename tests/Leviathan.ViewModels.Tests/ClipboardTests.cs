using System.IO.Abstractions.TestingHelpers;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.NewMenu;
using Leviathan.ViewModels.Transfers;

namespace Leviathan.ViewModels.Tests;

public sealed class ClipboardCommandTests
{
    internal sealed class FakeClipboard : IFileClipboard
    {
        private ClipboardItems? _held;

        public event EventHandler? Changed;

        public bool HasFiles => Stale || _held is not null;

        /// <summary>Reports files although the clipboard has been emptied since, as when another application clears it between the check and the read.</summary>
        public bool Stale { get; set; }

        public int Clears { get; private set; }

        public void Set(IReadOnlyList<LocationPath> items, ClipboardOperation operation)
        {
            _held = new ClipboardItems(items, operation);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            Clears++;
            _held = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<ClipboardItems?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_held);
    }

    private sealed class RecordingTransfers(bool started = true) : ITransferService
    {
        public List<TransferRequest> Requests { get; } = [];

        public Task<TransferOutcome> StartAsync(TransferRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TransferOutcome(started, started ? "Started" : "Not started"));
        }
    }

    private sealed class FolderOnlyCatalog : INewItemCatalog
    {
        public Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewItemTemplate>>([.. ShellNewCatalog.BuiltIn]);
    }

    private static LocationPath P(string path) => LocationPath.Parse(path);

    private static async Task<(WindowCommandsTests.Harness Harness, FakeClipboard Clipboard, RecordingTransfers Transfers, MockFileSystem Disk)> MakeAsync(bool started = true)
    {
        var disk = new MockFileSystem();
        disk.AddDirectory(@"C:\a\b");
        var clipboard = new FakeClipboard();
        var transfers = new RecordingTransfers(started);
        var h = new WindowCommandsTests.Harness(
            newItems: new NewItemService(new FolderOnlyCatalog(), new NewItemCreator(disk)),
            transfers: transfers,
            clipboard: clipboard);
        await h.Ready;
        return (h, clipboard, transfers, disk);
    }

    private static void Select(WindowCommandsTests.Harness h, string name) =>
        h.Tab.Selection.Click(h.Tab.Items.Rows.First(r => r.Name == name));

    [Fact]
    public async Task CutAndCopy_AreDisabled_WithNothingSelected()
    {
        var (h, _, _, _) = await MakeAsync();

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditCut));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditCopy));
    }

    [Fact]
    public async Task Copy_PutsTheSelectionOnTheClipboard_AsACopy()
    {
        var (h, clipboard, _, _) = await MakeAsync();
        Select(h, "1.txt");

        var result = await h.Run(CommandIds.EditCopy);

        Assert.Equal(CommandResult.Executed, result);
        var held = await clipboard.GetAsync(CancellationToken.None);
        Assert.Equal(ClipboardOperation.Copy, held!.Operation);
        Assert.Equal([P(@"C:\a\1.txt")], held.Items);
        Assert.Equal("Copied 1.txt.", h.Shell.Messages[^1]);
        Assert.Contains(h.Trail.Snapshot(), a => a.Command == "edit.copy");
    }

    [Fact]
    public async Task Cut_PutsTheSelectionOnTheClipboard_AsACut()
    {
        var (h, clipboard, _, _) = await MakeAsync();
        h.Tab.Selection.SelectAll();

        await h.Run(CommandIds.EditCut);

        var held = await clipboard.GetAsync(CancellationToken.None);
        Assert.Equal(ClipboardOperation.Cut, held!.Operation);
        Assert.Equal(2, held.Items.Count);
        Assert.Equal("Cut 2 items. Paste to move.", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task Paste_IsDisabled_UntilTheClipboardHoldsFiles()
    {
        var (h, clipboard, _, _) = await MakeAsync();

        Assert.False(h.Commands.CanExecute(CommandIds.EditPaste));
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.EditPaste));

        clipboard.Set([P(@"D:\x\1.txt")], ClipboardOperation.Copy);
        Assert.True(h.Commands.CanExecute(CommandIds.EditPaste));
    }

    [Fact]
    public async Task PastingACopy_Copies_EvenOnTheSameVolume()
    {
        var (h, clipboard, transfers, _) = await MakeAsync();
        clipboard.Set([P(@"C:\v\2.txt")], ClipboardOperation.Copy);

        await h.Run(CommandIds.EditPaste);

        var request = Assert.Single(transfers.Requests);
        Assert.Equal(TransferMode.Copy, request.Mode);
        Assert.Equal(P(@"C:\a"), request.Destination);
        Assert.Equal(0, clipboard.Clears);
        Assert.True(clipboard.HasFiles);
    }

    [Fact]
    public async Task PastingACopy_IntoItsOwnFolder_IsAllowed_SoItMakesAnotherCopy()
    {
        var (h, clipboard, transfers, _) = await MakeAsync();
        clipboard.Set([P(@"C:\a\1.txt")], ClipboardOperation.Copy);

        await h.Run(CommandIds.EditPaste);

        Assert.Equal(TransferMode.Copy, Assert.Single(transfers.Requests).Mode);
    }

    [Fact]
    public async Task PastingACut_Moves_AndThenEmptiesTheClipboard()
    {
        var (h, clipboard, transfers, _) = await MakeAsync();
        clipboard.Set([P(@"C:\v\2.txt")], ClipboardOperation.Cut);

        await h.Run(CommandIds.EditPaste);

        Assert.Equal(TransferMode.Move, Assert.Single(transfers.Requests).Mode);
        Assert.Equal(1, clipboard.Clears);
        Assert.False(h.Commands.CanExecute(CommandIds.EditPaste));
    }

    [Fact]
    public async Task PastingACut_WhoseTransferDidNotStart_KeepsTheClipboard()
    {
        var (h, clipboard, _, _) = await MakeAsync(started: false);
        clipboard.Set([P(@"C:\v\2.txt")], ClipboardOperation.Cut);

        await h.Run(CommandIds.EditPaste);

        Assert.Equal(0, clipboard.Clears);
        Assert.True(clipboard.HasFiles);
        Assert.Equal("Not started", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task PastingACut_IntoTheFolderItCameFrom_IsRefused_AndKeepsTheClipboard()
    {
        var (h, clipboard, transfers, _) = await MakeAsync();
        clipboard.Set([P(@"C:\a\1.txt")], ClipboardOperation.Cut);

        await h.Run(CommandIds.EditPaste);

        Assert.Empty(transfers.Requests);
        Assert.Equal("1.txt is already in a", h.Shell.Messages[^1]);
        Assert.True(clipboard.HasFiles);
    }

    [Fact]
    public async Task PastingAFolderIntoItself_IsRefused()
    {
        var (h, clipboard, transfers, _) = await MakeAsync();
        await h.Run(CommandIds.NavGo, @"C:\a\b");
        clipboard.Set([P(@"C:\a")], ClipboardOperation.Copy);

        await h.Run(CommandIds.EditPaste);

        Assert.Empty(transfers.Requests);
        Assert.Contains("inside it", h.Shell.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasteIntoNewFolder_CreatesTheFolder_AndPastesThere()
    {
        var (h, clipboard, transfers, disk) = await MakeAsync();
        clipboard.Set([P(@"C:\a\1.txt")], ClipboardOperation.Cut);

        await h.Run(CommandIds.EditPasteIntoNewFolder);

        var request = Assert.Single(transfers.Requests);
        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.StartsWith(@"C:\a\New folder", request.Destination.Value, StringComparison.Ordinal);
        Assert.True(disk.Directory.Exists(request.Destination.Value));
    }

    [Fact]
    public async Task PasteIntoNewFolder_Refused_CreatesNoFolder()
    {
        var (h, clipboard, transfers, disk) = await MakeAsync();
        await h.Run(CommandIds.NavGo, @"C:\a\b");
        clipboard.Set([P(@"C:\a")], ClipboardOperation.Copy);

        await h.Run(CommandIds.EditPasteIntoNewFolder);

        Assert.Empty(transfers.Requests);
        Assert.Empty(disk.Directory.GetDirectories(@"C:\a\b"));
    }

    [Fact]
    public async Task Paste_WhenTheClipboardWasEmptiedMeanwhile_SaysSo_AndChangesNothing()
    {
        var (h, clipboard, transfers, _) = await MakeAsync();
        clipboard.Stale = true;

        var result = await h.Run(CommandIds.EditPaste);

        Assert.Equal(CommandResult.Executed, result);
        Assert.Empty(transfers.Requests);
        Assert.Equal("There are no files on the clipboard to paste.", h.Shell.Messages[^1]);
    }

    [Fact]
    public void TheKeys_AreBound()
    {
        var keys = WindowCommands.DefaultKeyMap();

        Assert.Equal(CommandIds.EditCut, keys.Resolve(KeyGesture.Parse("Ctrl+X"), isSplit: false));
        Assert.Equal(CommandIds.EditCopy, keys.Resolve(KeyGesture.Parse("Ctrl+C"), isSplit: false));
        Assert.Equal(CommandIds.EditPaste, keys.Resolve(KeyGesture.Parse("Ctrl+V"), isSplit: false));
    }
}
