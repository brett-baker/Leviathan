using CommunityToolkit.Mvvm.ComponentModel;

namespace Leviathan.ViewModels.Settings;

/// <summary>One page of the settings sheet (spec §17.2).</summary>
/// <param name="Id">Stable id, e.g. <c>files</c>.</param>
/// <param name="Title">Name in the page list.</param>
/// <param name="Summary">One line about what the page is for.</param>
/// <param name="IsAvailable">True when the page has settings that work; false for a page that only says what it will hold.</param>
/// <param name="Arrives">For a page not available yet: when it arrives, e.g. "Phase 2".</param>
/// <param name="Planned">For a page not available yet: what it will hold.</param>
/// <param name="Glyph">Segoe Fluent icon shown beside the page name in the list.</param>
public sealed record SettingsPageInfo(string Id, string Title, string Summary, bool IsAvailable, string? Arrives = null, IReadOnlyList<string>? Planned = null, string Glyph = "");

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
        new("general", "General", "Startup and new tabs", true, Glyph: ""),
        new("appearance", "Appearance", "How the app looks", true, Glyph: ""),
        new("files", "Files and folders", "What the file list shows", true, Glyph: ""),
        new("tabs", "Tabs and windows", "How tabs behave", true, Glyph: ""),
        new("network", "Network", "Shares and connections", true, Glyph: ""),
        new("operations", "Operations", "Copying and moving", true, Glyph: ""),
        new("delete", "Delete", "Deleting and the bin", true, Glyph: ""),
        new("media", "Media and preview", "Video and pictures", true, Glyph: ""),
        new("triage", "Triage", "Keeping and discarding videos", true, Glyph: ""),
        new("tags", "Tags", "Colour tags", true, Glyph: ""),
        new("search", "Search and index", "Finding files", true, Glyph: ""),
        new("keyboard", "Keyboard", "Shortcuts of every command", true, Glyph: ""),
        new("advanced", "Advanced", "Diagnostics, caches and the settings file", true, Glyph: ""),
    ];

    /// <summary>Every setting the search knows, working or planned.</summary>
    public static IReadOnlyList<SettingEntry> Entries { get; } =
    [
        new("general", "What to open at startup", "startup", "last session", "profile", "restore"),
        new("general", "Where a new tab opens", "new tab", "home", "folder"),
        new("general", "Open items with a single click", "single click", "double click", "open"),
        new("general", "Terminal that Open terminal here uses", "terminal", "powershell", "cmd", "wsl", "windows terminal", "shell"),
        new("appearance", "Theme", "dark", "light", "system"),
        new("appearance", "Density", "compact", "comfortable", "rows", "spacing"),
        new("appearance", "Accent colour", "accent", "colour", "color"),
        new("appearance", "Window backdrop (Mica)", "mica", "transparent", "wallpaper"),
        new("appearance", "Icon size", "icons", "small", "large"),
        new("appearance", "Icon set", "icons", "fluent", "font awesome", "colour", "style"),
        new("files", "Show hidden files", "hidden", "dotfiles"),
        new("files", "Show system files", "system", "protected"),
        new("files", "Show folder sizes", "size", "folder size", "calculate"),
        new("files", "Show deletes, moves and copies at once", "immediate", "eager", "update", "list"),
        new("files", "Git status in repositories", "git", "branch", "repository", "column"),
        new("files", "Quick move and copy targets", "quick", "target", "preset", "destination", "ctrl alt"),
        new("files", "Count drive sizes again every day", "recheck", "folder size", "mapped drive", "stale"),
        new("tabs", "Closing the last tab", "last tab", "close", "home", "window"),
        new("tabs", "Ctrl+Tab goes to the most recent tab", "ctrl tab", "mru", "order", "recent"),
        new("network", "Folder sizes on network drives", "size", "count", "nas", "share", "slow"),
        new("network", "Thumbnails on network drives", "thumbnail", "picture", "nas", "share", "slow"),
        new("operations", "How many jobs run at once", "parallel", "jobs", "operations", "copies", "concurrency"),
        new("operations", "Jobs at once on one drive or share", "per device", "per destination", "parallel", "drive"),
        new("operations", "Background jobs at once", "background", "thumbnails", "sizes", "parallel"),
        new("operations", "Check copies against their sources", "verify", "checksum", "hash", "integrity"),
        new("operations", "Bandwidth limits and schedule", "bandwidth", "speed", "limit", "throttle", "night", "mb"),
        new("delete", "How long deleted items are kept", "bin", "retention", "days", "purge", "soft delete"),
        new("delete", "Size limit of the bin", "bin", "gib", "purge", "soft delete", "size"),
        new("delete", "Hidden recycle folder on shares", "trash", "recycle", "share", "nas", "undo"),
        new("triage", "When kept files move", "triage", "keep", "move", "end"),
        new("triage", "Ask before moving kept files", "triage", "confirm"),
        new("triage", "Where each video starts", "triage", "offset", "intro", "start"),
        new("triage", "Go to the next file after a decision", "triage", "advance", "delay", "auto"),
        new("triage", "Folder that Keep also moves to", "triage", "keep folder", "destination"),
        new("triage", "Skim playback bursts", "skim", "burst", "interval", "seconds", "preview"),
        new("media", "Scrub videos by hovering", "hover", "scrub", "thumbnail", "tiles"),
        new("tags", "Tag names and colours", "colour tags", "rename", "recolour", "label"),
        new("tags", "Tag order and keys 1 to 9", "reorder", "shortcut", "move"),
        new("tags", "Tint the rows of tagged files", "row colour", "highlight", "wash"),
        new("search", "Folders to index", "index", "nas", "search", "crawl", "add"),
        new("search", "Indexer status", "index", "pause", "crawl", "progress", "now"),
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
