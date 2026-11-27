using Leviathan.Core.Model;
using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.Triage;

namespace Leviathan.ViewModels.Tests;

/// <summary>Shift+1 to Shift+9 discard with the reject reason in that position of the user's list; an empty position does nothing.</summary>
public sealed class TriageReasonKeysTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class Actions : ITriageActions
    {
        public Task<TriageActionResult> KeepAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<TriageActionResult> DiscardAsync(FileEntry file, CancellationToken cancellationToken) => Task.FromResult(new TriageActionResult(() => Task.CompletedTask));

        public Task<bool> ToggleTagAsync(FileEntry file, int tagNumber, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ClearTagsAsync(FileEntry file, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static FileEntry Video(string path)
    {
        var location = LocationPath.Parse(path);
        return new FileEntry(location.Name, location, FileEntryKind.File, 5000, T0, T0, FileAttributes.Normal);
    }

    private static async Task<(TriageSession Session, TriageDispatcher Dispatcher)> StartAsync(Func<int, string?> reasonAt)
    {
        var session = new TriageSession([Video(@"Z:\a.mp4"), Video(@"Z:\b.mp4")], new FakeVideoEngine(), new Actions(), new TriageOptions { StartOffset = 0 });
        await session.StartAsync();
        return (session, new TriageDispatcher(session, TriageCommands.DefaultKeyMap(), reasonAt));
    }

    private static string? Reasons(int n) => n switch { 1 => "short", 2 => "seen", _ => null };

    [Theory]
    [InlineData("Shift+1", "short")]
    [InlineData("Shift+2", "seen")]
    public async Task ShiftDigit_DiscardsWithThatReason(string gesture, string reason)
    {
        var (session, dispatcher) = await StartAsync(Reasons);

        var handled = await dispatcher.HandleAsync(KeyGesture.Parse(gesture));

        Assert.True(handled);
        Assert.Equal(TriageDecision.Discarded, session.Items[0].Decision);
        Assert.Equal(reason, session.Items[0].RejectReasonId);
    }

    [Fact]
    public async Task ShiftDigit_WithNoReasonThere_DiscardsNothing()
    {
        var (session, dispatcher) = await StartAsync(Reasons);

        await dispatcher.HandleAsync(KeyGesture.Parse("Shift+5"));

        Assert.Equal(TriageDecision.None, session.Items[0].Decision);
    }

    [Fact]
    public async Task ShiftDigit_WithNoReasonListAtAll_DiscardsNothing()
    {
        var (session, dispatcher) = await StartAsync(_ => null);

        await dispatcher.HandleAsync(KeyGesture.Parse("Shift+1"));

        Assert.Equal(TriageDecision.None, session.Items[0].Decision);
    }

    [Fact]
    public async Task PlainDigits_StillToggleTags_AndPlainDiscardHasNoReason()
    {
        var (session, dispatcher) = await StartAsync(Reasons);

        await dispatcher.HandleAsync(KeyGesture.Parse("1"));
        Assert.Contains(1, session.Items[0].Tags);
        Assert.Equal(TriageDecision.None, session.Items[0].Decision);

        await dispatcher.HandleAsync(KeyGesture.Parse("X"));
        Assert.Equal(TriageDecision.Discarded, session.Items[0].Decision);
        Assert.Null(session.Items[0].RejectReasonId);
    }

    [Fact]
    public void TheNineReasonCommands_AreListedWithTheirKeys()
    {
        var rows = TriageCommands.Rows(TriageCommands.DefaultKeyMap()).Where(r => r.Command.Value.StartsWith("triageMode.discardReason", StringComparison.Ordinal)).ToList();

        Assert.Equal(9, rows.Count);
        Assert.Equal("Shift+1", rows[0].Keys);
        Assert.Equal("Shift+9", rows[8].Keys);
    }
}
