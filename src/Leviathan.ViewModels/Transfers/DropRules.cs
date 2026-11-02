using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Transfers;

/// <summary>What a drop does with the dragged items (spec §8.4).</summary>
public enum TransferMode
{
    /// <summary>Put the items in the destination and remove them from where they were.</summary>
    Move,

    /// <summary>Put copies of the items in the destination.</summary>
    Copy,

    /// <summary>Create shortcuts to the items in the destination.</summary>
    Link,
}

/// <summary>The modifier keys held while dragging. Shift forces move, Ctrl forces copy, Alt creates a shortcut (spec §8.4).</summary>
[Flags]
public enum DropModifiers
{
    /// <summary>No modifier key.</summary>
    None = 0,

    /// <summary>Shift is held.</summary>
    Shift = 1,

    /// <summary>Ctrl is held.</summary>
    Ctrl = 2,

    /// <summary>Alt is held.</summary>
    Alt = 4,
}

/// <summary>What would happen if the user let go now: whether the drop is allowed, which operation it is, and the tooltip that says so.</summary>
/// <param name="Allowed">False when the drop must be refused.</param>
/// <param name="Mode">The operation. Meaningless when <paramref name="Allowed"/> is false.</param>
/// <param name="Tooltip">Text shown next to the pointer: the operation, or why the drop is refused.</param>
public sealed record DropDecision(bool Allowed, TransferMode Mode, string Tooltip);

/// <summary>
/// Decides what dropping items on a folder does (spec §8.4: move on the same volume, copy across volumes; Shift forces move, Ctrl
/// forces copy, Alt creates a shortcut; "a tooltip always says which one will happen"). Pure, so every rule is unit-tested and the
/// tab strip, the tree, the breadcrumb and the file views all answer the same way.
/// </summary>
public static class DropRules
{
    /// <summary>
    /// Decides the drop of <paramref name="sources"/> onto the folder <paramref name="destination"/>. Pass null for
    /// <paramref name="sources"/> when the items are not known yet (a drag from another application is only readable once it is
    /// dropped); the decision then assumes they come from another volume.
    /// </summary>
    public static DropDecision Decide(IReadOnlyList<LocationPath>? sources, LocationPath destination, DropModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination.IsNetwork && destination.IsRoot && destination.Kind == LocationKind.Unc && destination.Share is null)
        {
            return Refuse("Open a share to drop into it");
        }

        if (destination.Kind is not (LocationKind.Drive or LocationKind.Unc))
        {
            return Refuse($"Cannot drop into {destination.Name} yet");
        }

        var known = sources is { Count: > 0 };
        if (known)
        {
            foreach (var source in sources!)
            {
                if (source.Contains(destination))
                {
                    return Refuse(source.Equals(destination)
                        ? $"Cannot drop {source.Name} onto itself"
                        : $"Cannot drop {source.Name} into a folder inside it");
                }
            }
        }

        var mode = ModeFor(sources, destination, modifiers);
        var alreadyThere = known && sources!.All(s => s.Parent is { } parent && parent.Equals(destination));
        if (mode == TransferMode.Move && alreadyThere)
        {
            return Refuse(sources!.Count == 1 ? $"{sources[0].Name} is already in {destination.Name}" : $"These items are already in {destination.Name}");
        }

        return new DropDecision(true, mode, Describe(mode, sources, destination, CrossesVolumes(sources, destination)));
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are on the same volume, so a move is a rename that copies nothing.</summary>
    public static bool SameVolume(LocationPath a, LocationPath b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Kind == b.Kind && string.Equals(a.Root, b.Root, StringComparison.OrdinalIgnoreCase);
    }

    private static TransferMode ModeFor(IReadOnlyList<LocationPath>? sources, LocationPath destination, DropModifiers modifiers)
    {
        if (modifiers.HasFlag(DropModifiers.Alt))
        {
            return TransferMode.Link;
        }

        if (modifiers.HasFlag(DropModifiers.Ctrl))
        {
            return TransferMode.Copy;
        }

        if (modifiers.HasFlag(DropModifiers.Shift))
        {
            return TransferMode.Move;
        }

        return CrossesVolumes(sources, destination) ? TransferMode.Copy : TransferMode.Move;
    }

    // Unknown items count as coming from elsewhere: copying never loses anything, moving a stranger's files might.
    private static bool CrossesVolumes(IReadOnlyList<LocationPath>? sources, LocationPath destination) =>
        sources is not { Count: > 0 } || sources.Any(s => !SameVolume(s, destination));

    private static string Describe(TransferMode mode, IReadOnlyList<LocationPath>? sources, LocationPath destination, bool crossesVolumes)
    {
        var what = sources is not { Count: > 0 } ? string.Empty : sources.Count == 1 ? $" {sources[0].Name}" : $" {sources.Count} items";
        var where = destination.Name;
        return mode switch
        {
            TransferMode.Copy => $"Copy{what} to {where}",
            TransferMode.Link => $"Create shortcut{(sources is { Count: > 1 } ? "s" : string.Empty)}{(what.Length > 0 ? " to" + what : string.Empty)} in {where}",
            _ when crossesVolumes => $"Move{what} to {where} (copied, checked, then removed from the original)",
            _ => $"Move{what} to {where}",
        };
    }

    private static DropDecision Refuse(string reason) => new(false, TransferMode.Copy, reason);
}
