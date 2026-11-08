using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Testing;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

public sealed class TriageCommandsTests
{
    private static KeyGesture G(string text) => KeyGesture.Parse(text);

    [Theory]
    [InlineData("Space", "triageMode.playPause")]
    [InlineData("Right", "triageMode.forward30")]
    [InlineData("Left", "triageMode.back30")]
    [InlineData("Shift+Right", "triageMode.forward1m")]
    [InlineData("Shift+Left", "triageMode.back1m")]
    [InlineData("Ctrl+Right", "triageMode.forward5m")]
    [InlineData("Ctrl+Left", "triageMode.back5m")]
    [InlineData("Alt+Right", "triageMode.forward5s")]
    [InlineData("Alt+Left", "triageMode.back5s")]
    [InlineData(".", "triageMode.frameNext")]
    [InlineData(",", "triageMode.framePrevious")]
    [InlineData("Ctrl+1", "triageMode.jump10")]
    [InlineData("Ctrl+9", "triageMode.jump90")]
    [InlineData("Home", "triageMode.start")]
    [InlineData("End", "triageMode.end")]
    [InlineData("J", "triageMode.rewind")]
    [InlineData("K", "triageMode.pause")]
    [InlineData("L", "triageMode.fastForward")]
    [InlineData("[", "triageMode.speedDown")]
    [InlineData("]", "triageMode.speedUp")]
    [InlineData("S", "triageMode.skim")]
    [InlineData("Enter", "triageMode.keep")]
    [InlineData("G", "triageMode.keep")]
    [InlineData("Delete", "triageMode.discard")]
    [InlineData("X", "triageMode.discard")]
    [InlineData("1", "triageMode.tag1")]
    [InlineData("9", "triageMode.tag9")]
    [InlineData("Ctrl+0", "triageMode.clearTags")]
    [InlineData("Down", "triageMode.skip")]
    [InlineData("Tab", "triageMode.skip")]
    [InlineData("Up", "triageMode.previous")]
    [InlineData("Shift+Tab", "triageMode.previous")]
    [InlineData("C", "triageMode.contactSheet")]
    [InlineData("R", "triageMode.rename")]
    [InlineData("M", "triageMode.move")]
    [InlineData("Ctrl+Z", "triageMode.undo")]
    [InlineData("I", "triageMode.overlay")]
    [InlineData("Escape", "triageMode.exit")]
    public void DefaultKeys_AreThoseOfSpecSection10_2(string key, string command) =>
        Assert.Equal(command, TriageCommands.DefaultKeyMap().Resolve(G(key), isSplit: false)?.Value);

    [Fact]
    public void EveryCommand_HasAKey_AndEveryDefaultKeyBelongsToACommand()
    {
        var map = TriageCommands.DefaultKeyMap();

        Assert.All(TriageCommands.All, c => Assert.NotEmpty(map.GesturesFor(c.Id)));
        Assert.All(map.Bindings, b => Assert.True(TriageCommands.IsKnown(b.Command)));
    }

    [Fact]
    public void TheCommandIds_AreUnique_AndAllStartWithThePrefix()
    {
        Assert.Equal(TriageCommands.All.Count, TriageCommands.All.Select(c => c.Id).Distinct().Count());
        Assert.All(TriageCommands.All, c => Assert.True(TriageCommands.IsTriage(c.Id.Value)));
        Assert.All(TriageCommands.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
    }

    [Fact]
    public void NoTriageId_IsAlsoAFileViewCommand() =>
        Assert.DoesNotContain(CommandIds.All, c => TriageCommands.IsTriage(c.Value));

    [Fact]
    public void BuildKeyMap_WithNothing_IsTheDefaults()
    {
        var result = TriageCommands.BuildKeyMap(null);

        Assert.Empty(result.Problems);
        Assert.Empty(result.Notes);
        Assert.Equal(TriageCommands.DefaultKeyMap().Bindings.Count, result.Map.Bindings.Count);
    }

    [Fact]
    public void BuildKeyMap_AnEntryReplacesEveryDefaultKeyOfTheCommand()
    {
        var result = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["triageMode.keep"] = "F2" });

        Assert.Equal("triageMode.keep", result.Map.Resolve(G("F2"), false)?.Value);
        Assert.Null(result.Map.Resolve(G("Enter"), false));
        Assert.Null(result.Map.Resolve(G("G"), false));
    }

    [Fact]
    public void BuildKeyMap_AKeyAnotherCommandHeld_GoesToTheNewOne_AndIsReported()
    {
        var result = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["triageMode.keep"] = "X" });

        Assert.Equal("triageMode.keep", result.Map.Resolve(G("X"), false)?.Value);
        Assert.Contains(result.Notes, n => n.Contains("triageMode.discard", StringComparison.Ordinal));
        Assert.Equal("triageMode.discard", result.Map.Resolve(G("Delete"), false)?.Value);
    }

    [Fact]
    public void BuildKeyMap_AnEmptyEntry_RemovesTheKeys()
    {
        var result = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["triageMode.rename"] = string.Empty });

        Assert.Null(result.Map.Resolve(G("R"), false));
        Assert.Empty(result.Map.GesturesFor(TriageCommands.Rename));
    }

    [Fact]
    public void BuildKeyMap_BadEntries_AreReported_AndTheDefaultsStay()
    {
        var result = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["triageMode.nope"] = "F1", ["triageMode.keep"] = "Hyper+K" });

        Assert.Equal(2, result.Problems.Count);
        Assert.Equal("triageMode.keep", result.Map.Resolve(G("Enter"), false)?.Value);
    }

    [Fact]
    public void BuildKeyMap_LeavesTheFileViewEntriesToTheFileView()
    {
        var result = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["tab.new"] = "Ctrl+Alt+N", ["triageMode.keep"] = "F2" });

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void TheFileViewsBuilder_StillStartsFromItsOwnKeys()
    {
        var result = KeyMapBuilder.Build(null, CommandIds.All.Contains);

        Assert.Equal(WindowCommands.DefaultKeyMap().Bindings.Count, result.Map.Bindings.Count);
        Assert.Null(result.Map.Resolve(G("Space"), false) is { } space && TriageCommands.IsTriage(space.Value) ? space : (CommandId?)null);
    }

    [Fact]
    public void Rows_ListEveryCommandWithItsKeys_AndMarkChangedOnes()
    {
        var map = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["triageMode.keep"] = "F2" }).Map;

        var rows = TriageCommands.Rows(map);

        Assert.Equal(TriageCommands.All.Count, rows.Count);
        var keep = rows.Single(r => r.Command == TriageCommands.Keep);
        Assert.Equal("F2", keep.Keys);
        Assert.True(keep.IsCustom);
        Assert.False(rows.Single(r => r.Command == TriageCommands.Discard).IsCustom);
        Assert.StartsWith("Triage · ", keep.Category);
    }
}

public sealed class KeyNamesTests
{
    [Theory]
    [InlineData(65, false, false, false, "A")]
    [InlineData(90, false, false, false, "Z")]
    [InlineData(49, false, false, false, "1")]
    [InlineData(97, false, false, false, "1")]
    [InlineData(32, false, false, false, "Space")]
    [InlineData(13, false, false, false, "Enter")]
    [InlineData(27, false, false, false, "Escape")]
    [InlineData(46, false, false, false, "Delete")]
    [InlineData(37, false, false, false, "Left")]
    [InlineData(40, false, false, false, "Down")]
    [InlineData(188, false, false, false, ",")]
    [InlineData(190, false, false, false, ".")]
    [InlineData(219, false, false, false, "[")]
    [InlineData(221, false, false, false, "]")]
    [InlineData(112, false, false, false, "F1")]
    [InlineData(9, false, true, false, "Shift+Tab")]
    [InlineData(39, true, false, false, "Ctrl+Right")]
    [InlineData(39, false, false, true, "Alt+Right")]
    [InlineData(90, true, false, false, "Ctrl+Z")]
    [InlineData(39, true, true, true, "Ctrl+Alt+Shift+Right")]
    public void GestureFor_NamesTheKeyAndModifiers(int vk, bool ctrl, bool shift, bool alt, string expected) =>
        Assert.Equal(expected, KeyNames.GestureFor(vk, ctrl, shift, alt)?.ToString());

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(91)]
    [InlineData(175)]
    [InlineData(0)]
    public void GestureFor_OfAKeyWithNoName_IsNull(int vk) => Assert.Null(KeyNames.GestureFor(vk, false, false, false));

    [Theory]
    [InlineData(16, true)]
    [InlineData(162, true)]
    [InlineData(65, false)]
    public void IsModifier_KnowsTheModifierKeys(int vk, bool expected) => Assert.Equal(expected, KeyNames.IsModifier(vk));

    [Fact]
    public void TheDefaultMap_IsReachableFromVirtualKeys()
    {
        var map = TriageCommands.DefaultKeyMap();

        Assert.Equal("triageMode.keep", map.Resolve(KeyNames.GestureFor(13, false, false, false)!.Value, false)?.Value);
        Assert.Equal("triageMode.undo", map.Resolve(KeyNames.GestureFor(90, true, false, false)!.Value, false)?.Value);
        Assert.Equal("triageMode.jump30", map.Resolve(KeyNames.GestureFor(51, true, false, false)!.Value, false)?.Value);
        Assert.Equal("triageMode.frameNext", map.Resolve(KeyNames.GestureFor(190, false, false, false)!.Value, false)?.Value);
    }
}

public sealed class TriageDispatcherTests
{
    private static FileEntry Video(string name, long size = 1000) =>
        new(name, LocationPath.Parse($@"C:\inbox\{name}"), FileEntryKind.File, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FileAttributes.Normal);

    private sealed class Actions : ITriageActions
    {
        public List<string> Log { get; } = [];

        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken)
        {
            Log.Add($"keep {file.Name}");
            return Task.FromResult(new TriageActionResult(null));
        }

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken)
        {
            Log.Add($"discard {file.Name}");
            return Task.FromResult(new TriageActionResult(null));
        }

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken)
        {
            Log.Add($"tag {tagNumber}");
            return Task.FromResult(true);
        }

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken)
        {
            Log.Add("cleartags");
            return Task.CompletedTask;
        }
    }

    private static async Task<(TriageDispatcher Dispatcher, FakeVideoEngine Engine, Actions Actions)> StartAsync(int files = 3)
    {
        var engine = new FakeVideoEngine { FileDuration = TimeSpan.FromMinutes(20) };
        var actions = new Actions();
        var session = new TriageSession([.. Enumerable.Range(1, files).Select(i => Video($"v{i}.mkv"))], engine, actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync();
        return (new TriageDispatcher(session, TriageCommands.DefaultKeyMap()), engine, actions);
    }

    private static Task<bool> Press(TriageDispatcher d, string key) => d.HandleAsync(KeyGesture.Parse(key));

    [Theory]
    [InlineData("Right", 30)]
    [InlineData("Left", 0)]
    [InlineData("Shift+Right", 60)]
    [InlineData("Ctrl+Right", 300)]
    [InlineData("Alt+Right", 5)]
    [InlineData("Ctrl+2", 240)]
    [InlineData("End", 1190)]
    public async Task PlaybackKeys_MoveThePlayer(string key, double expectedSeconds)
    {
        var (dispatcher, engine, _) = await StartAsync();

        Assert.True(await Press(dispatcher, key));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), engine.Snapshot.Position);
    }

    [Fact]
    public async Task Space_TogglesPlayAndPause()
    {
        var (dispatcher, engine, _) = await StartAsync();

        await Press(dispatcher, "Space");
        Assert.Equal(PlaybackState.Paused, engine.Snapshot.State);
        await Press(dispatcher, "Space");

        Assert.Equal(PlaybackState.Playing, engine.Snapshot.State);
    }

    [Fact]
    public async Task FrameKeys_StepThePausedPlayer()
    {
        var (dispatcher, engine, _) = await StartAsync();
        await Press(dispatcher, "Space");
        var before = engine.Snapshot.Position;

        await Press(dispatcher, ".");

        Assert.True(engine.Snapshot.Position > before);
    }

    [Fact]
    public async Task SpeedKeys_StepTheSpeedLadder()
    {
        var (dispatcher, engine, _) = await StartAsync();

        await Press(dispatcher, "]");
        Assert.Equal(1.25, engine.Snapshot.Speed);
        await Press(dispatcher, "[");
        await Press(dispatcher, "[");

        Assert.Equal(0.75, engine.Snapshot.Speed);
    }

    [Fact]
    public async Task EnterAndG_Keep_DeleteAndX_Discard()
    {
        var (dispatcher, _, actions) = await StartAsync(4);

        await Press(dispatcher, "Enter");
        await Press(dispatcher, "x");
        await Press(dispatcher, "G");
        await Press(dispatcher, "Delete");

        Assert.Equal(["keep v1.mkv", "discard v2.mkv", "keep v3.mkv", "discard v4.mkv"], actions.Log);
        Assert.True(dispatcher.Session.IsFinished);
    }

    [Fact]
    public async Task TabAndUp_MoveAboutWithoutDeciding()
    {
        var (dispatcher, _, actions) = await StartAsync();

        await Press(dispatcher, "Tab");
        Assert.Equal(1, dispatcher.Session.Index);
        await Press(dispatcher, "Up");

        Assert.Equal(0, dispatcher.Session.Index);
        Assert.Empty(actions.Log);
    }

    [Fact]
    public async Task CtrlZ_UndoesTheLastDecision()
    {
        var (dispatcher, _, _) = await StartAsync();
        await Press(dispatcher, "Tab");

        await Press(dispatcher, "Ctrl+Z");

        Assert.Equal(0, dispatcher.Session.Index);
        Assert.Equal(TriageDecision.None, dispatcher.Session.Items[0].Decision);
    }

    [Fact]
    public async Task NumberKeys_ToggleTags_WithoutAdvancing()
    {
        var (dispatcher, _, actions) = await StartAsync();

        await Press(dispatcher, "3");
        await Press(dispatcher, "7");
        await Press(dispatcher, "Ctrl+0");

        Assert.Equal(["tag 3", "tag 7", "cleartags"], actions.Log);
        Assert.Equal(0, dispatcher.Session.Index);
        Assert.Empty(dispatcher.Session.Current!.Tags);
    }

    [Theory]
    [InlineData("C", "triageMode.contactSheet")]
    [InlineData("R", "triageMode.rename")]
    [InlineData("M", "triageMode.move")]
    [InlineData("I", "triageMode.overlay")]
    [InlineData("S", "triageMode.skim")]
    [InlineData("Escape", "triageMode.exit")]
    public async Task WindowKeys_AreHandedToTheWindow(string key, string command)
    {
        var (dispatcher, _, actions) = await StartAsync();
        var raised = new List<string>();
        dispatcher.WindowCommand += (_, c) => raised.Add(c.Value);

        Assert.True(await Press(dispatcher, key));

        Assert.Equal([command], raised);
        Assert.Empty(actions.Log);
    }

    [Fact]
    public async Task AKeyThatIsNotBound_IsNotHandled_SoTheWindowCanLetItThrough()
    {
        var (dispatcher, _, _) = await StartAsync();

        Assert.False(await Press(dispatcher, "F9"));
        Assert.False(await Press(dispatcher, "Q"));
    }

    [Fact]
    public async Task Run_OfACommandThatIsNotTriage_IsNotHandled()
    {
        var (dispatcher, _, _) = await StartAsync();

        Assert.False(await dispatcher.RunAsync(CommandIds.TabNew));
    }

    [Fact]
    public async Task ARemappedKey_RunsTheCommand_AndTheOldKeyDoesNothing()
    {
        var engine = new FakeVideoEngine();
        var actions = new Actions();
        var session = new TriageSession([Video("a.mkv"), Video("b.mkv")], engine, actions, new TriageOptions { StartOffset = 0 });
        await session.StartAsync();
        var map = TriageCommands.BuildKeyMap(new Dictionary<string, string> { ["triageMode.keep"] = "F2" }).Map;
        var dispatcher = new TriageDispatcher(session, map);

        Assert.False(await dispatcher.HandleAsync(KeyGesture.Parse("Enter")));
        Assert.True(await dispatcher.HandleAsync(KeyGesture.Parse("F2")));

        Assert.Equal(["keep a.mkv"], actions.Log);
    }

    [Fact]
    public async Task Keys_AfterTheQueueEnded_DoNothingHarmful()
    {
        var (dispatcher, _, actions) = await StartAsync(1);
        await Press(dispatcher, "Enter");
        Assert.True(dispatcher.Session.IsFinished);

        await Press(dispatcher, "Enter");
        await Press(dispatcher, "Delete");
        await Press(dispatcher, "Right");
        await Press(dispatcher, "5");

        Assert.Equal(["keep v1.mkv"], actions.Log);
    }
}
