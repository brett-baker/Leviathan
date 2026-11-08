using Leviathan.ViewModels.Commands;

namespace Leviathan.ViewModels.Triage;

/// <summary>
/// Turns a pressed key into what triage does (spec §10.2). The key map says which command a gesture is; this runs the command: the
/// decisions and navigation go to the <see cref="TriageSession"/>, the playback keys to its player, and the commands that need the
/// window (contact sheet, rename, move to a preset, overlay, skim, leave) are raised as <see cref="WindowCommand"/> for the window to
/// carry out, because they need dialogs and pictures this class knows nothing about. Mouse buttons in the window call
/// <see cref="RunAsync"/> with the same ids, so a click and its key always do the same.
/// </summary>
public sealed class TriageDispatcher(TriageSession session, KeyMap map)
{
    private static readonly HashSet<string> WindowCommands = new(StringComparer.Ordinal)
    {
        TriageCommands.ContactSheet.Value,
        TriageCommands.Rename.Value,
        TriageCommands.MoveToPreset.Value,
        TriageCommands.ToggleOverlay.Value,
        TriageCommands.ToggleSkim.Value,
        TriageCommands.Exit.Value,
    };

    /// <summary>The session the keys drive.</summary>
    public TriageSession Session => session;

    /// <summary>Raised on the calling thread for a command only the window can carry out.</summary>
    public event EventHandler<CommandId>? WindowCommand;

    /// <summary>The command a gesture is bound to, or null; lets the window decide whether to swallow the key before running it.</summary>
    public CommandId? Resolve(KeyGesture gesture) => map.Resolve(gesture, isSplit: false);

    /// <summary>Runs the command of <paramref name="gesture"/>; false when the key is not bound, so the window can leave it to others.</summary>
    public async Task<bool> HandleAsync(KeyGesture gesture) =>
        Resolve(gesture) is { } command && await RunAsync(command).ConfigureAwait(false);

    /// <summary>Runs <paramref name="command"/>; false when it is not a triage command.</summary>
    public async Task<bool> RunAsync(CommandId command)
    {
        if (!TriageCommands.IsKnown(command))
        {
            return false;
        }

        if (WindowCommands.Contains(command.Value))
        {
            WindowCommand?.Invoke(this, command);
            return true;
        }

        try
        {
            await RunSessionCommandAsync(command).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The file changed under a seek: the key had nothing left to do.
        }

        return true;
    }

    private Task RunSessionCommandAsync(CommandId command)
    {
        var keys = session.Keys;
        var id = command.Value;
        if (TryNumber(id, "triageMode.tag", out var tag))
        {
            return session.ToggleTagAsync(tag);
        }

        if (TryNumber(id, "triageMode.jump", out var percent))
        {
            return keys.JumpToTenthAsync(percent / 10);
        }

        return id switch
        {
            "triageMode.keep" => session.KeepAsync(),
            "triageMode.discard" => session.DiscardAsync(),
            "triageMode.skip" => session.SkipAsync(),
            "triageMode.previous" => session.PreviousAsync(),
            "triageMode.undo" => session.UndoAsync(),
            "triageMode.clearTags" => session.ClearTagsAsync(),
            "triageMode.playPause" => keys.TogglePlayAsync(),
            "triageMode.forward30" => keys.JumpAsync(TimeSpan.FromSeconds(30)),
            "triageMode.back30" => keys.JumpAsync(-TimeSpan.FromSeconds(30)),
            "triageMode.forward1m" => keys.JumpAsync(TimeSpan.FromMinutes(1)),
            "triageMode.back1m" => keys.JumpAsync(-TimeSpan.FromMinutes(1)),
            "triageMode.forward5m" => keys.JumpAsync(TimeSpan.FromMinutes(5)),
            "triageMode.back5m" => keys.JumpAsync(-TimeSpan.FromMinutes(5)),
            "triageMode.forward5s" => keys.JumpAsync(TimeSpan.FromSeconds(5)),
            "triageMode.back5s" => keys.JumpAsync(-TimeSpan.FromSeconds(5)),
            "triageMode.frameNext" => keys.StepFrameAsync(forward: true),
            "triageMode.framePrevious" => keys.StepFrameAsync(forward: false),
            "triageMode.start" => keys.HomeAsync(),
            "triageMode.end" => keys.EndAsync(),
            "triageMode.rewind" => keys.SkimBackAsync(),
            "triageMode.pause" => keys.PauseAsync(),
            "triageMode.fastForward" => keys.SkimForwardAsync(),
            "triageMode.speedDown" => keys.SlowDownAsync(),
            "triageMode.speedUp" => keys.SpeedUpAsync(),
            _ => Task.CompletedTask,
        };
    }

    private static bool TryNumber(string id, string prefix, out int number)
    {
        number = 0;
        return id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(prefix.Length), out number);
    }
}
