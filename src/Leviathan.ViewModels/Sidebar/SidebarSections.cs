using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Sidebar;

/// <summary>A pinned folder (spec §7, Favorites).</summary>
/// <param name="Location">The folder.</param>
/// <param name="Name">The name the user gave it, or null for the folder's own name.</param>
/// <param name="Icon">Key of the icon the user chose (see <see cref="FavoriteStyles"/>), or null for the star.</param>
/// <param name="Color">Colour the user chose as <c>#RRGGBB</c>, or null for the default.</param>
public sealed record FavoriteEntry(LocationPath Location, string? Name = null, string? Icon = null, string? Color = null);

/// <summary>A change to how a favorite looks: both parts are applied, null meaning "back to the default".</summary>
/// <param name="Icon">Icon key from <see cref="FavoriteStyles.Icons"/>.</param>
/// <param name="Color">Colour as <c>#RRGGBB</c>.</param>
public sealed record FavoriteStyleChange(string? Icon, string? Color);

/// <summary>The icons and colours a favorite can wear (spec §5.2: "right-click offers rename, icon and colour for the favorite").</summary>
public static class FavoriteStyles
{
    /// <summary>Icons by key: key, name shown in the menu, Segoe Fluent Icons character.</summary>
    public static IReadOnlyList<(string Key, string Title, string Glyph)> Icons { get; } =
    [
        ("star", "Star", "\uE734"),
        ("folder", "Folder", "\uE8B7"),
        ("heart", "Heart", "\uEB51"),
        ("flag", "Flag", "\uE7C1"),
        ("pin", "Pin", "\uE718"),
        ("work", "Work", "\uE821"),
        ("home", "Home", "\uE80F"),
        ("code", "Code", "\uE943"),
        ("video", "Video", "\uE714"),
        ("photo", "Photos", "\uEB9F"),
        ("music", "Music", "\uE8D6"),
        ("download", "Download", "\uE896"),
    ];

    /// <summary>Colours: name shown in the menu and <c>#RRGGBB</c>.</summary>
    public static IReadOnlyList<(string Title, string Hex)> Colors { get; } =
    [
        ("Red", "#E74856"),
        ("Orange", "#F7630C"),
        ("Yellow", "#FFB900"),
        ("Green", "#16C60C"),
        ("Teal", "#00B7C3"),
        ("Blue", "#0078D4"),
        ("Purple", "#886CE4"),
        ("Pink", "#E3008C"),
        ("Grey", "#8A8A8A"),
    ];

    /// <summary>The glyph of icon <paramref name="key"/>, or null when there is no such icon.</summary>
    public static string? GlyphOf(string? key) => Icons.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal)).Glyph;

    /// <summary>True for <c>#RRGGBB</c>.</summary>
    public static bool IsColor(string? text) => text is { Length: 7 } && text[0] == '#' && text.AsSpan(1).ContainsAnyExcept(HexDigits) is false;

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
}

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

    /// <summary>
    /// Sets how a favorite looks: an icon key from <see cref="FavoriteStyles.Icons"/> and a colour as <c>#RRGGBB</c>; null for either
    /// goes back to the default. An unknown key or a malformed colour is ignored rather than stored.
    /// </summary>
    public void SetStyle(LocationPath location, string? icon, string? color)
    {
        var index = _items.FindIndex(f => f.Location == location);
        if (index < 0)
        {
            return;
        }

        _items[index] = _items[index] with
        {
            Icon = FavoriteStyles.GlyphOf(icon) is null ? null : icon,
            Color = FavoriteStyles.IsColor(color) ? color!.ToUpperInvariant() : null,
        };
        Persist();
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
        IEnumerable<CloudProviderInfo> cloud,
        IEnumerable<SavedConnection>? servers = null,
        IEnumerable<NearbyDevice>? nearby = null,
        bool searching = false)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(common);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(cloud);
        var driveList = drives.ToList();
        var saved = (servers ?? []).ToList();
        return
        [
            new SidebarSection("Favorites", [.. favorites.Select(f => new SidebarPlace(f.Name ?? f.Location.Name, f.Location, SidebarIcon.Favorite, f.Location.Value) { Glyph = FavoriteStyles.GlyphOf(f.Icon), Accent = f.Color })], "Press Ctrl+D to pin a folder"),
            new SidebarSection("Common folders", [.. common]),
            new SidebarSection("Tags", [], "Tags arrive in Phase 3"),
            new SidebarSection("Local", [.. driveList.Where(d => d.Kind != DriveKind.Network).Select(DrivePlace)]),
            new SidebarSection(
                "Network",
                [.. saved.Select(ServerPlace), .. driveList.Where(d => d.Kind == DriveKind.Network).Select(DrivePlace)],
                "No network locations yet")
            {
                Groups = [NearbyGroup(nearby, saved, searching)],
                ActionTitle = "Add connection…",
            },
            new SidebarSection("Internet", [.. cloud.Select(c => new SidebarPlace(c.Name, c.SyncFolder, SidebarIcon.Cloud))]),
        ];
    }

    /// <summary>The title of the heading for devices found on the network.</summary>
    public const string NearbyTitle = "Nearby";

    /// <summary>The Nearby group (spec §7.1): devices found by discovery, minus the ones already saved. Says "Searching…" until something turns up.</summary>
    public static SidebarGroup NearbyGroup(IEnumerable<NearbyDevice>? devices, IEnumerable<SavedConnection> saved, bool searching)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var known = saved.Select(s => SavedConnectionList.Normalize(s.Host)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var places = (devices ?? [])
            .Where(d => SavedConnectionList.IsValidHost(d.Host) && !known.Contains(SavedConnectionList.Normalize(d.Host)))
            .DistinctBy(d => SavedConnectionList.Normalize(d.Host), StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .Select(d => new SidebarPlace(d.Title, LocationPath.Parse(@"\\" + SavedConnectionList.Normalize(d.Host)), SidebarIcon.Server, d.Host) { Server = SavedConnectionList.Normalize(d.Host) })
            .ToList();
        return new SidebarGroup(NearbyTitle, places, searching ? "Searching…" : "No devices found");
    }

    private static SidebarPlace ServerPlace(SavedConnection server) =>
        new(server.Title, server.Location, SidebarIcon.Server, server.Title == server.Host ? string.Empty : server.Host)
        {
            Server = server.Host,
            IsSaved = true,
        };

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
