using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>A pinned folder (spec §7, Favorites).</summary>
/// <param name="Location">The folder.</param>
/// <param name="Name">The name the user gave it, or null for the folder's own name.</param>
public sealed record FavoriteEntry(LocationPath Location, string? Name = null);

/// <summary>Where the favorites are kept between runs. Writes must be atomic (spec §17).</summary>
public interface IFavoritesStorage
{
    /// <summary>Reads the saved favorites; a missing or damaged file gives an empty list.</summary>
    IReadOnlyList<FavoriteEntry> Load();

    /// <summary>Saves the favorites.</summary>
    void Save(IReadOnlyList<FavoriteEntry> favorites);
}

/// <summary>The pinned folders: Ctrl+D pins or unpins the current folder (spec §7). Used from the UI thread.</summary>
public sealed class FavoritesList
{
    private readonly IFavoritesStorage _storage;
    private readonly List<FavoriteEntry> _items;

    /// <summary>Creates the list from what <paramref name="storage"/> holds.</summary>
    public FavoritesList(IFavoritesStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _items = [.. storage.Load()];
    }

    /// <summary>Raised after the list changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The pinned folders in order.</summary>
    public IReadOnlyList<FavoriteEntry> Items => _items;

    /// <summary>True when <paramref name="location"/> is pinned.</summary>
    public bool Contains(LocationPath location) => _items.Exists(f => f.Location == location);

    /// <summary>Pins the folder if it is not, unpins it if it is. Returns true when it is pinned afterwards.</summary>
    public bool Toggle(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var index = _items.FindIndex(f => f.Location == location);
        if (index >= 0)
        {
            _items.RemoveAt(index);
        }
        else
        {
            _items.Add(new FavoriteEntry(location));
        }

        Persist();
        return index < 0;
    }

    /// <summary>Gives a favorite its own name, or null to go back to the folder's name.</summary>
    public void Rename(LocationPath location, string? name)
    {
        var index = _items.FindIndex(f => f.Location == location);
        if (index >= 0)
        {
            _items[index] = _items[index] with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            _storage.Save([.. _items]);
        }
        catch (IOException)
        {
            // The pin still works for this run; it is just not remembered.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Builds the sections of the sidebar in their default order (spec §7).</summary>
public static class SidebarSections
{
    /// <summary>
    /// Favorites, Common folders, Tags, Local, Network and Internet. Mapped network drives are listed under Network only (decision
    /// in docs/decisions.md); Internet is left out when no sync client is installed.
    /// </summary>
    public static IReadOnlyList<SidebarSection> Build(
        IEnumerable<FavoriteEntry> favorites,
        IEnumerable<SidebarPlace> common,
        IEnumerable<DriveDetails> drives,
        IEnumerable<CloudProviderInfo> cloud)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(common);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(cloud);
        var driveList = drives.ToList();
        return
        [
            new SidebarSection("Favorites", [.. favorites.Select(f => new SidebarPlace(f.Name ?? f.Location.Name, f.Location, SidebarIcon.Favorite, f.Location.Value))], "Press Ctrl+D to pin a folder"),
            new SidebarSection("Common folders", [.. common]),
            new SidebarSection("Tags", [], "Tags arrive in Phase 3"),
            new SidebarSection("Local", [.. driveList.Where(d => d.Kind != DriveKind.Network).Select(DrivePlace)]),
            new SidebarSection("Network", [.. driveList.Where(d => d.Kind == DriveKind.Network).Select(DrivePlace)], "No network locations yet"),
            new SidebarSection("Internet", [.. cloud.Select(c => new SidebarPlace(c.Name, c.SyncFolder, SidebarIcon.Cloud))]),
        ];
    }

    private static SidebarPlace DrivePlace(DriveDetails drive)
    {
        var detail = !drive.IsResponsive
            ? "Not responding"
            : drive.Kind == DriveKind.Network && drive.NetworkPath is not null
                ? drive.NetworkPath
                : drive.HasSizes ? $"{DisplayFormat.Size(drive.FreeBytes)} free of {DisplayFormat.Size(drive.TotalBytes)}" : string.Empty;
        var icon = drive.Kind switch
        {
            DriveKind.Network => SidebarIcon.NetworkDrive,
            DriveKind.Removable or DriveKind.Optical => SidebarIcon.Removable,
            _ => SidebarIcon.Drive,
        };
        return new SidebarPlace(drive.DisplayName, drive.Root, icon, detail, drive.HasSizes ? drive.UsedFraction : null, drive.IsLowOnSpace);
    }
}

/// <summary><see cref="ISubfolderSource"/> over the location providers. Unlike autocomplete it lets failures through, so the tree can say why a node did not open.</summary>
public sealed class ProviderSubfolderSource : ISubfolderSource
{
    private readonly Core.Providers.LocationProviderRegistry _providers;

    /// <summary>Creates a source over <paramref name="providers"/>.</summary>
    public ProviderSubfolderSource(Core.Providers.LocationProviderRegistry providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocationPath>> ListFoldersAsync(LocationPath location, CancellationToken cancellationToken)
    {
        var folders = new List<LocationPath>();
        var provider = _providers.Resolve(location);
        await foreach (var batch in provider.ListAsync(location, Core.Providers.ListingOptions.Default, cancellationToken).ConfigureAwait(false))
        {
            folders.AddRange(batch.Items.Where(i => Folders.FileColumns.IsFolderLike(i) && !i.IsHidden).Select(i => i.Location));
        }

        return folders;
    }
}
