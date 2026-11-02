using CommunityToolkit.Mvvm.ComponentModel;
using Leviathan.Core.Providers;

namespace Leviathan.ViewModels.Views;

/// <summary>
/// View choices that belong to the user rather than to a folder: whether hidden and system items are listed (spec §6.3,
/// §17.2 Files and folders). Shared by every tab and window, so one switch in the View menu applies everywhere; a tab
/// picks the change up the next time it lists a folder.
/// </summary>
public sealed partial class ViewOptions : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Listing))]
    private bool _showHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Listing))]
    private bool _showSystem;

    /// <summary>The listing options these choices mean.</summary>
    public ListingOptions Listing => new() { IncludeHidden = ShowHidden, IncludeSystem = ShowSystem };
}
