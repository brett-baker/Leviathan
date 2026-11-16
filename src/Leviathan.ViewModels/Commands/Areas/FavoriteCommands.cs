// View models are UI-thread affine: the awaits in this file deliberately resume on the caller's context (crash signature
// 52a0ec04c34d, see WindowCommands.cs). UiThreadAffinityTests guards this.
#pragma warning disable CA2007

using Leviathan.Core.Paths;
using Leviathan.ViewModels.Navigation;
using Leviathan.ViewModels.Windows;

namespace Leviathan.ViewModels.Commands.Areas;

/// <summary>Adding, renaming and styling favorites.</summary>
internal sealed class FavoriteCommands(CommandScope scope) : CommandArea(scope)
{
    /// <summary>Registers this area's commands.</summary>
    public void Register()
    {
        var shell = Scope.Shell;
        var favorites = Scope.Favorites;

        Add(CommandIds.FavoriteAdd, "Add to favorites", "Favorites", (c, _) => Run(() =>
        {
            var location = LocationPath.Parse(c.Target!);
            if (favorites!.Contains(location))
            {
                shell.ShowMessage($"{location.Name} is already in Favorites");
            }
            else
            {
                favorites.Toggle(location);
                shell.ShowMessage("Added to Favorites");
            }
        }), c => favorites is not null && LocationPath.TryParse(c.Target, out _));

        Add(CommandIds.FavoriteRename, "Rename favorite", "Favorites", (c, _) => Run(() => favorites!.Rename(LocationPath.Parse(c.Target!), c.Parameter as string)),
            c => favorites is not null && LocationPath.TryParse(c.Target, out var l) && favorites.Contains(l));
        Add(CommandIds.FavoriteStyle, "Change favorite icon and colour", "Favorites", (c, _) => Run(() =>
        {
            var change = (Sidebar.FavoriteStyleChange)c.Parameter!;
            favorites!.SetStyle(LocationPath.Parse(c.Target!), change.Icon, change.Color);
        }), c => favorites is not null && c.Parameter is Sidebar.FavoriteStyleChange && LocationPath.TryParse(c.Target, out var l) && favorites.Contains(l));
        Add(CommandIds.FavoriteToggle, "Pin or unpin folder", "Favorites", (_, _) => Run(() =>
        {
            var pinned = favorites!.Toggle(Current()!.History.Current);
            shell.ShowMessage(pinned ? "Added to Favorites" : "Removed from Favorites");
        }), _ => favorites is not null && Current() is not null, shortcut: "Ctrl+D");
    }
}
