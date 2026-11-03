using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.ContextMenu;
using Leviathan.ViewModels.NewMenu;
using Leviathan.ViewModels.Transfers;
using System.IO.Abstractions.TestingHelpers;

namespace Leviathan.ViewModels.Tests;

public sealed class RightClickMenuTests
{
    private sealed class FolderOnlyCatalog : INewItemCatalog
    {
        public Task<IReadOnlyList<NewItemTemplate>> GetTemplatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewItemTemplate>>([.. ShellNewCatalog.BuiltIn]);
    }

    private static async Task<(WindowCommandsTests.Harness Harness, ClipboardCommandTests.FakeClipboard Clipboard)> MakeAsync()
    {
        var clipboard = new ClipboardCommandTests.FakeClipboard();
        var h = new WindowCommandsTests.Harness(
            newItems: new NewItemService(new FolderOnlyCatalog(), new NewItemCreator(new MockFileSystem())),
            renamer: new Folders.RenameService(new MockFileSystem()),
            deleter: new NoDelete(),
            clipboard: clipboard);
        await h.Ready;
        return (h, clipboard);
    }

    private sealed class NoDelete : IDeleteService
    {
        public Task<TransferOutcome> DeleteAsync(IReadOnlyList<LocationPath> items, bool permanent, CancellationToken cancellationToken) =>
            Task.FromResult(new TransferOutcome(true, "ok"));
    }

    private static IReadOnlyList<MenuEntry> Build(WindowCommandsTests.Harness h) =>
        RightClickMenu.Build(h.Tab.Selection.SelectedEntries, h.Tab.History.Current, h.Commands);

    private static string[] Titles(IEnumerable<MenuEntry> entries) =>
        [.. entries.Select(e => e.Kind == MenuEntryKind.Separator ? "-" : e.Title)];

    private static void Select(WindowCommandsTests.Harness h, string name) =>
        h.Tab.Selection.Click(h.Tab.Items.Rows.First(r => r.Name == name));

    [Fact]
    public async Task AFolder_OffersOpenOpenInNewTab_TheClipboardGroup_AndProperties_InThatOrder()
    {
        var (h, _) = await MakeAsync();
        Select(h, "b");

        var titles = Titles(Build(h));

        Assert.Equal("Open", titles[0]);
        Assert.Equal("Open in new tab", titles[1]);
        var cut = Array.IndexOf(titles, "Cut");
        Assert.True(cut > 1);
        Assert.Equal(["Cut", "Copy", "Paste", "Rename", "Delete"], titles[cut..(cut + 5)]);
        Assert.Equal("Properties", titles[^1]);
        Assert.DoesNotContain("Open with…", titles);
    }

    [Fact]
    public async Task AFile_OffersOpenWith_NotOpenInNewTab()
    {
        var (h, _) = await MakeAsync();
        Select(h, "1.txt");

        var titles = Titles(Build(h));

        Assert.Equal(["Open", "Open with…"], titles[..2]);
        Assert.DoesNotContain("Open in new tab", titles);
    }

    [Fact]
    public async Task SeveralItems_OfferNoOpenAndNoRename_ButCutCopyAndDelete()
    {
        var (h, _) = await MakeAsync();
        h.Tab.Selection.SelectAll();

        var titles = Titles(Build(h));

        Assert.DoesNotContain("Open", titles);
        Assert.DoesNotContain("Rename", titles);
        Assert.DoesNotContain("Properties", titles);
        Assert.Contains("Cut", titles);
        Assert.Contains("Copy", titles);
        Assert.Contains("Delete", titles);
    }

    [Fact]
    public async Task EmptySpace_OffersPasteAndNewFolder_AndSelectCommands()
    {
        var (h, _) = await MakeAsync();
        h.Tab.Selection.Clear();

        var titles = Titles(Build(h));

        Assert.Contains("Paste", titles);
        Assert.Contains("New folder", titles);
        Assert.Contains("Select all", titles);
        Assert.DoesNotContain("Cut", titles);
        Assert.DoesNotContain("Delete", titles);
    }

    [Fact]
    public async Task Paste_IsShownGreyed_UntilTheClipboardHoldsFiles()
    {
        var (h, clipboard) = await MakeAsync();
        h.Tab.Selection.Clear();

        var before = Build(h).Single(e => e.Title == "Paste");
        clipboard.Set([LocationPath.Parse(@"C:\v\2.txt")], ClipboardOperation.Copy);
        var after = Build(h).Single(e => e.Title == "Paste");

        Assert.False(before.Enabled);
        Assert.True(after.Enabled);
    }

    [Fact]
    public async Task EveryCommandEntry_RunsARegisteredCommand_WithItsContext()
    {
        var (h, _) = await MakeAsync();
        Select(h, "b");

        var open = Build(h).First(e => e.Title == "Open");

        Assert.Equal(CommandIds.ItemOpen, open.Command);
        Assert.Equal(@"C:\a\b", open.Context!.Target);
        Assert.True(h.Commands.TryGet(open.Command!.Value, out _));
    }

    [Fact]
    public async Task CommandsWhoseServiceIsMissing_AreShownGreyed_SoTheMenuKeepsItsShape()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        Select(h, "1.txt");

        var entries = Build(h);

        Assert.All(entries.Where(e => e.Title is "Cut" or "Paste" or "Delete"), e => Assert.False(e.Enabled));
        Assert.Contains(entries, e => e.Title == "Open" && e.Enabled);
    }

    [Theory]
    [InlineData("b")]
    [InlineData("1.txt")]
    [InlineData("")]
    public async Task NoMenu_StartsEndsOrDoublesUpOnADivider(string name)
    {
        var (h, _) = await MakeAsync();
        if (name.Length == 0)
        {
            h.Tab.Selection.Clear();
        }
        else
        {
            Select(h, name);
        }

        var entries = Build(h);

        Assert.NotEmpty(entries);
        Assert.NotEqual(MenuEntryKind.Separator, entries[0].Kind);
        Assert.NotEqual(MenuEntryKind.Separator, entries[^1].Kind);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.False(entries[i].Kind == MenuEntryKind.Separator && entries[i - 1].Kind == MenuEntryKind.Separator);
        }
    }
}

public sealed class ShellMenuFilterTests
{
    private static ShellMenuItem Item(int position, string label, string? verb, ShellMenuItemKind kind = ShellMenuItemKind.Item, IReadOnlyList<ShellMenuItem>? children = null) =>
        new(position, kind == ShellMenuItemKind.Separator ? -1 : position, kind, label, verb, true, false, false, null, children);

    private static ShellMenuItem Sep(int position) => Item(position, string.Empty, null, ShellMenuItemKind.Separator);

    private static string[] Labels(IEnumerable<ShellMenuItem> items) => [.. items.Select(i => i.Kind == ShellMenuItemKind.Separator ? "-" : i.Label)];

    [Fact]
    public void TheItemsLeviathanShowsItself_AreDropped_WhateverTheirCase()
    {
        var items = new[]
        {
            Item(0, "&Open", "open"), Item(1, "7-Zip", null, ShellMenuItemKind.SubMenu), Sep(2), Item(3, "Cu&t", "cut"), Item(4, "&Copy", "COPY"),
            Item(5, "&Paste", "paste"), Item(6, "Rena&me", "rename"), Item(7, "&Delete", "delete"), Item(8, "P&roperties", "properties"),
            Item(9, "Copy &as path", "copyaspath"), Item(10, "Create &shortcut", "link"),
        };

        var labels = Labels(ShellMenuFilter.Prepare(items));

        Assert.Equal(["7-Zip", "-", "Copy &as path", "Create &shortcut"], labels);
    }

    [Fact]
    public void Dividers_AreNotLeftLeadingTrailingOrDoubled()
    {
        var items = new[] { Sep(0), Item(1, "A", "a"), Sep(2), Item(3, "Cut", "cut"), Sep(4), Item(5, "B", "b"), Sep(6) };

        Assert.Equal(["A", "-", "B"], Labels(ShellMenuFilter.Prepare(items)));
    }

    [Fact]
    public void Submenus_AreFilteredToo_AndKeepTheirOtherItems()
    {
        var sub = Item(0, "Open with", null, ShellMenuItemKind.SubMenu, [Item(0, "Notepad", null), Item(1, "Open", "open")]);

        var result = ShellMenuFilter.Prepare([sub]);

        Assert.Equal(["Notepad"], Labels(Assert.Single(result).Children!));
    }

    [Fact]
    public void ASubmenuThatHasNotBeenLoaded_StaysAsItIs()
    {
        var result = ShellMenuFilter.Prepare([Item(0, "Send to", null, ShellMenuItemKind.SubMenu)]);

        Assert.Null(Assert.Single(result).Children);
    }

    [Fact]
    public void Positions_AreKept_SoSubmenusStayAddressable()
    {
        var result = ShellMenuFilter.Prepare([Item(0, "Open", "open"), Item(1, "7-Zip", null, ShellMenuItemKind.SubMenu)]);

        Assert.Equal(1, Assert.Single(result).Position);
    }
}

public sealed class ShellMenuTextTests
{
    [Theory]
    [InlineData("&Open", "Open", null)]
    [InlineData("Open wit&h", "Open with", null)]
    [InlineData("Cu&t\tCtrl+X", "Cut", "Ctrl+X")]
    [InlineData("Tom && Jerry", "Tom & Jerry", null)]
    [InlineData("Plain", "Plain", null)]
    [InlineData("Trailing tab\t", "Trailing tab", null)]
    [InlineData("", "", null)]
    public void Split_RemovesMnemonics_AndSeparatesTheShortcutText(string label, string title, string? shortcut)
    {
        Assert.Equal((title, shortcut), ShellMenuText.Split(label));
    }

    [Fact]
    public void Split_OfNull_IsEmpty()
    {
        Assert.Equal((string.Empty, (string?)null), ShellMenuText.Split(null));
    }
}
