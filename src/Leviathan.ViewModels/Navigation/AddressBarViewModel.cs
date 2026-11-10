using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Paths;

namespace Leviathan.ViewModels.Navigation;

/// <summary>The breadcrumb split into what fits and what collapses into the "…" menu.</summary>
/// <param name="Collapsed">Leading segments hidden in the overflow menu, root first.</param>
/// <param name="Visible">Segments shown as buttons, the last being the current folder.</param>
public sealed record BreadcrumbLayout(IReadOnlyList<Breadcrumb> Collapsed, IReadOnlyList<Breadcrumb> Visible);

/// <summary>
/// State of the address bar (spec §5.2): breadcrumb mode by default, edit mode on Ctrl+L, Alt+D, a click on empty space or
/// typing. The control binds to this; nothing here touches the UI or the file system.
/// </summary>
public sealed partial class AddressBarViewModel : ObservableObject
{
    private LocationPath _location;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    /// <summary>Creates the bar showing <paramref name="location"/>.</summary>
    public AddressBarViewModel(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        _location = location;
        Breadcrumbs = BreadcrumbsOf(location);
    }

    // A search result folder has no folders to climb: one crumb says what was searched for (spec §13).
    private static IReadOnlyList<Breadcrumb> BreadcrumbsOf(LocationPath location) =>
        Search.SearchLocations.IsSearchLocation(location) ? [new Breadcrumb(Search.SearchLocations.DescribeOf(location), location)] : location.GetBreadcrumbs();

    /// <summary>The folder shown.</summary>
    public LocationPath Location
    {
        get => _location;
        private set => SetProperty(ref _location, value);
    }

    /// <summary>Root-to-current segments.</summary>
    public IReadOnlyList<Breadcrumb> Breadcrumbs { get; private set; }

    /// <summary>Shows a new location and leaves edit mode.</summary>
    public void Show(LocationPath location)
    {
        ArgumentNullException.ThrowIfNull(location);
        Location = location;
        Breadcrumbs = BreadcrumbsOf(location);
        OnPropertyChanged(nameof(Breadcrumbs));
        IsEditing = false;
        EditText = string.Empty;
    }

    /// <summary>Enters edit mode with the full path selected by the control. <paramref name="typed"/> replaces the path when the user started typing.</summary>
    public void BeginEdit(string? typed = null)
    {
        EditText = typed ?? (Search.SearchLocations.IsSearchLocation(Location) ? Search.SearchLocations.DescribeOf(Location) : Location.Value);
        IsEditing = true;
    }

    /// <summary>Leaves edit mode without navigating (Escape, focus lost).</summary>
    public void CancelEdit()
    {
        IsEditing = false;
        EditText = string.Empty;
    }

    /// <summary>
    /// Decides which segments stay visible when only <paramref name="maxVisible"/> fit. The current folder is always visible;
    /// the leading segments go into the "…" menu (spec §5.2).
    /// </summary>
    public BreadcrumbLayout Layout(int maxVisible)
    {
        var visibleCount = Math.Clamp(maxVisible, 1, Breadcrumbs.Count);
        var collapsed = Breadcrumbs.Count - visibleCount;
        return new BreadcrumbLayout([.. Breadcrumbs.Take(collapsed)], [.. Breadcrumbs.Skip(collapsed)]);
    }

    /// <summary>The text for a "Copy as" menu item.</summary>
    public string CopyAs(CopyAsFormat format, IDriveMappings? mappings = null) => PathFormats.Format(Location, format, mappings);
}
