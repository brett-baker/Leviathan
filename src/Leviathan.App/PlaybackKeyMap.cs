using Leviathan.ViewModels.Playback;
using VirtualKey = Windows.System.VirtualKey;

namespace Leviathan.App;

/// <summary>
/// The playback keys of spec §10.2 (Space, jumps, Ctrl+1…9, Home/End, frame steps, [ ], J/K/L) as one table, so the player window and
/// the preview pane answer the same keys the same way. What each key does is decided by <see cref="PlaybackController"/>.
/// </summary>
internal static class PlaybackKeyMap
{
    /// <summary>The work for a key, or null when the key is not a playback key.</summary>
    public static Task? Handle(PlaybackController keys, VirtualKey key, bool ctrl, bool shift, bool alt) => key switch
    {
        VirtualKey.Space => keys.TogglePlayAsync(),
        VirtualKey.Right => keys.JumpAsync(Jump(ctrl, shift, alt)),
        VirtualKey.Left => keys.JumpAsync(-Jump(ctrl, shift, alt)),
        VirtualKey.Home => keys.HomeAsync(),
        VirtualKey.End => keys.EndAsync(),
        (VirtualKey)190 => keys.StepFrameAsync(forward: true),
        (VirtualKey)188 => keys.StepFrameAsync(forward: false),
        (VirtualKey)221 => keys.SpeedUpAsync(),
        (VirtualKey)219 => keys.SlowDownAsync(),
        VirtualKey.J => keys.SkimBackAsync(),
        VirtualKey.K => keys.PauseAsync(),
        VirtualKey.L => keys.SkimForwardAsync(),
        >= VirtualKey.Number1 and <= VirtualKey.Number9 when ctrl => keys.JumpToTenthAsync(key - VirtualKey.Number0),
        _ => null,
    };

    /// <summary>Plain arrows jump 30 s, Shift 1 min, Ctrl 5 min and Alt 5 s (spec §10.2).</summary>
    public static TimeSpan Jump(bool ctrl, bool shift, bool alt) =>
        alt ? TimeSpan.FromSeconds(5) : ctrl ? TimeSpan.FromMinutes(5) : shift ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(30);
}
