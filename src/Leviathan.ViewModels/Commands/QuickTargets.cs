using Leviathan.Core.Archives;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Commands;

/// <summary>Where the nine quick targets are kept (spec §15.6). The app backs it with the settings; tests with a list.</summary>
public interface IQuickTargets
{
    /// <summary>The nine slots, slot 1 first; an unset slot is an empty string.</summary>
    IReadOnlyList<string> Slots { get; }

    /// <summary>Puts <paramref name="folder"/> in slot <paramref name="slot"/> (1 to 9), or clears it when empty.</summary>
    Task SetAsync(int slot, string folder, CancellationToken cancellationToken);
}

/// <summary>
/// Quick move and copy targets (spec §15.6): up to nine preset destinations. Ctrl+Alt+1..9 moves the selection to the target, Ctrl+Alt+Shift+1..9
/// copies it, in every folder and in triage. The transfer is the normal queued one, so a target on another drive or a NAS is a verified,
/// resumable job. Pure helpers; the commands are in <see cref="WindowCommands"/>.
/// </summary>
public static class QuickTargets
{
    /// <summary>How many targets there are.</summary>
    public const int Count = 9;

    /// <summary>Exactly <see cref="Count"/> slots from whatever was stored: trimmed, shorter lists padded with empty slots, longer ones cut.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? stored)
    {
        var slots = new string[Count];
        var i = 0;
        foreach (var item in stored ?? [])
        {
            if (i == Count)
            {
                break;
            }

            slots[i++] = (item ?? string.Empty).Trim();
        }

        while (i < Count)
        {
            slots[i++] = string.Empty;
        }

        return slots;
    }

    /// <summary>The folder in slot <paramref name="slot"/> (1 to 9), or null when it is empty, not a usable location or inside an archive.</summary>
    public static LocationPath? FolderOf(IReadOnlyList<string> slots, int slot)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slot is < 1 or > Count || slot > slots.Count || string.IsNullOrWhiteSpace(slots[slot - 1]))
        {
            return null;
        }

        return LocationPath.TryParse(slots[slot - 1], out var folder) && !ArchiveKinds.IsInside(folder) ? folder : null;
    }

    /// <summary>The command id for "move to target N" (1 to 9).</summary>
    public static CommandId MoveId(int slot) => new($"quick.move.{slot}");

    /// <summary>The command id for "copy to target N".</summary>
    public static CommandId CopyId(int slot) => new($"quick.copy.{slot}");

    /// <summary>The command id for "make this folder target N".</summary>
    public static CommandId SetId(int slot) => new($"quick.set.{slot}");
}

/// <summary>The quick targets kept in <c>settings.json</c> (<see cref="Settings.FilesSettings.QuickTargets"/>), so they survive restarts and are changed without a restart (spec §17).</summary>
public sealed class SettingsQuickTargets(Settings.SettingsStore store) : IQuickTargets
{
    /// <inheritdoc />
    public IReadOnlyList<string> Slots => QuickTargets.Normalize(store.Current.Files.QuickTargets);

    /// <inheritdoc />
    public async Task SetAsync(int slot, string folder, CancellationToken cancellationToken)
    {
        if (slot is < 1 or > QuickTargets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        await store.UpdateAsync(s =>
        {
            var slots = QuickTargets.Normalize(s.Files.QuickTargets).ToArray();
            slots[slot - 1] = (folder ?? string.Empty).Trim();
            return s with { Files = s.Files with { QuickTargets = slots } };
        }, cancellationToken).ConfigureAwait(false);
    }
}
