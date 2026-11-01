using Leviathan.Diagnostics;
using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Tests;

public sealed class CommandTests
{
    private static AppCommand Make(string id, string title, string category, Func<CommandContext, CancellationToken, Task>? run = null, Func<CommandContext, bool>? can = null) =>
        new(new CommandDefinition(new CommandId(id), title, category), run ?? ((_, _) => Task.CompletedTask), can);

    [Fact]
    public async Task ExecuteAsync_EnabledCommand_RunsAndRecordsInTrail()
    {
        var trail = new ActionTrail();
        var registry = new CommandRegistry(trail);
        var ran = new List<string?>();
        registry.Register(Make("tab.new", "New tab", "Tabs", (c, _) => { ran.Add(c.Target); return Task.CompletedTask; }));

        var result = await registry.ExecuteAsync(new CommandId("tab.new"), new CommandContext(@"C:\Users"), CancellationToken.None);

        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal([@"C:\Users"], ran);
        var action = Assert.Single(trail.Snapshot());
        Assert.Equal("tab.new", action.Command);
        Assert.Equal(@"C:\Users", action.Target);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledOrUnknown_DoesNothingAndIsNotRecorded()
    {
        var trail = new ActionTrail();
        var registry = new CommandRegistry(trail);
        var ran = false;
        registry.Register(Make("file.paste", "Paste", "Files", (_, _) => { ran = true; return Task.CompletedTask; }, _ => false));

        Assert.Equal(CommandResult.Disabled, await registry.ExecuteAsync(new CommandId("file.paste"), null, CancellationToken.None));
        Assert.Equal(CommandResult.Unknown, await registry.ExecuteAsync(new CommandId("nope"), null, CancellationToken.None));

        Assert.False(ran);
        Assert.Empty(trail.Snapshot());
    }

    [Fact]
    public async Task ExecuteAsync_CommandThatThrows_IsAlreadyInTheTrail()
    {
        var trail = new ActionTrail();
        var registry = new CommandRegistry(trail);
        registry.Register(Make("boom", "Boom", "Test", (_, _) => throw new InvalidOperationException("x")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync(new CommandId("boom"), null, CancellationToken.None));

        Assert.Equal("boom", Assert.Single(trail.Snapshot()).Command);
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var registry = new CommandRegistry(new ActionTrail());
        registry.Register(Make("a", "A", "X"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Make("a", "A again", "X")));
    }

    [Fact]
    public void Search_RanksPrefixThenWordStartThenInnerMatch()
    {
        var registry = new CommandRegistry(new ActionTrail());
        registry.Register(Make("1", "Reopen closed tab", "Tabs"));
        registry.Register(Make("2", "New tab", "Tabs"));
        registry.Register(Make("3", "Tab context", "Tabs"));
        registry.Register(Make("4", "Rename", "Files"));

        var titles = registry.Search("tab").Select(c => c.Definition.Title).ToList();

        Assert.Equal(["Tab context", "New tab", "Reopen closed tab"], titles);
    }

    [Fact]
    public void Search_AllWordsMustMatch_AndCategoryCounts()
    {
        var registry = new CommandRegistry(new ActionTrail());
        registry.Register(Make("1", "New tab", "Tabs"));
        registry.Register(Make("2", "New window", "Windows"));

        Assert.Equal(["New tab"], registry.Search("new tabs").Select(c => c.Definition.Title));
        Assert.Empty(registry.Search("zzz"));
        Assert.Equal(2, registry.Search("").Count);
    }

    [Fact]
    public async Task ReplayAsync_RunsTrailInOrder_AndReportsUnknownCommands()
    {
        var registry = new CommandRegistry(new ActionTrail());
        var order = new List<string>();
        registry.Register(Make("nav.go", "Go", "Nav", (c, _) => { order.Add(c.Target!); return Task.CompletedTask; }));
        var actions = new[]
        {
            new UserAction(DateTimeOffset.UnixEpoch, "nav.go", @"C:\a"),
            new UserAction(DateTimeOffset.UnixEpoch, "gone", null),
            new UserAction(DateTimeOffset.UnixEpoch, "nav.go", @"C:\b"),
        };

        var results = await new ActionReplayer(registry).ReplayAsync(actions, CancellationToken.None);

        Assert.Equal([@"C:\a", @"C:\b"], order);
        Assert.Equal([CommandResult.Executed, CommandResult.Unknown, CommandResult.Executed], results.Select(r => r.Result));
    }

    [Theory]
    [InlineData("ctrl+shift+p", "Ctrl+Shift+P")]
    [InlineData("Shift+Ctrl+P", "Ctrl+Shift+P")]
    [InlineData("alt+left", "Alt+Left")]
    [InlineData("Del", "Delete")]
    [InlineData("shift+del", "Shift+Delete")]
    [InlineData("f5", "F5")]
    [InlineData("Ctrl+Alt+Shift+PgDn", "Ctrl+Alt+Shift+PageDown")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Ctrl+\\", "Ctrl+\\")]
    [InlineData("T", "T")]
    public void KeyGesture_Parse_NormalizesToCanonicalText(string input, string expected)
    {
        Assert.Equal(expected, KeyGesture.Parse(input).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hyper+K")]
    [InlineData("Ctrl+")]
    public void KeyGesture_TryParse_RejectsBadText(string input)
    {
        Assert.False(KeyGesture.TryParse(input, out _));
    }

    [Fact]
    public void KeyGesture_DifferentSpellings_AreEqual()
    {
        Assert.Equal(KeyGesture.Parse("Ctrl+Shift+K"), KeyGesture.Parse("shift+control+k"));
    }

    private static KeyMap SplitMap(out CommandId reload, out CommandId copyOther)
    {
        reload = new CommandId("nav.reload");
        copyOther = new CommandId("pane.copyToOther");
        var map = new KeyMap();
        Assert.Null(map.TryBind(new KeyBinding(KeyGesture.Parse("F5"), reload)));
        Assert.Null(map.TryBind(new KeyBinding(KeyGesture.Parse("F5"), copyOther, KeyScope.SplitOnly)));
        return map;
    }

    [Fact]
    public void Resolve_SinglePane_UsesPlainBinding()
    {
        var map = SplitMap(out var reload, out _);

        Assert.Equal(reload, map.Resolve(KeyGesture.Parse("F5"), isSplit: false));
    }

    [Fact]
    public void Resolve_Split_SplitBindingWinsByDefault()
    {
        var map = SplitMap(out _, out var copyOther);

        Assert.Equal(copyOther, map.Resolve(KeyGesture.Parse("F5"), isSplit: true));
    }

    [Fact]
    public void Resolve_SplitBindingsWinOff_PlainBindingWinsWhileSplit()
    {
        var map = SplitMap(out var reload, out _);
        map.SplitBindingsWin = false;

        Assert.Equal(reload, map.Resolve(KeyGesture.Parse("F5"), isSplit: true));
    }

    [Fact]
    public void Resolve_SplitOnlyBindingWithoutPlain_StillWorksWhileSplitButNotOtherwise()
    {
        var map = new KeyMap();
        var swap = new CommandId("pane.swap");
        map.TryBind(new KeyBinding(KeyGesture.Parse("Ctrl+Shift+S"), swap, KeyScope.SplitOnly));
        map.SplitBindingsWin = false;

        Assert.Equal(swap, map.Resolve(KeyGesture.Parse("Ctrl+Shift+S"), isSplit: true));
        Assert.Null(map.Resolve(KeyGesture.Parse("Ctrl+Shift+S"), isSplit: false));
    }

    [Fact]
    public void TryBind_SameGestureAndScope_ReturnsTheConflictingCommand()
    {
        var map = new KeyMap();
        var copy = new CommandId("file.copy");
        map.TryBind(new KeyBinding(KeyGesture.Parse("Ctrl+C"), copy));

        var conflict = map.TryBind(new KeyBinding(KeyGesture.Parse("ctrl+c"), new CommandId("other")));

        Assert.Equal(copy, conflict);
        Assert.Single(map.Bindings);
    }

    [Fact]
    public void Unbind_ThenBind_RebindsACommand_AndGesturesForListsTooltipKeys()
    {
        var map = new KeyMap();
        var copy = new CommandId("file.copy");
        map.TryBind(new KeyBinding(KeyGesture.Parse("Ctrl+C"), copy));

        Assert.Equal(1, map.Unbind(copy));
        map.TryBind(new KeyBinding(KeyGesture.Parse("Ctrl+Insert"), copy));

        Assert.Equal(["Ctrl+Insert"], map.GesturesFor(copy).Select(g => g.ToString()));
        Assert.Null(map.Resolve(KeyGesture.Parse("Ctrl+C"), false));
    }
}
