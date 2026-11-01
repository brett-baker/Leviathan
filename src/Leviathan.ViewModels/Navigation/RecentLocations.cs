using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Text;

namespace Leviathan.ViewModels.Navigation;

/// <summary>
/// The folders visited recently, newest first, shared by every tab and window. It feeds the address bar's suggestions (spec §5.2:
/// "autocomplete from the file system, history and favorites") and the Ctrl+P palette (spec §5.3). Used from the UI thread.
/// </summary>
public sealed class RecentLocations
{
    /// <summary>Most locations kept.</summary>
    public const int Capacity = 100;

    private readonly List<LocationPath> _items = [];

    /// <summary>Records a visit: the location moves to the front, and the oldest falls off past <see cref="Capacity"/>.</summary>
    public void Add(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        _items.Remove(location);
        _items.Insert(0, location);
        if (_items.Count > Capacity)
        {
            _items.RemoveRange(Capacity, _items.Count - Capacity);
        }
    }

    /// <summary>Forgets a location, e.g. one that no longer exists.</summary>
    public bool Remove(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return _items.Remove(location);
    }

    /// <summary>The locations, newest first.</summary>
    public IReadOnlyList<LocationPath> Snapshot() => [.. _items];

    /// <summary>Replaces the list (loading a saved session), newest first.</summary>
    public void Load(IEnumerable<LocationPath> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        _items.Clear();
        foreach (var location in locations.Distinct().Take(Capacity))
        {
            _items.Add(location);
        }
    }
}

/// <summary>
/// The list behind a breadcrumb's chevron (spec §5.2): the subfolders of that segment, in natural order, narrowed as the user
/// types. Listing failures give an empty list so the menu simply shows "no folders" instead of an error dialog.
/// </summary>
public sealed class SubfolderMenu
{
    private readonly IFolderLister _folders;

    /// <summary>Creates the menu service.</summary>
    public SubfolderMenu(IFolderLister folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = folders;
    }

    /// <summary>Lists the folders directly below <paramref name="location"/>, sorted naturally.</summary>
    public async Task<IReadOnlyList<LocationPath>> LoadAsync(LocationPath location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var folders = await _folders.ListFoldersAsync(location, cancellationToken).ConfigureAwait(false);
        return [.. folders.OrderBy(f => f.Name, NaturalStringComparer.Instance)];
    }

    /// <summary>
    /// Narrows <paramref name="folders"/> to those whose name contains <paramref name="text"/> (any case); names that start with
    /// it come first. Empty text keeps everything.
    /// </summary>
    public static IReadOnlyList<LocationPath> Filter(IReadOnlyList<LocationPath> folders, string text)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (string.IsNullOrWhiteSpace(text))
        {
            return folders;
        }

        var needle = text.Trim();
        return [.. folders
            .Where(f => f.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
    }
}
