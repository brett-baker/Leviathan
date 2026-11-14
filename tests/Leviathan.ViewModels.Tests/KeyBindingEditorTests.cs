using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

/// <summary>The edits of the Keyboard page (spec §17.2): set, remove, reset, clash, export and import.</summary>
public sealed class KeyBindingEditorTests
{
    private static readonly CommandId Copy = new("edit.copy");
    private static readonly CommandId Cut = new("edit.cut");

    private static KeyMap Defaults()
    {
        var map = new KeyMap();
        map.TryBind(new KeyBinding(KeyGesture.Parse("Ctrl+C"), Copy));
        map.TryBind(new KeyBinding(KeyGesture.Parse("Ctrl+X"), Cut));
        return map;
    }

    private static readonly Dictionary<string, string> None = [];

    [Fact]
    public void ANewKey_IsStoredAsCanonicalText()
    {
        var result = KeyBindingEditor.Set(None, Copy, KeyGesture.Parse("shift+ctrl+k"), Defaults());

        Assert.Equal("Ctrl+Shift+K", result[Copy.Value]);
    }

    [Fact]
    public void TheDefaultKey_IsNotStored_AndRemovesAnEarlierChange()
    {
        var changed = new Dictionary<string, string> { [Copy.Value] = "Ctrl+K", ["other"] = "F5" };

        var result = KeyBindingEditor.Set(changed, Copy, KeyGesture.Parse("Ctrl+C"), Defaults());

        Assert.False(result.ContainsKey(Copy.Value));
        Assert.Equal("F5", result["other"]);
    }

    [Fact]
    public void RemovingKeys_WritesAnEmptyText_ButOnlyWhenThereWasADefault()
    {
        Assert.Equal(string.Empty, KeyBindingEditor.Unbind(None, Copy, Defaults())[Copy.Value]);
        Assert.Empty(KeyBindingEditor.Unbind(new Dictionary<string, string> { ["new.thing"] = "F9" }, new CommandId("new.thing"), Defaults()));
    }

    [Fact]
    public void Reset_DeletesTheEntry_AndLeavesTheOthers()
    {
        var changed = new Dictionary<string, string> { [Copy.Value] = "Ctrl+K", [Cut.Value] = "Ctrl+J" };

        var result = KeyBindingEditor.Reset(changed, Copy);

        Assert.Equal(["edit.cut"], result.Keys);
        Assert.Equal(2, changed.Count); // the input is never changed
    }

    [Fact]
    public void AClash_NamesTheOtherCommand_ButNotTheCommandItself()
    {
        var map = Defaults();

        Assert.Equal(Cut, KeyBindingEditor.HeldBy(map, Copy, KeyGesture.Parse("Ctrl+X")));
        Assert.Null(KeyBindingEditor.HeldBy(map, Copy, KeyGesture.Parse("Ctrl+C")));
        Assert.Null(KeyBindingEditor.HeldBy(map, Copy, KeyGesture.Parse("Ctrl+Q")));
    }

    [Fact]
    public void ExportThenImport_GivesTheSameBindings()
    {
        var bindings = new Dictionary<string, string> { [Cut.Value] = "Ctrl+J", [Copy.Value] = string.Empty };

        var imported = KeyBindingEditor.Import(KeyBindingEditor.Export(bindings), _ => true);

        Assert.True(imported.IsKeyFile);
        Assert.Empty(imported.Problems);
        Assert.Equal(bindings.OrderBy(b => b.Key), imported.Bindings.OrderBy(b => b.Key));
    }

    [Fact]
    public void Import_SkipsWhatItCannotUse_AndSaysSo()
    {
        var json = """{ "edit.copy": "Ctrl+K", "gone.command": "F5", "edit.cut": "not a key +" }""";

        var imported = KeyBindingEditor.Import(json, id => id == Copy || id == Cut);

        Assert.Equal(["edit.copy"], imported.Bindings.Keys);
        Assert.Equal(2, imported.Problems.Count);
        Assert.Contains(imported.Problems, p => p.Contains("gone.command"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"a\": 5}")]
    public void ATextThatIsNotAKeyFile_ImportsNothing(string json)
    {
        var imported = KeyBindingEditor.Import(json, _ => true);

        Assert.False(imported.IsKeyFile);
        Assert.Empty(imported.Bindings);
        Assert.Single(imported.Problems);
    }
}
