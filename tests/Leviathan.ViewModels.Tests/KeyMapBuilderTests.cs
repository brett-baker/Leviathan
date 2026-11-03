using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

public sealed class KeyMapBuilderTests
{
    private static bool Known(CommandId id) => new[] { "edit.delete", "edit.copy", "edit.undo", "edit.redo", "nav.reload", "view.hidden", "edit.cut" }.Contains(id.Value);

    private static KeyGesture G(string text) => KeyGesture.Parse(text);

    [Fact]
    public void WithNoOverrides_ItIsTheDefaultMap()
    {
        var result = KeyMapBuilder.Build(null, Known);

        Assert.Equal(WindowCommands.DefaultKeyMap().Bindings.Count, result.Map.Bindings.Count);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Notes);
        Assert.Equal(CommandIds.EditCopy, result.Map.Resolve(G("Ctrl+C"), false));
    }

    [Fact]
    public void AnOverride_ReplacesEveryDefaultKeyOfTheCommand()
    {
        var result = KeyMapBuilder.Build(new Dictionary<string, string> { ["nav.reload"] = "Ctrl+F5" }, Known);

        Assert.Equal(CommandIds.NavReload, result.Map.Resolve(G("Ctrl+F5"), false));
        Assert.Null(result.Map.Resolve(G("F5"), false));
        Assert.Null(result.Map.Resolve(G("Ctrl+R"), false));
        Assert.Equal([G("Ctrl+F5")], result.Map.GesturesFor(CommandIds.NavReload));
    }

    [Fact]
    public void AnEmptyText_RemovesTheKeysOfTheCommand()
    {
        var result = KeyMapBuilder.Build(new Dictionary<string, string> { ["edit.delete"] = "" }, Known);

        Assert.Null(result.Map.Resolve(G("Del"), false));
        Assert.Empty(result.Map.GesturesFor(CommandIds.EditDelete));
        Assert.Contains(result.Notes, n => n.Contains("edit.delete", StringComparison.Ordinal));
    }

    [Fact]
    public void ATakenKey_GoesToTheCommandTheFileNames_AndTheNoteSaysWho_LostIt()
    {
        var result = KeyMapBuilder.Build(new Dictionary<string, string> { ["edit.cut"] = "Ctrl+C" }, Known);

        Assert.Equal(CommandIds.EditCut, result.Map.Resolve(G("Ctrl+C"), false));
        Assert.Empty(result.Map.GesturesFor(CommandIds.EditCopy));
        Assert.Contains(result.Notes, n => n.Contains("Ctrl+C", StringComparison.Ordinal) && n.Contains("edit.copy", StringComparison.Ordinal));
    }

    [Fact]
    public void ABadEntry_IsReportedAndSkipped_WithoutStoppingTheOthers()
    {
        var result = KeyMapBuilder.Build(new Dictionary<string, string>
        {
            ["no.such.command"] = "Ctrl+Q",
            ["edit.undo"] = "Ctrl+Bogus+Z+",
            ["edit.delete"] = "Ctrl+Backspace",
        }, Known);

        Assert.Equal(2, result.Problems.Count);
        Assert.Contains(result.Problems, p => p.Contains("no.such.command", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("edit.undo", StringComparison.Ordinal));
        Assert.Equal(CommandIds.EditUndo, result.Map.Resolve(G("Ctrl+Z"), false));
        Assert.Equal(CommandIds.EditDelete, result.Map.Resolve(G("Ctrl+Backspace"), false));
    }

    [Fact]
    public void TheResult_DoesNotDependOnTheOrderOfTheFile()
    {
        var one = KeyMapBuilder.Build(new Dictionary<string, string> { ["edit.cut"] = "Ctrl+K", ["edit.copy"] = "Ctrl+K" }, Known);
        var other = KeyMapBuilder.Build(new Dictionary<string, string> { ["edit.copy"] = "Ctrl+K", ["edit.cut"] = "Ctrl+K" }, Known);

        Assert.Equal(one.Map.Resolve(G("Ctrl+K"), false), other.Map.Resolve(G("Ctrl+K"), false));
    }

    [Fact]
    public void TheGestureTextOfAFile_IsReadLeniently()
    {
        var result = KeyMapBuilder.Build(new Dictionary<string, string> { ["view.hidden"] = " ctrl + shift + h " }, Known);

        Assert.Equal(CommandIds.ViewHidden, result.Map.Resolve(G("Ctrl+Shift+H"), false));
    }

    [Fact]
    public void TheCommaKey_CanBeWritten()
    {
        var result = KeyMapBuilder.Build(new Dictionary<string, string> { ["edit.redo"] = "Ctrl+," }, Known);

        Assert.Equal(CommandIds.EditRedo, result.Map.Resolve(G("Ctrl+,"), false));
        Assert.Contains(result.Notes, n => n.Contains("settings.open", StringComparison.Ordinal));
    }
}

public sealed class DefaultKeysTests
{
    [Fact]
    public async Task EveryCommandWithADefaultShortcut_IsBoundToIt_AndEveryBindingRunsARealCommand()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var map = WindowCommands.DefaultKeyMap();

        // Enter opens the item under the keyboard cursor: the file list itself raises that (it knows which row), so it is not a window key.
        foreach (var command in h.Commands.Commands.Where(c => c.Definition.DefaultShortcut is not null && c.Definition.Id != CommandIds.ItemOpen))
        {
            var gesture = KeyGesture.Parse(command.Definition.DefaultShortcut!);
            Assert.True(map.Resolve(gesture, false) == command.Definition.Id, $"{command.Definition.Id} should be bound to {gesture}");
        }

        foreach (var binding in map.Bindings)
        {
            Assert.True(h.Commands.TryGet(binding.Command, out _), $"{binding.Gesture} runs {binding.Command}, which does not exist");
        }
    }

    [Fact]
    public async Task CommandIdsAll_HasEveryRegisteredCommand_SoAFileCanBeCheckedWithoutAWindow()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;

        var missing = h.Commands.Commands.Select(c => c.Definition.Id).Where(id => !CommandIds.All.Contains(id)).ToList();

        Assert.Empty(missing);
        Assert.True(CommandIds.All.Count >= h.Commands.Commands.Count);
    }

    [Fact]
    public void NoKeyIsBoundTwice()
    {
        var map = WindowCommands.DefaultKeyMap();

        var duplicates = map.Bindings.GroupBy(b => (b.Gesture, b.Scope)).Where(g => g.Count() > 1).Select(g => g.Key.Gesture.ToString()).ToList();

        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("Ctrl+H", "view.hidden")]
    [InlineData("Ctrl+I", "select.invert")]
    [InlineData("Ctrl+Shift+A", "select.none")]
    [InlineData("Alt+Enter", "item.properties")]
    [InlineData("Ctrl+Shift+N", "new.folder")]
    [InlineData("F2", "edit.rename")]
    [InlineData("Shift+Del", "edit.delete.permanent")]
    public void TheKeysOfThePhaseOneCommands_Spec17_3_AreBound(string keys, string command)
    {
        var bound = WindowCommands.DefaultKeyMap().Resolve(KeyGesture.Parse(keys), false);

        Assert.NotNull(bound);
        Assert.StartsWith(command.Split('.')[0], bound.Value.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Properties_FromTheKeyboard_ActsOnTheOneSelectedItem_AndIsOffWithNoSelection()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        Assert.False(h.Commands.CanExecute(CommandIds.Properties));

        h.Tab.Selection.Click(h.Tab.Folder.Entries[0]);

        Assert.True(h.Commands.CanExecute(CommandIds.Properties));
    }

    [Fact]
    public async Task TheKeyboardRows_ListEveryCommand_WithItsKeys_AndMarkWhatTheFileChanged()
    {
        var h = new WindowCommandsTests.Harness();
        await h.Ready;
        var overrides = new Dictionary<string, string> { ["edit.undo"] = "Ctrl+Alt+Z" };
        var result = KeyMapBuilder.Build(overrides, id => h.Commands.TryGet(id, out _));

        var rows = KeyMapBuilder.Rows(h.Commands.Commands, result.Map);

        Assert.Equal(h.Commands.Commands.Count, rows.Count);
        var undo = rows.Single(r => r.Command == CommandIds.EditUndo);
        Assert.Equal("Ctrl+Alt+Z", undo.Keys);
        Assert.True(undo.IsCustom);
        var copy = rows.Single(r => r.Command == CommandIds.EditCopy);
        Assert.Equal("Ctrl+C", copy.Keys);
        Assert.False(copy.IsCustom);

        var bad = KeyMapBuilder.Build(new Dictionary<string, string> { ["edit.undo"] = "garbage+" }, id => h.Commands.TryGet(id, out _));
        var badRows = KeyMapBuilder.Rows(h.Commands.Commands, bad.Map);
        Assert.False(badRows.Single(r => r.Command == CommandIds.EditUndo).IsCustom);
        Assert.Equal(rows.OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase).Select(r => r.Category), rows.Select(r => r.Category));
    }
}

public sealed class SettingsReloadTests
{
    [Fact]
    public async Task Reload_ReadsTheFileAgain_AndTellsSubscribers()
    {
        var disk = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        using var store = new SettingsStore(disk, @"C:\data\settings.json");
        await store.LoadAsync(CancellationToken.None);
        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;

        disk.AddFile(@"C:\data\settings.json", new System.IO.Abstractions.TestingHelpers.MockFileData("{ \"KeyBindings\": { \"edit.delete\": \"Ctrl+Backspace\" } }"));
        var reloaded = await store.ReloadAsync(CancellationToken.None);

        Assert.Equal("Ctrl+Backspace", reloaded.KeyBindings["edit.delete"]);
        Assert.Equal("Ctrl+Backspace", seen?.KeyBindings["edit.delete"]);
        Assert.Equal("Ctrl+Backspace", store.Current.KeyBindings["edit.delete"]);
    }
}
