using CommunityToolkit.Mvvm.ComponentModel;

namespace Leviathan.ViewModels.Settings;

/// <summary>One page of the settings sheet (spec §17.2).</summary>
/// <param name="Id">Stable id, e.g. <c>files</c>.</param>
/// <param name="Title">Name in the page list.</param>
/// <param name="Summary">One line about what the page is for.</param>
/// <param name="IsAvailable">True when the page has settings that work; false for a page that only says what it will hold.</param>
/// <param name="Arrives">For a page not available yet: when it arrives, e.g. "Phase 2".</param>
/// <param name="Planned">For a page not available yet: what it will hold.</param>
public sealed record SettingsPageInfo(string Id, string Title, string Summary, bool IsAvailable, string? Arrives = null, IReadOnlyList<string>? Planned = null);

/// <summary>One setting findable by the sheet's search.</summary>
/// <param name="PageId">The page it lives on.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Keywords">Other words people look for it by.</param>
public sealed record SettingEntry(string PageId, string Title, params string[] Keywords);

/// <summary>The pages of the settings sheet and everything its search can find (spec §17.2, D36).</summary>
public static class SettingsCatalog
{
    /// <summary>The page the sheet opens on.</summary>
    public const string DefaultPage = "general";

    /// <summary>The pages in the order of the list. Pages not built yet say what they will hold, so nothing a user looks for seems to be missing.</summary>
    public static IReadOnlyList<SettingsPageInfo> Pages { get; } =
    [
        new("general", "General", "Startup and new tabs", true),
        new("appearance", "Appearance", "How the app looks", true),
        new("files", "Files and folders", "What the file list shows", true),
        new("tabs", "Tabs and windows", "How tabs behave", true),
        new("network", "Network", "Shares and connections", false, "Phase 2", ["Timeouts and retry policy", "Polling intervals", "Discovery on or off", "Offline cache size", "Saved connections"]),
        new("operations", "Operations", "Copying and moving", false, "Phase 2", ["How many jobs run per destination", "Verify policy", "Bandwidth limits and schedule", "What to do on a name conflict"]),
        new("delete", "Delete", "Deleting and the bin", false, "Phase 2", ["How long deleted items are kept", "Size limit of the bin", "Confirmations"]),
        new("media", "Media and preview", "Video and pictures", false, "Phase 3", ["Video engine", "Hardware decoding", "Where ffmpeg is", "Preview size limits", "Which triage tools are on", "Contact sheet grid"]),
        new("tags", "Tags", "Colour tags", false, "Phase 3", ["Tag names, colours and keys", "Write the Windows Tags property"]),
        new("search", "Search and index", "Finding files", false, "Phase 4", ["Indexer status", "Which NAS folders to index", "Index schedule"]),
        new("keyboard", "Keyboard", "Shortcuts of every command", true),
        new("advanced", "Advanced", "Diagnostics, caches and the settings file", true),
    ];

    /// <summary>Every setting the search knows, working or planned.</summary>
    public static IReadOnlyList<SettingEntry> Entries { get; } =
    [
        new("general", "What to open at startup", "startup", "last session", "profile", "restore"),
        new("general", "Where a new tab opens", "new tab", "home", "folder"),
        new("appearance", "Icon set", "icons", "fluent", "font awesome", "colour", "style"),
        new("files", "Show hidden files", "hidden", "dotfiles"),
        new("files", "Show system files", "system", "protected"),
        new("files", "Show folder sizes", "size", "folder size", "calculate"),
        new("tabs", "Closing the last tab", "last tab", "close", "home", "window"),
        new("keyboard", "Keyboard shortcuts", "keys", "hotkey", "remap", "rebind", "shortcut", "binding"),
        new("advanced", "Open the settings file", "json", "edit", "file"),
        new("advanced", "Diagnostics", "log", "crash", "worker", "stalls"),
        new("advanced", "Clear caches", "cache", "listings", "thumbnails", "history", "activity", "reset"),
        .. Pages.Where(p => p.Planned is not null).SelectMany(p => p.Planned!.Select(item => new SettingEntry(p.Id, item))),
    ];

    /// <summary>The page with <paramref name="id"/>, or the default page.</summary>
    public static SettingsPageInfo Page(string? id) =>
        Pages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal)) ?? Pages[0];

    /// <summary>
    /// The settings that match <paramref name="text"/> (every word of it must appear in the title, the keywords or the page name),
    /// working ones first.
    /// </summary>
    public static IReadOnlyList<SettingEntry> Search(string? text)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        return
        [
            .. Entries
                .Where(entry =>
                {
                    var haystack = $"{entry.Title} {string.Join(' ', entry.Keywords)} {Page(entry.PageId).Title}";
                    return words.All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
                })
                .OrderBy(entry => Page(entry.PageId).IsAvailable ? 0 : 1),
        ];
    }
}

/// <summary>
/// The state of the settings sheet of one window (spec §17.2, D36): open or closed, which page, expanded to the full window or not,
/// and the search text. It rises over about 70% of the window; Esc or a click outside closes it. No UI types, so the rules are
/// unit-tested.
/// </summary>
public sealed partial class SettingsSheetModel : ObservableObject
{
    /// <summary>Share of the window's height the sheet takes until expanded.</summary>
    public const double HeightFraction = 0.7;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _pageId = SettingsCatalog.DefaultPage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>What the search finds for <see cref="SearchText"/>.</summary>
    public IReadOnlyList<SettingEntry> Results => SettingsCatalog.Search(SearchText);

    /// <summary>True when the search box has text, so the results take the place of the page.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Opens the sheet on <paramref name="page"/> (the page last shown when null).</summary>
    public void Open(string? page = null)
    {
        if (page is not null)
        {
            PageId = SettingsCatalog.Page(page).Id;
        }

        SearchText = string.Empty;
        IsOpen = true;
    }

    /// <summary>Closes the sheet; the next opening starts at the page it was on, not expanded.</summary>
    public void Close()
    {
        IsOpen = false;
        IsExpanded = false;
        SearchText = string.Empty;
    }

    /// <summary>Esc or a click outside. With search text, the first Esc only clears it. Returns true when it did something.</summary>
    public bool Dismiss()
    {
        if (!IsOpen)
        {
            return false;
        }

        if (IsSearching)
        {
            SearchText = string.Empty;
            return true;
        }

        Close();
        return true;
    }

    /// <summary>Shows a page from a search result (or the page list) and leaves the search.</summary>
    public void ShowPage(string page)
    {
        PageId = SettingsCatalog.Page(page).Id;
        SearchText = string.Empty;
    }

    /// <summary>Opens when closed, closes when open (Ctrl+,).</summary>
    public void Toggle(string? page = null)
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open(page);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(IsSearching));
    }
}
